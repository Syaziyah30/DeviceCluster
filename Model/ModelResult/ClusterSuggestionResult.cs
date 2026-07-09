using System;
using System.Collections.Generic;
using System.Text;

namespace Model.ModelResult
{
	public class ClusterSuggestionResult
	{
		public string Section { get; set; } = string.Empty;
		public string Cluster { get; set; } = string.Empty;
		public string ClosestDeviceId { get; set; } = string.Empty;
		public double Confidence { get; set; }
	}
}