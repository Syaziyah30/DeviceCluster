using System;
using System.Collections.Generic;
using System.Text;
using Model.ModelRequest;

namespace Model.ModelRequest
{
public class PipelinePredictRequest
{
    public List<PipelineRecord> records { get; set; }
    public string? export_raw_csv_path { get; set; }   // ◄── ADDED
}