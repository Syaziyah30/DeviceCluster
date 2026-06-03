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

public class DeviceTypeResult
{
	public string customer { get; set; }
	public string data_id { get; set; }
	public string manual_check { get; set; }
	public string data_type { get; set; }
	public double? confidence { get; set; }
	public string reason { get; set; }
}

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
	private readonly string _pythonExe; // Private — unchanged

	public PythonClient(string pythonExe)
	{
		_pythonExe = pythonExe;
	}

	// ✅ Keep public — external callers (Program) depend on this
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

		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();

		try
		{
			await process.StandardInput.WriteAsync(jsonInput);
			process.StandardInput.Close();
		}
		catch (IOException)
		{
			// Python exited before reading input
		}

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

	private static readonly string _baseDir = AppContext.BaseDirectory;

	private static readonly string _projectDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\"));

	private static readonly string _serviceDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\..\"));

	private const string PYTHON_EXE = @"C:\Users\sitisyaziyah\AppData\Local\Programs\Python\Python313\python.exe";

	// only passed to RunAsync() inside Main()
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");

	// only passed to RunAsync() inside Main()
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");

	// only read inside Main()
	private static readonly string PROJECT_JSON = Path.Combine(_serviceDir, "TestDevice", "A1825.json");

	// only used inside Program for deserialization
	private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

	// Public — entry point, must remain accessible by the runtime
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
			var sw = Stopwatch.StartNew();
			string typeJson = await client.RunAsync(SCRIPT_TYPE, request);
			sw.Stop();

			var typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, _jsonOpts);
			PrintDeviceTypeTable(typeResults);
			Console.WriteLine($"Time taken to predict : {sw.Elapsed.TotalSeconds:F1} secs");

			// Build a lookup: device_id → data_type  (used in Step 2 & 3 display)
			var deviceTypeLookup = typeResults
				.GroupBy(r => r.data_id)
				.ToDictionary(
					g => g.Key,
					g => g.Last().data_type ?? "N/A"
				);

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

			sw.Restart();
			string pipelineJson = await client.RunAsync(SCRIPT_PIPELINE, pipelineRequest);
			sw.Stop();
			double step2Secs = sw.Elapsed.TotalSeconds;

			var pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, _jsonOpts);
			PrintSectionTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken to predict : {step2Secs:F1} secs");

			Console.Write("\nPress Enter to predict Cluster...");
			Console.ReadLine();

			// ── STEP 3: Cluster ───────────────────────────────────────────────
			Console.WriteLine("[Step 3/3] Predicting clusters...");
			PrintClusterTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken to predict : {step2Secs:F1} secs");
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
		}
		finally
		{
			Console.WriteLine("\n\nPress Enter to exit...");
			Console.ReadLine();
		}
	}

	// ── Display Helpers ───────────────────────────────────────────────────────

	// Private — display helper, only called inside Main()
	private static void PrintDeviceTypeTable(List<DeviceTypeResult> results)
	{
		const int W = 80;
		Console.WriteLine();
		Console.WriteLine("===== STEP 1: DEVICE TYPE =====");
		Console.WriteLine();
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Confidence",10}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string conf = r.confidence.HasValue ? r.confidence.Value.ToString("F3") : "N/A";
			Console.WriteLine(
				$"{r.customer,-12} | " +
				$"{r.data_id,-25} | " +
				$"{r.data_type,-25} | " +
				$"{conf,10}"
			);
		}
	}

	// Private — display helper, only called inside Main()
	private static void PrintSectionTable(List<PipelineResult> results, Dictionary<string, string> deviceTypeLookup)
	{
		const int W = 110;
		Console.WriteLine();
		Console.WriteLine("===== STEP 2: SECTION =====");
		Console.WriteLine();
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Section",-20} | {"Confidence %",12}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string devType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "N/A";
			string conf = r.SECTION_CONFIDENCE.HasValue ? r.SECTION_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine(
				$"{r.CUSTOMER,-12} | " +
				$"{r.DEVICE_ID,-25} | " +
				$"{devType,-25} | " +
				$"{r.PREDICTED_SECTION,-20} | " +
				$"{conf,12}"
			);
		}
	}

	// Private — display helper, only called inside Main()
	private static void PrintClusterTable(List<PipelineResult> results, Dictionary<string, string> deviceTypeLookup)
	{
		const int W = 130;
		Console.WriteLine();
		Console.WriteLine("===== STEP 3: CLUSTER =====");
		Console.WriteLine();
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Section",-20} | {"Cluster",-20} | {"Confidence %",12}");
		Console.WriteLine(new string('-', W));

		foreach (var r in results)
		{
			string devType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "N/A";
			string conf = r.CLUSTER_CONFIDENCE.HasValue ? r.CLUSTER_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine(
				$"{r.CUSTOMER,-12} | " +
				$"{r.DEVICE_ID,-25} | " +
				$"{devType,-25} | " +
				$"{r.PREDICTED_SECTION,-20} | " +
				$"{r.PREDICTED_CLUSTER,-20} | " +
				$"{conf,12}"
			);
		}
	}
}

#endregion