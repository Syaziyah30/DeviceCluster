using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Logic.Models;
using Logic.LogicAssignUser;
using Logic.SimilarityScore;

namespace Logic
{
	// ◄── NEW: result shape for Program.cs's manual-correction cluster suggestions
	public class ClusterSuggestion
	{
		public string Section { get; set; } = string.Empty;
		public string Cluster { get; set; } = string.Empty;
		public string ClosestDeviceId { get; set; } = string.Empty;
		public double Confidence { get; set; }
	}

	public class LogicAssignment
	{
		private readonly string _connectionString;
		private readonly string _reviewQueueTable;
		private readonly string _assignmentTable;

		public LogicAssignment(string connectionString, string reviewQueueTable = "dbo.DeviceReviewQueue", string assignmentTable = "dbo.OutputDeviceAssignment")
		{
			_connectionString = connectionString;
			_reviewQueueTable = reviewQueueTable;
			_assignmentTable = assignmentTable;
		}

		// ── STEP 3.5a: Split the FLOATING pool by cause ────────────────────────
		// unknownPrediction: DeviceType/Section/Cluster itself is UNKNOWN — the model gave nothing to allocate.
		// unallocatedKnown: prediction is fully known, but no quota bucket had room (or none matched at all).
		public (List<DeviceResult> unknownPrediction, List<DeviceResult> unallocatedKnown) SplitFloatingPool(
			List<DeviceResult> floatingDevices)
		{
			var unknownPrediction = floatingDevices.Where(r => !HasKnownPrediction(r)).ToList();
			var unallocatedKnown = floatingDevices.Where(r => HasKnownPrediction(r)).ToList();
			Console.WriteLine($"[Logic] Floating: {unknownPrediction.Count} unknown prediction | {unallocatedKnown.Count} known but unallocated");
			return (unknownPrediction, unallocatedKnown);
		}

		private bool HasKnownPrediction(DeviceResult r) =>
			!string.IsNullOrEmpty(r.DeviceType) && r.DeviceType != "UNKNOWN" &&
			!string.IsNullOrEmpty(r.Section) && r.Section != "UNKNOWN" &&
			!string.IsNullOrEmpty(r.Cluster) && r.Cluster != "UNKNOWN";


		// ── STEP 3.5b: Upsert devices with an UNKNOWN prediction into dbo.DeviceReviewQueue ──
		public async Task DumpFloating(List<DeviceResult> unknownDevices)
		{
			await UpsertReviewQueueAsync(unknownDevices, "UnknownPrediction");
		}


		// ── STEP 3.5c: Upsert devices with a known prediction that quota allocation couldn't place ──
		public async Task DumpUnallocated(List<DeviceResult> unallocatedDevices)
		{
			await UpsertReviewQueueAsync(unallocatedDevices, "Unallocated");
		}


		// Shared upsert for both categories. A MERGE keyed on (DeviceId, ProjectCode) means a
		// device that changes classification between runs just gets its Category column updated
		// on the same row — no cross-file reconciliation needed, unlike the old JSON-file design.
		private async Task UpsertReviewQueueAsync(List<DeviceResult> devices, string category)
		{
			if (devices.Count == 0)
			{
				Console.WriteLine($"[Logic] No {category} devices to dump.");
				return;
			}

			await using var connection = new SqlConnection(_connectionString);
			await connection.OpenAsync();

			string sql = $@"
				MERGE {_reviewQueueTable} AS target
				USING (SELECT @DeviceId AS DeviceId, @ProjectCode AS ProjectCode) AS src
				  ON target.DeviceId = src.DeviceId AND target.ProjectCode = src.ProjectCode
				WHEN MATCHED THEN
					UPDATE SET Category = @Category, DeviceType = @DeviceType,
							   PredictedSection = @PredictedSection, PredictedCluster = @PredictedCluster
				WHEN NOT MATCHED THEN
					INSERT (Category, DumpedAt, Customer, ProjectCode, DeviceId, DeviceType, PredictedSection, PredictedCluster, Status)
					VALUES (@Category, @DumpedAt, @Customer, @ProjectCode, @DeviceId, @DeviceType, @PredictedSection, @PredictedCluster, 'pending');";

			foreach (var d in devices)
			{
				await using var command = new SqlCommand(sql, connection);
				command.Parameters.AddWithValue("@Category", category);
				command.Parameters.AddWithValue("@DumpedAt", DateTime.Now);
				command.Parameters.AddWithValue("@Customer", d.Customer);
				command.Parameters.AddWithValue("@ProjectCode", d.ProjectCode);
				command.Parameters.AddWithValue("@DeviceId", d.DeviceId);
				command.Parameters.AddWithValue("@DeviceType", d.DeviceType);
				command.Parameters.AddWithValue("@PredictedSection", d.Section);
				command.Parameters.AddWithValue("@PredictedCluster", d.Cluster);

				await command.ExecuteNonQueryAsync();
			}

			Console.WriteLine($"[Logic] Upserted {devices.Count} {category} device(s) into {_reviewQueueTable}");
		}


