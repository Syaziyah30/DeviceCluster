using System.Collections.Generic;

namespace Logic.LogicAssignUser
{
	public class ClusterGroup
	{
		public string Section { get; set; } = string.Empty;
		public string Cluster { get; set; } = string.Empty;
		public List<ScoredDevice> Devices { get; set; } = new();
	}
}
