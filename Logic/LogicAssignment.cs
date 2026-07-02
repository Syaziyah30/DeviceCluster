using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Logic.Models;
using Logic.LogicAssignUser;
using Logic.SimilarityScore;  // ◄── NEW

namespace Logic
{
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


		// ── STEP 3: Assign section/cluster by Levenshtein similarity ──────────  // ◄── MODIFIED
		public DeviceResult AssignByNumericSimilarity(
			UnknownDumpEntry entry,
			List<DeviceResult> knownDevices)
		{
			if (string.IsNullOrEmpty(entry.DeviceId))
			{
				Console.WriteLine($"[Logic] Empty DeviceId. Skipping.");
				return null!;
			}

			// ◄── MODIFIED: use Levenshtein instead of numeric extraction
			var closest = knownDevices
				.Where(d => !string.IsNullOrEmpty(d.DeviceId))
				.OrderByDescending(d => StringSimilarity.LevenshteinSimilarity(entry.DeviceId, d.DeviceId))
				.FirstOrDefault();

			if (closest == null)
			{
				Console.WriteLine($"[Logic] No known devices to compare against for '{entry.DeviceId}'.");
				return null!;
			}

			double sim = StringSimilarity.LevenshteinSimilarity(entry.DeviceId, closest.DeviceId);

			Console.WriteLine($"[Logic] '{entry.DeviceId}' " +
							  $"→ closest to '{closest.DeviceId}' (similarity={sim:F1}%) " +
							  $"→ assigned {closest.Section}, {closest.Cluster}");

			return new DeviceResult
			{
				Customer = entry.Customer,
				ProjectCode = entry.ProjectCode,
				DeviceId = entry.DeviceId,
				DeviceType = entry.DeviceType,
				Section = closest.Section,
				Cluster = closest.Cluster,
				Confidence = closest.Confidence
			};
		}


		// ── STEP 3B: Suggest top N clusters by Levenshtein similarity ─────────  // ◄── MODIFIED
		public List<(string Section, string Cluster, string ClosestDeviceId, double Similarity)>
			SuggestTopClusters(string deviceId, List<DeviceResult> knownDevices, int topN = 3)
		{
			return knownDevices
				.Where(d => !string.IsNullOrEmpty(d.DeviceId))
				.Select(d => new
				{
					Device = d,
					Similarity = StringSimilarity.LevenshteinSimilarity(deviceId, d.DeviceId)  // ◄── MODIFIED
				})
				.OrderByDescending(x => x.Similarity)
				.GroupBy(x => new { x.Device.Section, x.Device.Cluster })
				.Select(g =>
				{
					var best = g.First();
					return (
						Section: best.Device.Section,
						Cluster: best.Device.Cluster,
						ClosestDeviceId: best.Device.DeviceId,
						Similarity: best.Similarity
					);
				})
				.OrderByDescending(x => x.Similarity)
				.Take(topN)
				.ToList();
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


		// ── HELPERS ───────────────────────────────────────────────────────────
		private int ExtractNumeric(string deviceId)
		{
			var match = Regex.Match(deviceId, @"\d+");
			return match.Success ? int.Parse(match.Value) : -1;
		}

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
	}
}