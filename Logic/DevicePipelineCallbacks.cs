using System;
using System.Collections.Generic;
using Model.ModelRequest;
using Model.ModelResult;
using Logic.Models;
using Logic.LogicAssignUser;

namespace Logic
{
	// Progress hooks for DevicePipeline.RunAsync — one per meaningful checkpoint, fired
	// synchronously as that stage completes. Each callback owns its own presentation
	// (console printing, UI updates, pausing for input, etc.) — the pipeline itself never
	// writes to Console. Leave any hook null to skip it; nothing fires by default.
	public class DevicePipelineCallbacks
	{
		public Action<DevicePredictRequest, string>? OnProjectLoaded;                                   // (request, devicesJsonPath)
		public Action<List<DeviceTypeResult>, double>? OnDeviceTypesPredicted;                           // (typeResults, elapsedSecs)
		public Action<List<PipelineResult>, Dictionary<string, string>, double>? OnSectionsPredicted;    // (pipelineResults, deviceTypeLookup, elapsedSecs)
		public Action<List<PipelineResult>, Dictionary<string, string>, double>? OnClustersPredicted;    // (pipelineResults, deviceTypeLookup, elapsedSecs)
		public Action<List<ClusterQuota>, AllocationResult>? OnQuotaAllocated;                           // (quotas, allocationResult)
		public Action<List<DeviceResult>, List<DeviceResult>, List<DeviceResult>>? OnFloatingSplit;      // (allFloating, unknownPrediction, unallocated)
		public Action<List<DeviceResult>, List<ClusterGroup>>? OnClusterGroupsBuilt;                     // (assignedDevices, clusterGroups)
	}
}
