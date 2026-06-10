using System;
using System.Collections.Generic;
using System.Text;
using DeviceIdentifierLibrary.Models;

namespace DeviceIdentifierLibrary.ModelRequest
{
	public class PipelinePredictRequest
	{
		public List<PipelineRecord> records { get; set; }
	}
}
