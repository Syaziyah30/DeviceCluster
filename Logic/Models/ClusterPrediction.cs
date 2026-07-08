using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Logic.Models
{
	public class ClusterPrediction
	{
		public string Cluster { get; set; } = string.Empty;
		public double Probability { get; set; }
	}
}