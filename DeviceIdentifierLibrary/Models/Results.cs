using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceIdentifierLibrary.Models
{
	public class DeviceTypeResult
	{
		public string customer { get; set; }
		public string data_id { get; set; }
		public string manual_check { get; set; }
		public string data_type { get; set; }
		public double? confidence { get; set; }
		public string reason { get; set; }
	}

	public class PipelineResult
	{
		public string DEVICE_ID { get; set; }
		public string CUSTOMER { get; set; }
		public string PROJECT { get; set; }
		public string PREDICTED_SECTION { get; set; }
		public double? SECTION_CONFIDENCE { get; set; }
		public string PREDICTED_CLUSTER { get; set; }
		public double? CLUSTER_CONFIDENCE { get; set; }
		public string REJECTION_REASON { get; set; }
		public string FORMAT_WARNING { get; set; }
	}
}