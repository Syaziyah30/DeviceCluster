
namespace Logic.LogicAssignUser
{
	public class FloatingDumpEntry
	{
		public string DumpedAt { get; set; } = string.Empty;  // datestamp
		public string Customer { get; set; } = string.Empty;
		public string ProjectCode { get; set; } = string.Empty;
		public string DeviceId { get; set; } = string.Empty;
		public string DeviceType { get; set; } = string.Empty;
		public string PredictedSection { get; set; } = string.Empty;
		public string PredictedCluster { get; set; } = string.Empty;
		public string Status { get; set; } = string.Empty;    // Assign (quota room, needs manual placement) | floating (prediction itself UNKNOWN)
	}
}
