using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Logic.LogicAssignUser
{
	public class UnknownDumpEntry
	{
		public string DumpedAt { get; set; } = string.Empty;  // datestamp
		public string Customer { get; set; } = string.Empty;
		public string ProjectCode { get; set; } = string.Empty;
		public string DeviceId { get; set; } = string.Empty;
		public string DeviceType { get; set; } = string.Empty;
		public string PredictedSection { get; set; } = string.Empty;
		public string PredictedCluster { get; set; } = string.Empty;
		public string Status { get; set; } = "pending";     // pending → assigned
	}
}