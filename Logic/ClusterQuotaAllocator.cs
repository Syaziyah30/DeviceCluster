using System;
using System.Collections.Generic;
using System.Linq;
using Logic.Models;

namespace Logic
{
	// ============================================================
	// 📦 DATA CONTRACTS
	// ============================================================

	/// <summary>
	/// One row of raw model prediction output — unfiltered, before quota allocation.
	/// DeviceType is a fixed, single label per DeviceId (confirmed: not fuzzy, no multi-type ambiguity).
	/// </summary>
	public class DevicePrediction
	{
		public string Section { get; set; }
		public string Cluster { get; set; }
		public string DeviceId { get; set; }
		public string DeviceType { get; set; }
		public double Score { get; set; }   // 0.0 - 1.0 (or 0-100, just be consistent with caller)

		// Ranked cluster candidates for this device (model's own per-cluster prediction
		// percentages, same Section, highest first) — used by Step 3's reassignment pool
		// to try a device's next-best cluster when its top pick has no room.
		public List<ClusterPrediction> TopClusters { get; set; } = new();
	}

	/// <summary>
	/// Target count for a given Section/Cluster/DeviceType combination.
	/// Source-agnostic: caller may derive this from historical data, manual entry,
	/// or model self-confidence — ClusterQuotaAllocator does not care where it came from.
	/// </summary>
	public class ClusterQuota
	{
		public string Section { get; set; }
		public string Cluster { get; set; }
		public string DeviceType { get; set; }
		public int TargetCount { get; set; }
	}

	/// <summary>
	/// One finalized assignment — a device placed into a Section/Cluster,
	/// tagged with whether it was a primary (quota-native) pick or a backfill.
	/// </summary>
	public class AllocatedDevice
	{
		public string Section { get; set; }
		public string Cluster { get; set; }
		public string DeviceId { get; set; }
		public string DeviceType { get; set; }
		public double Score { get; set; }
		public bool IsBackfill { get; set; }        // ◄── true if pulled from floating pool to fill a vacancy
		public string OriginalCluster { get; set; }  // ◄── where the model originally predicted it, if different from final Cluster
	}

	/// <summary>
	/// A vacancy — either the ORIGINAL deficit measured right after Step 1 (before backfill runs),
	/// or the REMAINING deficit measured after Step 3 backfill has already tried to fill it.
	/// Same shape is reused for both, distinguished by which list it's placed in
	/// (AllocationResult.InitialDeficits vs AllocationResult.VacancyReport).
	/// </summary>
	public class VacancyReportEntry
	{
		public string Section { get; set; }
		public string Cluster { get; set; }
		public string DeviceType { get; set; }
		public int RemainingVacant { get; set; }

		public override string ToString() =>
			$"{RemainingVacant} vacant {DeviceType} in {Section} {Cluster}";
	}

	/// <summary>
	/// Full result of an allocation run.
	/// </summary>
	public class AllocationResult
	{
		public List<AllocatedDevice> Assigned { get; set; } = new List<AllocatedDevice>();
		public List<DevicePrediction> Floating { get; set; } = new List<DevicePrediction>();

		// ◄── NEW: quota gap as it existed right after Step 1, BEFORE Step 3 backfill ran.
		// Every quota bucket with TargetCount > directly-matched predictions shows up here,
		// even if backfill later fully closes the gap. Use this to see the "raw" model shortfall.
		public List<VacancyReportEntry> InitialDeficits { get; set; } = new List<VacancyReportEntry>();

		// Unchanged: quota gap AFTER backfill has already tried to fill it from the floating pool.
		// Only buckets that are STILL short after backfill appear here.
		public List<VacancyReportEntry> VacancyReport { get; set; } = new List<VacancyReportEntry>();
	}

	// ============================================================
	// 🧩 ALLOCATOR
	// ============================================================

