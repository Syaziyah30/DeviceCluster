using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Logic.SimilarityScore
{
	public static class NumericalSimilarity
	{
		// ── Extract numeric part from device ID ───────────────────────────────
		public static int ExtractNumeric(string deviceId)
		{
			var match = Regex.Match(deviceId, @"\d+");
			return match.Success ? int.Parse(match.Value) : -1;
		}

		// ── Numeric difference between two device IDs ─────────────────────────
		public static int NumericDiff(string deviceIdA, string deviceIdB)
		{
			int a = ExtractNumeric(deviceIdA);
			int b = ExtractNumeric(deviceIdB);
			if (a < 0 || b < 0) return int.MaxValue;  // no numeric → treat as furthest
			return Math.Abs(a - b);
		}

		// ── Numeric similarity % (0% = furthest, 100% = identical) ───────────
		public static double NumericSimilarity(string targetId, string candidateId, int maxDiff)
		{
			if (maxDiff == 0) return 100.0;  // all same number → 100%
			int diff = NumericDiff(targetId, candidateId);
			if (diff == int.MaxValue) return 0.0;
			return (1.0 - (double)diff / maxDiff) * 100.0;
		}

		// ── Suggest top N clusters by numeric similarity ──────────────────────
		public static List<(string Section, string Cluster, string ClosestDeviceId, int Diff, double Similarity)>
			SuggestTopClusters(
				string deviceId,
				List<Logic.Models.DeviceResult> knownDevices,
				int topN = 3)
		{
			int targetNumeric = ExtractNumeric(deviceId);
			if (targetNumeric < 0) return new();

			// Calculate diff for each known device
			var withDiff = knownDevices
				.Where(d => ExtractNumeric(d.DeviceId) >= 0)
				.Select(d => new
				{
					Device = d,
					Diff = Math.Abs(ExtractNumeric(d.DeviceId) - targetNumeric)
				})
				.ToList();

			if (withDiff.Count == 0) return new();

			// Get max diff for normalization
			int maxDiff = withDiff.Max(x => x.Diff);

			return withDiff
				.OrderBy(x => x.Diff)                                        // lowest diff first
				.GroupBy(x => new { x.Device.Section, x.Device.Cluster })    // one per cluster
				.Select(g =>
				{
					var best = g.First();                                      // closest in this cluster
					double sim = maxDiff == 0 ? 100.0
							   : (1.0 - (double)best.Diff / maxDiff) * 100.0;
					return (
						Section: best.Device.Section,
						Cluster: best.Device.Cluster,
						ClosestDeviceId: best.Device.DeviceId,
						Diff: best.Diff,
						Similarity: sim
					);
				})
				.OrderBy(x => x.Diff)                                         // sort by closest
				.Take(topN)
				.ToList();
		}

		// ── Find single closest device by numeric similarity ──────────────────
		public static Logic.Models.DeviceResult? FindClosest(
			string deviceId,
			List<Logic.Models.DeviceResult> knownDevices)
		{
			int targetNumeric = ExtractNumeric(deviceId);
			if (targetNumeric < 0) return null;

			return knownDevices
				.Where(d => ExtractNumeric(d.DeviceId) >= 0)
				.OrderBy(d => Math.Abs(ExtractNumeric(d.DeviceId) - targetNumeric))
				.FirstOrDefault();
		}
	}
}