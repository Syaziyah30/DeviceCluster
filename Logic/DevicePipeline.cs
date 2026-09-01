using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Model.ModelRequest;
using Model.ModelResult;
using Model.Services;
using Logic.Models;

namespace Logic
{
	// The full automated device-clustering pipeline: SQL load → predict device type →
	// predict section/cluster → quota allocation → floating split/dump → cluster groups.
	// Ships with Logic.dll so any caller (console app, UI, service) gets one method instead
	// of having to re-derive the step sequence from Program.cs.
	//
	// Deliberately excludes the interactive manual-correction loop — that's a human-in-the-loop
	// concern, not part of the automated run. Callers invoke LogicAssignment's existing
	// AssignByNumericSimilarity / PlaceDevice / MarkAsAssigned on top of this method's result
	// when a correction is needed.
	//
	// Progress is reported via DevicePipelineCallbacks, not Console — the pipeline itself
	// never prints. Callers decide how (or whether) to present each stage.
	public static class DevicePipeline
	{
		private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

		// client decides where predictions come from: PythonClient runs the scripts
		// locally, HttpPredictionClient calls the ML service. Everything else in
		// this method is identical either way, which is the point of the interface.
		public static async Task<DevicePipelineResult> RunAsync(
			PythonSQL sqlReader,
			IPredictionClient client,
			LogicAssignment logic,
			string sqlSourceTable,
			string sqlQuotaTable,
			string sqlOutputDir,
			string projectCode,
			DevicePipelineCallbacks? callbacks = null)
		{
			callbacks ??= new DevicePipelineCallbacks();

			// ── STEP 1: SQL reads device IDs ──────────────────────────────────────────
			var (requestJson, sqlOutputJson) = await sqlReader.LoadProjectDataAsync(sqlSourceTable, projectCode, sqlOutputDir);
			var request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, JsonOpts);

			if (request == null || request.data_ids == null || request.data_ids.Count == 0)
				throw new InvalidOperationException($"No project data found in '{sqlSourceTable}' table.");

			request.data_ids = request.data_ids
				.Select(id => id.Replace("﻿", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			callbacks.OnProjectLoaded?.Invoke(request, sqlOutputJson);

			// ── STEP 2: Predict device type ───────────────────────────────────────────
			var sw = Stopwatch.StartNew();
			string typeJson = await client.PredictDeviceTypeAsync(request);
			sw.Stop();

			var typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, JsonOpts) ?? new();
			var deviceTypeLookup = typeResults
				.GroupBy(r => r.data_id)
				.ToDictionary(g => g.Key, g => g.Last().data_type ?? "N/A");

			callbacks.OnDeviceTypesPredicted?.Invoke(typeResults, sw.Elapsed.TotalSeconds);

			// ── STEP 3: Predict section + cluster ─────────────────────────────────────
			var pipelineRequest = new PipelinePredictRequest
			{
				records = typeResults.Select(r => new PipelineRecord
				{
					device_id = r.data_id,
					customer = r.customer ?? request.customer_code,
					project = request.project_code
				}).ToList()
			};

			sw.Restart();
			string pipelineJson = await client.PredictSectionClusterAsync(pipelineRequest);
			sw.Stop();
			double step3Secs = sw.Elapsed.TotalSeconds;

			var pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, JsonOpts) ?? new();

			callbacks.OnSectionsPredicted?.Invoke(pipelineResults, deviceTypeLookup, step3Secs);
			callbacks.OnClustersPredicted?.Invoke(pipelineResults, deviceTypeLookup, step3Secs);

			// ── STEP 3.5: Quota allocation ─────────────────────────────────────────────
			var predictions = pipelineResults.Select(r => new DevicePrediction
			{
				Section = r.PREDICTED_SECTION ?? "UNKNOWN",
				Cluster = r.PREDICTED_CLUSTER ?? "UNKNOWN",
				DeviceId = r.DEVICE_ID,
				DeviceType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var predDt) ? predDt : "UNKNOWN",
				Score = r.CLUSTER_CONFIDENCE ?? 0,
				TopClusters = (r.TOP_CLUSTERS ?? new List<ClusterCandidate>())
					.Select(c => new ClusterPrediction { Cluster = c.Cluster, Probability = c.Probability })
					.ToList()
			}).ToList();

			var quotas = await QuotaCatalog.LoadQuotasFromDbAsync(sqlReader.ConnectionString, sqlQuotaTable, request.customer_code);
			var allocationResult = ClusterQuotaAllocator.Allocate(predictions, quotas);

			callbacks.OnQuotaAllocated?.Invoke(quotas, allocationResult);

			// ── Floating devices → split by cause, dump via Logic.dll ─────────────────
			List<DeviceResult> floatingDevices = new();
			List<DeviceResult> unknownPredictionDevices = new();
			List<DeviceResult> unallocatedDevices = new();

			if (allocationResult.Floating.Count > 0)
			{
				floatingDevices = allocationResult.Floating.Select(f => new DeviceResult
				{
					Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == f.DeviceId)?.CUSTOMER ?? request.customer_code,
					ProjectCode = request.project_code,
					DeviceId = f.DeviceId,
					DeviceType = f.DeviceType,
					Section = f.Section,
					Cluster = f.Cluster,
					Confidence = f.Score
				}).ToList();

				(unknownPredictionDevices, unallocatedDevices) = logic.SplitFloatingPool(floatingDevices);

				await logic.DumpFloating(unknownPredictionDevices);
				await logic.DumpUnallocated(unallocatedDevices);
			}

			callbacks.OnFloatingSplit?.Invoke(floatingDevices, unknownPredictionDevices, unallocatedDevices);

			// ── STEP 4/5: Assigned devices → cluster groups ───────────────────────────
			var assignedDevices = allocationResult.Assigned.Select(a => new DeviceResult
			{
				Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == a.DeviceId)?.CUSTOMER ?? request.customer_code,
				ProjectCode = request.project_code,
				DeviceId = a.DeviceId,
				DeviceType = a.DeviceType,
				Section = a.Section,
				Cluster = a.Cluster,
				Confidence = a.Score
			}).ToList();

			var clusterGroups = logic.BuildClusterGroups(assignedDevices);

			await logic.DumpAssigned(assignedDevices, allocationResult.Assigned);

			callbacks.OnClusterGroupsBuilt?.Invoke(assignedDevices, clusterGroups);

			return new DevicePipelineResult
			{
				Request = request,
				TypeResults = typeResults,
				PipelineResults = pipelineResults,
				DeviceTypeLookup = deviceTypeLookup,
				AllocationResult = allocationResult,
				UnknownPredictionDevices = unknownPredictionDevices,
				UnallocatedDevices = unallocatedDevices,
				AssignedDevices = assignedDevices,
				ClusterGroups = clusterGroups
			};
		}
	}
}
