using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceIdentifierLibrary.Models
{
	public class DevicePredictRequest
	{
		public string project_code { get; set; }
		public string customer_code { get; set; }
		public List<string> data_ids { get; set; }
	}

	public class ManualAssignment
	{
		public string data_id { get; set; }
		public string equipment { get; set; }
	}

	public class UserManualAssignRequest
	{
		public string action { get; set; } = "user_manual_assign";
		public string project_code { get; set; }
		public string customer { get; set; }
		public List<ManualAssignment> assignments { get; set; }
	}

	public class PipelinePredictRequest
	{
		public List<PipelineRecord> records { get; set; }
	}

	public class PipelineRecord
	{
		public string device_id { get; set; }
		public string customer { get; set; }
		public string project { get; set; }
	}
}
