using System;
using System.Collections.Generic;
using System.Text;
using Model.ModelRequest;

namespace Model.ModelRequest
{
    public class PipelinePredictRequest
    {
        public List<PipelineRecord> records { get; set; }
    }
}