	/// <summary>
	/// Two-pass quota-constrained cluster allocation:
	///   Step 1: for each (Section, Cluster, DeviceType) quota bucket, take the top-N
	///           highest-scoring predictions that match that Section/Cluster/DeviceType.
	///           Any shortfall here is recorded immediately into InitialDeficits.
	///   Step 2: whatever wasn't claimed in Step 1 becomes the "floating pool" for that Section.
	///   Step 3: device-centric reassignment pool. Floating devices are processed highest-score
	///           first; each tries its own ranked cluster candidates (model's per-cluster
	///           prediction percentages, same Section) in order, taking the first one that still
	///           has room. A device that exhausts its candidates without finding room stays floating.
	///   Any bucket still short after Step 3 is reported into VacancyReport, not silently dropped.
	/// </summary>
	public static class ClusterQuotaAllocator
	{
		public static AllocationResult Allocate(
			List<DevicePrediction> predictions,
			List<ClusterQuota> quotas)
		{
			var result = new AllocationResult();

			// Defensive copies — never mutate caller's input lists
			var remainingPredictions = new List<DevicePrediction>(predictions);
			var claimedIds = new HashSet<string>();   // ◄── DeviceId is assumed globally unique per run

			// ----------------------------------------------------
			// STEP 1 — Primary quota-constrained top-N allocation
			// ----------------------------------------------------
			foreach (var quota in quotas)
			{
				var candidates = remainingPredictions
					.Where(p => p.Section == quota.Section
							 && p.Cluster == quota.Cluster
							 && p.DeviceType == quota.DeviceType
							 && !claimedIds.Contains(p.DeviceId))
					.OrderByDescending(p => p.Score)
					.ToList();

				var take = candidates.Take(quota.TargetCount).ToList();

				foreach (var device in take)
				{
					claimedIds.Add(device.DeviceId);
					result.Assigned.Add(new AllocatedDevice
					{
						Section = device.Section,
						Cluster = device.Cluster,
						DeviceId = device.DeviceId,
						DeviceType = device.DeviceType,
						Score = device.Score,
						IsBackfill = false,
						OriginalCluster = device.Cluster
					});
				}

				int deficit = quota.TargetCount - take.Count;
				if (deficit > 0)
				{
					// Captures the ORIGINAL deficit, before Step 3 has a chance to touch it
					result.InitialDeficits.Add(new VacancyReportEntry
					{
						Section = quota.Section,
						Cluster = quota.Cluster,
						DeviceType = quota.DeviceType,
						RemainingVacant = deficit
					});
				}
			}

			// ----------------------------------------------------
			// STEP 2 — Build floating pool
			// ----------------------------------------------------
			// Everything not claimed in Step 1, across the whole prediction set.
			var floatingPool = remainingPredictions
				.Where(p => !claimedIds.Contains(p.DeviceId))
				.ToList();

			// Running assigned-count per quota bucket, so Step 3 knows how much room is left
			// as devices claim slots one at a time (Step 1's counts, then Step 3's on top).
			var bucketCounts = new Dictionary<(string Section, string Cluster, string DeviceType), int>();
			foreach (var quota in quotas)
				bucketCounts[(quota.Section, quota.Cluster, quota.DeviceType)] = 0;
			foreach (var assigned in result.Assigned)
			{
				var key = (assigned.Section, assigned.Cluster, assigned.DeviceType);
				if (bucketCounts.ContainsKey(key))
					bucketCounts[key]++;
			}

			// ----------------------------------------------------
			// STEP 3 — Device-centric reassignment pool
			// ----------------------------------------------------
			// Highest-score floating devices go first, so strong predictions get first pick
			// when multiple devices compete for the same vacancy. Each device tries its own
			// ranked cluster candidates (highest percentage first) until one has room, or it
			// runs out of candidates and stays floating.
			var reassignmentPool = floatingPool
				.OrderByDescending(p => p.Score)
				.ToList();

			foreach (var device in reassignmentPool)
			{
				foreach (var candidate in device.TopClusters.OrderByDescending(c => c.Probability))
				{
					var key = (device.Section, candidate.Cluster, device.DeviceType);
					if (!bucketCounts.TryGetValue(key, out int count))
						continue; // no quota bucket defined for this candidate cluster

					var matchingQuota = quotas.First(q =>
						q.Section == device.Section && q.Cluster == candidate.Cluster && q.DeviceType == device.DeviceType);

					if (count >= matchingQuota.TargetCount)
						continue; // this candidate is already full — try the device's next-ranked cluster

					claimedIds.Add(device.DeviceId);
					bucketCounts[key] = count + 1;
					result.Assigned.Add(new AllocatedDevice
					{
						Section = device.Section,
						Cluster = candidate.Cluster,       // ◄── placed into whichever ranked candidate had room
						DeviceId = device.DeviceId,
						DeviceType = device.DeviceType,
						Score = device.Score,
						IsBackfill = true,
						OriginalCluster = device.Cluster   // ◄── where the model originally predicted it (top-1 pick)
					});
					break; // placed — move on to the next device
				}
			}

			// ----------------------------------------------------
			// Vacancy report — every quota bucket still short after Step 3
			// ----------------------------------------------------
			result.VacancyReport = quotas
				.Select(q => (Quota: q, Count: bucketCounts[(q.Section, q.Cluster, q.DeviceType)]))
				.Where(x => x.Count < x.Quota.TargetCount)
				.Select(x => new VacancyReportEntry
				{
					Section = x.Quota.Section,
					Cluster = x.Quota.Cluster,
					DeviceType = x.Quota.DeviceType,
					RemainingVacant = x.Quota.TargetCount - x.Count
				})
				.ToList();

			// ----------------------------------------------------
			// Final floating list = anything still unclaimed after Step 3
			// ----------------------------------------------------
			result.Floating = remainingPredictions
				.Where(p => !claimedIds.Contains(p.DeviceId))
				.ToList();

			return result;
		}

