using System;
using System.Collections.Generic;
using System.Text;

namespace Model.ModelRequest
{
	public class DevicePredictRequest
	{
		public string project_code { get; set; }
		public string customer_code { get; set; }
		public List<string> data_ids { get; set; }
	}
}



