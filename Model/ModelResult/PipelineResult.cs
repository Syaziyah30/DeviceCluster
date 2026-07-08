using System;
using System.Collections.Generic;
using System.Text;

namespace Model.ModelResult
{
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