		// ── STEP 4b: Upsert successfully assigned devices into dbo.OutputDeviceAssignment ──
		// allocated carries IsBackfill/OriginalCluster (lost once devices are mapped to
		// DeviceResult for cluster grouping) — joined back in here by DeviceId.
		public async Task DumpAssigned(List<DeviceResult> devices, List<AllocatedDevice> allocated)
		{
			if (devices.Count == 0)
			{
				Console.WriteLine("[Logic] No assigned devices to dump.");
				return;
			}

			var allocLookup = allocated
				.GroupBy(a => a.DeviceId)
				.ToDictionary(g => g.Key, g => g.Last());

			await using var connection = new SqlConnection(_connectionString);
			await connection.OpenAsync();

			string sql = $@"
				MERGE {_assignmentTable} AS target
				USING (SELECT @DeviceId AS DeviceId, @ProjectCode AS ProjectCode) AS src
				  ON target.DeviceId = src.DeviceId AND target.ProjectCode = src.ProjectCode
				WHEN MATCHED THEN
					UPDATE SET AssignedAt = @AssignedAt, Customer = @Customer, DeviceType = @DeviceType,
							   Section = @Section, Cluster = @Cluster, Confidence = @Confidence,
							   IsBackfill = @IsBackfill, OriginalCluster = @OriginalCluster
				WHEN NOT MATCHED THEN
					INSERT (AssignedAt, Customer, ProjectCode, DeviceId, DeviceType, Section, Cluster, Confidence, IsBackfill, OriginalCluster)
					VALUES (@AssignedAt, @Customer, @ProjectCode, @DeviceId, @DeviceType, @Section, @Cluster, @Confidence, @IsBackfill, @OriginalCluster);";

			foreach (var d in devices)
			{
				allocLookup.TryGetValue(d.DeviceId, out var alloc);

				await using var command = new SqlCommand(sql, connection);
				command.Parameters.AddWithValue("@AssignedAt", DateTime.Now);
				command.Parameters.AddWithValue("@Customer", d.Customer);
				command.Parameters.AddWithValue("@ProjectCode", d.ProjectCode);
				command.Parameters.AddWithValue("@DeviceId", d.DeviceId);
				command.Parameters.AddWithValue("@DeviceType", d.DeviceType);
				command.Parameters.AddWithValue("@Section", d.Section);
				command.Parameters.AddWithValue("@Cluster", d.Cluster);
				command.Parameters.AddWithValue("@Confidence", d.Confidence);
				command.Parameters.AddWithValue("@IsBackfill", alloc?.IsBackfill ?? false);
				command.Parameters.AddWithValue("@OriginalCluster", (object?)alloc?.OriginalCluster ?? DBNull.Value);

				await command.ExecuteNonQueryAsync();
			}

			Console.WriteLine($"[Logic] Upserted {devices.Count} assigned device(s) into {_assignmentTable}");
		}


		// ── STEP 3: Show model's cluster suggestion + section context ─────────
		public void ShowClusterSuggestion(DeviceResult device, List<DeviceResult> knownDevices)
		{
			Console.WriteLine($"\n{device.DeviceId} — predicted {device.Section} ({device.Confidence:F2}%)");
			Console.WriteLine("Top cluster candidates:");
			foreach (var c in device.TopClusters.OrderByDescending(c => c.Probability))
				Console.WriteLine($"  {device.DeviceId} - {c.Probability:F2}% - {c.Cluster}");
			var sectionDevices = knownDevices.Where(d => d.Section == device.Section).ToList();
			var groups = BuildClusterGroups(sectionDevices);
			PrintClusterTable(groups, device.Section);
		}


