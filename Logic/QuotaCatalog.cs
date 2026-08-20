using System;
using System.Collections.Generic;

namespace Logic
{
	// ⚠️ DUMMY DATA — placeholder quota patterns for pipeline testing only, not confirmed business rules.
	// Keyed by CustomerCode (not ProjectCode): the working assumption is that a client's different
	// projects share the same physical plant layout, so every OILTEK project gets the same SECTION 2
	// pattern below. If that assumption turns out wrong for some project, this catalog would need a
	// project-level override on top of the customer-level default — not implemented yet, since only
	// one customer's pattern exists so far.
	//
	// This lives in Logic.dll (not Program.cs) specifically so it ships with the library — any caller
	// (console app, future UI, etc.) can call QuotaCatalog.GetDraftQuotas(customerCode) directly.
	// Once real per-customer patterns are confirmed, replace this dictionary's contents, or swap
	// GetDraftQuotas' implementation for a database-backed lookup — callers don't need to change,
	// since they only ever consume the returned List<ClusterQuota>.
	public static class QuotaCatalog
	{
		private static readonly Dictionary<string, List<ClusterQuota>> DraftQuotasByCustomer = new(StringComparer.OrdinalIgnoreCase)
		{
			["OILTEK"] = new List<ClusterQuota>
			{
				// CLUSTER 1
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Fan", TargetCount = 4 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "On/Off Valve", TargetCount = 6 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "High Level Switch", TargetCount = 6 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Low Level Switch", TargetCount = 4 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Control Valve", TargetCount = 4 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Level Transmitter", TargetCount = 2 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Pump", TargetCount = 5 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Pressure Switch", TargetCount = 3 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Pressure Transmitter", TargetCount = 2 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 1", DeviceType = "Vibrator", TargetCount = 4 },

				// CLUSTER 2
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 2", DeviceType = "On/Off Valve", TargetCount = 5 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 2", DeviceType = "Control Valve", TargetCount = 5 },

				// CLUSTER 3
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 3", DeviceType = "On/Off Valve", TargetCount = 4 },

				// CLUSTER 4
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 4", DeviceType = "On/Off Valve", TargetCount = 6 },

				// CLUSTER 5
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 5", DeviceType = "Pump", TargetCount = 4 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 5", DeviceType = "Heater", TargetCount = 2 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 5", DeviceType = "Temperature Transmitter", TargetCount = 3 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 5", DeviceType = "Low Level Switch", TargetCount = 4 },

				// CLUSTER 6
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 6", DeviceType = "Control Valve", TargetCount = 7 },
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 6", DeviceType = "On/Off Valve", TargetCount = 7 },

				// CLUSTER 7 — this is the cluster the model currently never predicts (per earlier report)
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 7", DeviceType = "On/Off Valve", TargetCount = 6 },

				// CLUSTER 8 — same issue, model never predicts this cluster
				new ClusterQuota { Section = "SECTION 2", Cluster = "CLUSTER 8", DeviceType = "Control Valve", TargetCount = 7 },
			}
		};

		public static List<ClusterQuota> GetDraftQuotas(string customerCode)
		{
			if (string.IsNullOrWhiteSpace(customerCode))
				throw new ArgumentException("Customer code cannot be empty.", nameof(customerCode));

			if (DraftQuotasByCustomer.TryGetValue(customerCode, out var quotas))
				return quotas;

			throw new InvalidOperationException(
				$"No draft quota pattern defined for customer '{customerCode}'. " +
				$"Add one to QuotaCatalog.DraftQuotasByCustomer.");
		}
	}
}
