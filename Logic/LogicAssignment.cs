using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
		private readonly string _dumpFilePath;
		private readonly JsonSerializerOptions _jsonOpts = new()
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		public LogicAssignment(string dumpFilePath)
		{
			_dumpFilePath = dumpFilePath;
		}

		// ── STEP 1: Split KNOWN vs UNKNOWN ────────────────────────────────────
		public (List<DeviceResult> known, List<DeviceResult> unknown) SplitKnownUnknown(
			List<DeviceResult> allResults)
		{
			var known = allResults.Where(r => IsKnown(r)).ToList();
			var unknown = allResults.Where(r => !IsKnown(r)).ToList();
			Console.WriteLine($"[Logic] Known: {known.Count} devices | Unknown: {unknown.Count} devices");
			return (known, unknown);
		}

		private bool IsKnown(DeviceResult r) =>
			!string.IsNullOrEmpty(r.DeviceType) && r.DeviceType != "UNKNOWN" &&
			!string.IsNullOrEmpty(r.Section) && r.Section != "UNKNOWN" &&
			!string.IsNullOrEmpty(r.Cluster) && r.Cluster != "UNKNOWN";


		// ── STEP 2: Dump UNKNOWN to JSON file ─────────────────────────────────
		public void DumpUnknown(List<DeviceResult> unknownDevices)
		{
			if (unknownDevices.Count == 0)
			{
				Console.WriteLine("[Logic] No unknown devices to dump.");
				return;
			}

			List<UnknownDumpEntry> existing = LoadDumpFile();
			string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

			foreach (var d in unknownDevices)
			{
				bool alreadyExists = existing.Any(e =>
					e.DeviceId == d.DeviceId && e.ProjectCode == d.ProjectCode);

				if (!alreadyExists)
				{
					existing.Add(new UnknownDumpEntry
					{
						DumpedAt = now,
						Customer = d.Customer,
						ProjectCode = d.ProjectCode,
						DeviceId = d.DeviceId,
						DeviceType = d.DeviceType,
						PredictedSection = d.Section,
						PredictedCluster = d.Cluster,
						Status = "pending"
					});
				}
			}

			string json = JsonSerializer.Serialize(existing, _jsonOpts);
			Directory.CreateDirectory(Path.GetDirectoryName(_dumpFilePath)!);
			File.WriteAllText(_dumpFilePath, json);
			Console.WriteLine($"[Logic] Dumped {unknownDevices.Count} unknown devices → {_dumpFilePath}");
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
		public DeviceResult? AssignByNumericSimilarity(UnknownDumpEntry entry, List<DeviceResult> knownDevices)
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
		public void MarkAsAssigned(string deviceId, string projectCode, string resolvedSection, string resolvedCluster)
		{
			List<UnknownDumpEntry> existing = LoadDumpFile();

			var entry = existing.FirstOrDefault(e =>
				e.DeviceId == deviceId && e.ProjectCode == projectCode);

			if (entry != null)
			{
				entry.Status = "assigned";
				entry.PredictedSection = resolvedSection;
				entry.PredictedCluster = resolvedCluster;
			}

			string json = JsonSerializer.Serialize(existing, _jsonOpts);
			File.WriteAllText(_dumpFilePath, json);
			Console.WriteLine($"[Logic] '{deviceId}' marked as assigned → {_dumpFilePath}");
		}


		// ── HELPERS ───────────────────────────────────────────────────────────
		private void RecalculateScores(ClusterGroup group)
		{
			foreach (var sd in group.Devices)
				sd.Score = sd.Device.Confidence;
		}

		private List<UnknownDumpEntry> LoadDumpFile()
		{
			if (!File.Exists(_dumpFilePath)) return new List<UnknownDumpEntry>();
			string json = File.ReadAllText(_dumpFilePath);
			return JsonSerializer.Deserialize<List<UnknownDumpEntry>>(json, _jsonOpts)
				   ?? new List<UnknownDumpEntry>();
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