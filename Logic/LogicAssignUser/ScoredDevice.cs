using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Logic.Models;

namespace Logic.LogicAssignUser
{
	public class ScoredDevice
	{
		public DeviceResult Device { get; set; } = null!;
		public double Score { get; set; }  // type match % within cluster
	}
}