		// ── STEP 3b: Wrap NumericalSimilarity.SuggestTopClusters for Program.cs ────
		public List<ClusterSuggestion> SuggestTopClusters(string deviceId, List<DeviceResult> knownDevices, int topN = 3)
		{
			return NumericalSimilarity.SuggestTopClusters(deviceId, knownDevices, topN)
				.Select(x => new ClusterSuggestion
				{
					Section = x.Section,
					Cluster = x.Cluster,
					ClosestDeviceId = x.ClosestDeviceId,
					Confidence = x.Similarity
				})
				.ToList();
		}


		// ── Resolve a manual correction into a placeable DeviceResult ──────────────
		public DeviceResult? AssignByNumericSimilarity(UnallocatedDumpEntry entry, List<DeviceResult> knownDevices)
		{
			string resolvedSection = entry.PredictedSection;
			string resolvedCluster = entry.PredictedCluster;

			bool needsSection = string.IsNullOrEmpty(resolvedSection) || resolvedSection == "UNKNOWN";
			bool needsCluster = string.IsNullOrEmpty(resolvedCluster) || resolvedCluster == "UNKNOWN";

			if (needsSection || needsCluster)
			{
				var closest = NumericalSimilarity.FindClosest(entry.DeviceId, knownDevices);
				if (closest == null) return null;

				if (needsSection) resolvedSection = closest.Section;
				if (needsCluster) resolvedCluster = closest.Cluster;
			}

			return new DeviceResult
			{
				Customer = entry.Customer,
				ProjectCode = entry.ProjectCode,
				DeviceId = entry.DeviceId,
				DeviceType = entry.DeviceType,
				Section = resolvedSection,
				Cluster = resolvedCluster,
				Confidence = 100.0   // manually confirmed by user, treated as full confidence
			};
		}


		// ── STEP 4: Build cluster groups with scores ───────────────────────────
		public List<ClusterGroup> BuildClusterGroups(List<DeviceResult> devices)
		{
			return devices
				.GroupBy(d => new { d.Section, d.Cluster })
				.Select(g =>
				{
					var deviceList = g.ToList();
					return new ClusterGroup
					{
						Section = g.Key.Section,
						Cluster = g.Key.Cluster,
						Devices = deviceList.Select(d => new ScoredDevice
						{
							Device = d,
							Score = d.Confidence
						}).ToList()
					};
				})
				.ToList();
		}


		// ── STEP 5: Place new device + displacement check ─────────────────────
		public void PlaceDevice(DeviceResult newDevice, List<ClusterGroup> groups)
		{
			var targetGroup = groups.FirstOrDefault(g =>
				g.Section == newDevice.Section && g.Cluster == newDevice.Cluster);

			if (targetGroup == null)
			{
				groups.Add(new ClusterGroup
				{
					Section = newDevice.Section,
					Cluster = newDevice.Cluster,
					Devices = new List<ScoredDevice>
					{
						new ScoredDevice { Device = newDevice, Score = newDevice.Confidence }
					}
				});
				Console.WriteLine($"[Logic] Created new cluster {newDevice.Section}/{newDevice.Cluster} " +
								  $"for '{newDevice.DeviceId}'");
				return;
			}

			double newScore = newDevice.Confidence;
			var weakest = targetGroup.Devices.OrderBy(sd => sd.Score).First();

			if (newScore >= weakest.Score)
			{
				targetGroup.Devices.Add(new ScoredDevice { Device = newDevice, Score = newScore });
				Console.WriteLine($"[Logic] '{newDevice.DeviceId}' added to " +
								  $"{newDevice.Section}/{newDevice.Cluster} (score={newScore:F1}%)");
			}
			else
			{
				Console.WriteLine($"[Logic] '{newDevice.DeviceId}' (score={newScore:F1}%) < " +
								  $"weakest '{weakest.Device.DeviceId}' (score={weakest.Score:F1}%) " +
								  $"→ displacing weakest");

				targetGroup.Devices.Add(new ScoredDevice { Device = newDevice, Score = newScore });
				targetGroup.Devices.Remove(weakest);
				DisplaceToNextBestCluster(weakest.Device, groups);
			}

			RecalculateScores(targetGroup);
		}


