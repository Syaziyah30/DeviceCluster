using System;
using System.Collections.Generic;
using System.Text;

namespace Model.ModelRequest
{
	public class UserManualAssignRequest // no usage in Program.cs
	{
		public string action { get; set; } = "user_manual_assign";
		public string project_code { get; set; }
		public string customer { get; set; }
		public List<ManualAssignment> assignments { get; set; }
	}
}
