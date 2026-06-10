using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceIdentifierLibrary.ModelResult
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
}