		// ── STEP 6: Displacement to next best cluster ─────────────────────────
		private void DisplaceToNextBestCluster(DeviceResult displaced, List<ClusterGroup> groups)
		{
			var sameSection = groups
				.Where(g => g.Section == displaced.Section && g.Cluster != displaced.Cluster)
				.ToList();

			if (sameSection.Count == 0)
			{
				Console.WriteLine($"[Logic] No alternative cluster in {displaced.Section} " +
								  $"for '{displaced.DeviceId}' → remains displaced (review needed)");
				return;
			}

			var bestCluster = sameSection
				.OrderByDescending(g => g.Devices.Average(sd => sd.Score))
				.First();

			bestCluster.Devices.Add(new ScoredDevice
			{
				Device = displaced,
				Score = displaced.Confidence
			});

			Console.WriteLine($"[Logic] Displaced '{displaced.DeviceId}' → " +
							  $"{bestCluster.Section}/{bestCluster.Cluster} " +
							  $"(confidence={displaced.Confidence:F2}%)");
		}


		// ── UPDATE dump status after user assigns ─────────────────────────────  // ◄── NEW
		public async Task MarkAsAssigned(string deviceId, string projectCode, string resolvedSection, string resolvedCluster)
		{
			await using var connection = new SqlConnection(_connectionString);
			await connection.OpenAsync();

			string sql = $@"
				UPDATE {_reviewQueueTable}
				SET Status = 'assigned', PredictedSection = @Section, PredictedCluster = @Cluster
				WHERE DeviceId = @DeviceId AND ProjectCode = @ProjectCode";

			await using var command = new SqlCommand(sql, connection);
			command.Parameters.AddWithValue("@Section", resolvedSection);
			command.Parameters.AddWithValue("@Cluster", resolvedCluster);
			command.Parameters.AddWithValue("@DeviceId", deviceId);
			command.Parameters.AddWithValue("@ProjectCode", projectCode);

			int rows = await command.ExecuteNonQueryAsync();
			Console.WriteLine(rows > 0
				? $"[Logic] '{deviceId}' marked as assigned in {_reviewQueueTable}"
				: $"[Logic] '{deviceId}' not found in {_reviewQueueTable} — nothing to mark");
		}


		// ── HELPERS ───────────────────────────────────────────────────────────
		private void RecalculateScores(ClusterGroup group)
		{
			foreach (var sd in group.Devices)
				sd.Score = sd.Device.Confidence;
		}


		// ── PRINT HELPERS ─────────────────────────────────────────────────────
		public void PrintClusterTable(List<ClusterGroup> groups)
		{
			Console.WriteLine("\n===== LOGIC: CLUSTER GROUPING =====\n");

			var bySections = groups
				.OrderBy(g => g.Section)
				.ThenBy(g => g.Cluster)
				.GroupBy(g => g.Section);

			foreach (var section in bySections)
			{
				Console.WriteLine($"\n{section.Key} {new string('-', 88)}");
				Console.WriteLine($"{"Section",-12} | {"Cluster",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Score %",10}");
				Console.WriteLine(new string('-', 95));

				foreach (var g in section)
					foreach (var sd in g.Devices.OrderByDescending(sd => sd.Score))
						Console.WriteLine($"{g.Section,-12} | {g.Cluster,-12} | " +
										  $"{sd.Device.DeviceId,-25} | {sd.Device.DeviceType,-25} | " +
										  $"{sd.Score,9:F1}%");
			}
		}

		// ── PRINT HELPERS: filtered by section ────────────────────────────────
		public void PrintClusterTable(List<ClusterGroup> groups, string sectionFilter)
		{
			Console.WriteLine($"\n===== LOGIC: CLUSTER GROUPING — {sectionFilter} =====\n");

			var filtered = groups
				.Where(g => g.Section == sectionFilter)
				.OrderBy(g => g.Cluster)
				.GroupBy(g => g.Section);

			foreach (var section in filtered)
			{
				Console.WriteLine($"\n{section.Key} {new string('-', 88)}");
				Console.WriteLine($"{"Section",-12} | {"Cluster",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Score %",10}");
				Console.WriteLine(new string('-', 95));

				foreach (var g in section)
					foreach (var sd in g.Devices.OrderByDescending(sd => sd.Score))
						Console.WriteLine($"{g.Section,-12} | {g.Cluster,-12} | " +
										  $"{sd.Device.DeviceId,-25} | {sd.Device.DeviceType,-25} | " +
										  $"{sd.Score,9:F1}%");
			}
		}
	}
}