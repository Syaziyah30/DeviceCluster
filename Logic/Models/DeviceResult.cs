using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Logic.Models
{
	public class DeviceResult
	{
		public string Customer { get; set; } = string.Empty;
		public string ProjectCode { get; set; } = string.Empty;
		public string DeviceId { get; set; } = string.Empty;
		public string DeviceType { get; set; } = string.Empty;
		public string Section { get; set; } = string.Empty;
		public string Cluster { get; set; } = string.Empty;
		public double Confidence { get; set; }

		// ◄── top-3 cluster candidates from predict_sectioncluster.py
		public List<ClusterPrediction> TopClusters { get; set; } = new();
	}
}