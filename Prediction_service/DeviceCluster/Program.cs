using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

#region Request Models

public class DevicePredictRequest
{
	public string project_code { get; set; }
	public string customer_code { get; set; }
	public List<string> data_ids { get; set; }
}

public class ManualAssignment
{
	public string data_id { get; set; }
	public string equipment { get; set; }
}

public class UserManualAssignRequest
{
	public string action { get; set; } = "user_manual_assign";
	public string project_code { get; set; }
	public string customer { get; set; }
	public List<ManualAssignment> assignments { get; set; }
}

// Step 2+3 — pipeline request sent to predict_pipeline.py
public class PipelinePredictRequest
{
	public List<PipelineRecord> records { get; set; }
}

public class PipelineRecord
{
	public string device_id { get; set; }
	public string customer { get; set; }
	public string project { get; set; }
}

#endregion

#region Result Models

// Step 1 — device type result from predict_equipment.py
public class DeviceTypeResult
{
	public string customer { get; set; }
	public string data_id { get; set; }
	public string manual_check { get; set; }
	public string data_type { get; set; }
	public double? confidence { get; set; }
	public string reason { get; set; }
}

// Step 2+3 — section and cluster result from predict_pipeline.py
public class PipelineResult
{
	public string DEVICE_ID { get; set; }
	public string CUSTOMER { get; set; }
	public string PROJECT { get; set; }
	public string PREDICTED_SECTION { get; set; }
	public double? SECTION_CONFIDENCE { get; set; }
	public string PREDICTED_CLUSTER { get; set; }
	public double? CLUSTER_CONFIDENCE { get; set; }
	public string REJECTION_REASON { get; set; }
	public string FORMAT_WARNING { get; set; }
}

#endregion

#region Python Client

public class PythonClient
{
	private readonly string _pythonExe;

	public PythonClient(string pythonExe)
	{
		_pythonExe = pythonExe;
	}

	public async Task<string> RunAsync(string scriptPath, object request)
	{
		string jsonInput = JsonSerializer.Serialize(request);

		var psi = new ProcessStartInfo
		{
			FileName = _pythonExe,
			Arguments = $"-u \"{scriptPath}\"",
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var process = new Process { StartInfo = psi };
		process.Start();

		await process.StandardInput.WriteAsync(jsonInput);
		process.StandardInput.Close();

		// Read stdout and stderr concurrently to avoid deadlock
		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();

		string output = await outputTask;
		string error = await errorTask;

		await process.WaitForExitAsync();

		if (process.ExitCode != 0)
			throw new Exception($"Python script error:\n{error}");

		return output.Trim();
	}
}

#endregion

#region Program

public class Program
{
	const string PYTHON_EXE = @"C:\Users\sitisyaziyah\AppData\Local\Programs\Python\Python313\python.exe";
	const string SCRIPT_TYPE = @"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceCluster\predict_equipment.py";
	const string SCRIPT_PIPELINE = @"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceCluster\predict_pipeline.py";
	const string PROJECT_JSON = @"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\TestDevice\A1825.json";

	static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

	public static async Task Main(string[] args)
	{
		try
		{
			var client = new PythonClient(PYTHON_EXE);

			if (!File.Exists(PROJECT_JSON))
				throw new FileNotFoundException($"Project JSON not found: {PROJECT_JSON}");

			var request = JsonSerializer.Deserialize<DevicePredictRequest>(
				File.ReadAllText(PROJECT_JSON, Encoding.UTF8)
			);

			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			// ── STEP 1: Device Type ───────────────────────────────────────────
			Console.WriteLine("[Step 1/3] Predicting device types...");
			string typeJson = await client.RunAsync(SCRIPT_TYPE, request);

			var typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, _jsonOpts);
			PrintDeviceTypeTable(typeResults);

			Console.Write("\nPress Enter to predict Section...");
			Console.ReadLine();

			// ── STEP 2: Section ───────────────────────────────────────────────
			Console.WriteLine("[Step 2/3] Predicting sections...");

			var pipelineRequest = new PipelinePredictRequest
			{
				records = typeResults.Select(r => new PipelineRecord
				{
					device_id = r.data_id,
					customer = r.customer ?? request.customer_code,
					project = request.project_code
				}).ToList()
			};

			string pipelineJson = await client.RunAsync(SCRIPT_PIPELINE, pipelineRequest);

			var pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, _jsonOpts);
			PrintSectionTable(pipelineResults);

			Console.Write("\nPress Enter to predict Cluster...");
			Console.ReadLine();

			// ── STEP 3: Cluster (reuses Step 2 results) ──────────────────────
			Console.WriteLine("[Step 3/3] Cluster predictions:");
			PrintClusterTable(pipelineResults);
		}
		catch (Exception ex)
		{
			Console.WriteLine("\n========== ERROR ==========");
			Console.WriteLine($"Type   : {ex.GetType().FullName}");
			Console.WriteLine($"Message: {ex.Message}");
			Console.WriteLine($"\nStack Trace:\n{ex.StackTrace}");
			if (ex.InnerException != null)
			{
				Console.WriteLine($"\nInner Exception: {ex.InnerException.Message}");
				Console.WriteLine(ex.InnerException.StackTrace);
			}
			System.Diagnostics.Debug.WriteLine(ex.ToString());
		}
		finally
		{
			Console.WriteLine("\n\nPress Enter to exit...");
			Console.ReadLine();
		}
	}

	// ── Display Helpers ───────────────────────────────────────────────────────

	static void PrintDeviceTypeTable(List<DeviceTypeResult> results)
	{
		const int W = 106;
		Console.WriteLine();
		Console.WriteLine("===== STEP 1: DEVICE TYPE =====");
		Console.WriteLine();
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Confidence",10} | {"Reason",-20}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string conf = r.confidence.HasValue ? r.confidence.Value.ToString("F3") : "N/A";
			Console.WriteLine(
				$"{r.customer,-12} | " +
				$"{r.data_id,-25} | " +
				$"{r.data_type,-25} | " +
				$"{conf,10} | " +
				$"{r.reason,-20}"
			);
		}
	}

	static void PrintSectionTable(List<PipelineResult> results)
	{
		const int W = 110;
		Console.WriteLine();
		Console.WriteLine("===== STEP 2: SECTION =====");
		Console.WriteLine();
		Console.WriteLine($"{"Device ID",-25} | {"Customer",-12} | {"Section",-20} | {"Confidence %",12} | {"Warning",-30}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string conf = r.SECTION_CONFIDENCE.HasValue ? r.SECTION_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine(
				$"{r.DEVICE_ID,-25} | " +
				$"{r.CUSTOMER,-12} | " +
				$"{r.PREDICTED_SECTION,-20} | " +
				$"{conf,12} | " +
				$"{r.FORMAT_WARNING,-30}"
			);
		}
	}

	static void PrintClusterTable(List<PipelineResult> results)
	{
		const int W = 110;
		Console.WriteLine();
		Console.WriteLine("===== STEP 3: CLUSTER =====");
		Console.WriteLine();
		Console.WriteLine($"{"Device ID",-25} | {"Customer",-12} | {"Cluster",-20} | {"Confidence %",12} | {"Rejection Reason",-30}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string conf = r.CLUSTER_CONFIDENCE.HasValue ? r.CLUSTER_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine(
				$"{r.DEVICE_ID,-25} | " +
				$"{r.CUSTOMER,-12} | " +
				$"{r.PREDICTED_CLUSTER,-20} | " +
				$"{conf,12} | " +
				$"{r.REJECTION_REASON,-30}"
			);
		}
	}
}

#endregion