		/// <summary>
		/// Prints every quota bucket that ended up fully satisfied — the complement of
		/// PrintVacancyReport. A bucket is fulfilled if it doesn't appear in the final
		/// vacancyReport (Step 1 take() and Step 3 backfill both cap assigned count at
		/// exactly TargetCount, so a fulfilled bucket's assigned count always equals it).
		/// </summary>
		public static void PrintFulfilledReport(List<ClusterQuota> quotas, List<VacancyReportEntry> vacancyReport)
		{
			var fulfilled = quotas
				.Where(q => !vacancyReport.Any(v =>
					v.Section == q.Section && v.Cluster == q.Cluster && v.DeviceType == q.DeviceType))
				.OrderBy(q => ExtractNumber(q.Section))
				.ThenBy(q => ExtractNumber(q.Cluster))
				.ToList();

			if (fulfilled.Count == 0)
			{
				Console.WriteLine("⚠ No quota buckets fully filled.");
				return;
			}

			Console.WriteLine("✅ Fulfilled Report:");
			foreach (var q in fulfilled)
			{
				Console.WriteLine($"   {q.Section} {q.Cluster} - {q.TargetCount} fulfilled {q.DeviceType}");
			}
		}


		/// <summary>
		/// Convenience formatter for printing a vacancy-style report to console,
		/// matching the style of PrintClusterTable elsewhere in Logic.dll.
		/// Works for both InitialDeficits and VacancyReport since they share the same shape.
		/// </summary>
		public static void PrintVacancyReport(List<VacancyReportEntry> report)
		{
			if (report == null || report.Count == 0)
			{
				Console.WriteLine("✅ No vacancies remaining — all quota buckets fully filled.");
				return;
			}

			var sorted = report
				.OrderBy(v => ExtractNumber(v.Section))
				.ThenBy(v => ExtractNumber(v.Cluster))
				.ToList();

			Console.WriteLine("⚠ Vacancy Report:");
			foreach (var entry in sorted)
			{
				Console.WriteLine($"   {entry.Section} {entry.Cluster} - {entry.RemainingVacant} vacant {entry.DeviceType}");
			}
		}

		// Pulls the trailing number out of "SECTION 10" / "CLUSTER 2" so sorting is numeric,
		// not alphabetical (alphabetical would put "SECTION 10" before "SECTION 2").
		private static int ExtractNumber(string s)
		{
			string digits = new string(s.Where(char.IsDigit).ToArray());
			return int.TryParse(digits, out int n) ? n : 0;
		}
	}
}