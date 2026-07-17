using System;
using System.Collections.Generic;
using System.Linq;

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
	///   Step 3: for each quota bucket left with a deficit (fewer matches than TargetCount),
	///           backfill from the floating pool — matched by DeviceType only, NOT restricted
	///           to the device's originally-predicted Cluster. Backfill candidates are drawn
	///           from anywhere in the same Section.
	///   Any deficit still remaining after backfill is reported into VacancyReport, not silently dropped.
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
			// Track deficits per quota bucket for Step 3
			var deficits = new List<(ClusterQuota Quota, int Deficit)>();

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
					deficits.Add((quota, deficit));

					// ◄── NEW: capture the ORIGINAL deficit here, before backfill has a chance to touch it
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

			// ----------------------------------------------------
			// STEP 3 — Backfill deficits from floating pool
			// ----------------------------------------------------
			// Floating pool is scoped per Section (not per Section+Cluster) — confirmed:
			// a device predicted into Cluster 2 can backfill a Cluster 6 vacancy in the same Section.
			foreach (var (quota, deficit) in deficits)
			{
				var backfillCandidates = floatingPool
					.Where(p => p.Section == quota.Section          // ◄── same Section only
							 && p.DeviceType == quota.DeviceType     // ◄── hard filter, DeviceType is fixed per device
							 && !claimedIds.Contains(p.DeviceId))
					.OrderByDescending(p => p.Score)
					.ToList();

				var fill = backfillCandidates.Take(deficit).ToList();

				foreach (var device in fill)
				{
					claimedIds.Add(device.DeviceId);
					result.Assigned.Add(new AllocatedDevice
					{
						Section = quota.Section,
						Cluster = quota.Cluster,        // ◄── placed into the VACANT cluster, not its original prediction
						DeviceId = device.DeviceId,
						DeviceType = device.DeviceType,
						Score = device.Score,
						IsBackfill = true,
						OriginalCluster = device.Cluster
					});
				}

				int remainingDeficit = deficit - fill.Count;
				if (remainingDeficit > 0)
				{
					result.VacancyReport.Add(new VacancyReportEntry
					{
						Section = quota.Section,
						Cluster = quota.Cluster,
						DeviceType = quota.DeviceType,
						RemainingVacant = remainingDeficit
					});
				}
			}

			// ----------------------------------------------------
			// Final floating list = anything still unclaimed after backfill
			// ----------------------------------------------------
			result.Floating = remainingPredictions
				.Where(p => !claimedIds.Contains(p.DeviceId))
				.ToList();

			return result;
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

			Console.WriteLine("⚠ Vacancy Report:");
			foreach (var entry in report)
			{
				Console.WriteLine($"   {entry}");
			}
		}
	}
}