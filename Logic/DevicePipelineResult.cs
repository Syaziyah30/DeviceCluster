using System.Collections.Generic;
using Model.ModelRequest;
using Model.ModelResult;
using Logic.Models;
using Logic.LogicAssignUser;

namespace Logic
{
	// Everything DevicePipeline.RunAsync produced, for the caller to act on afterward
	// (e.g. the interactive manual-correction loop, or a UI rendering a summary).
	public class DevicePipelineResult
	{
		public DevicePredictRequest Request { get; set; } = new();
		public List<DeviceTypeResult> TypeResults { get; set; } = new();
		public List<PipelineResult> PipelineResults { get; set; } = new();
		public Dictionary<string, string> DeviceTypeLookup { get; set; } = new();
		public AllocationResult AllocationResult { get; set; } = new();
		public List<DeviceResult> UnknownPredictionDevices { get; set; } = new();
		public List<DeviceResult> UnallocatedDevices { get; set; } = new();
		public List<DeviceResult> AssignedDevices { get; set; } = new();
		public List<ClusterGroup> ClusterGroups { get; set; } = new();
	}
}
