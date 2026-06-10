using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeviceIdentifierLibrary.Models;
using DeviceIdentifierLibrary.Services;

public class Program
{
	private static readonly string _baseDir = AppContext.BaseDirectory;
	private static readonly string _projectDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\"));
	private static readonly string _serviceDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\..\"));

	// using relative path
	private static readonly string PYTHON_EXE = Environment.GetEnvironmentVariable("PYTHON_EXE") ?? "python";
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");
	private static readonly string PROJECT_JSON = Path.Combine(_serviceDir, "TestDevice", "A1825.json");
	private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

	public static async Task Main(string[] args)
	{
		try
		{
			var client = new PythonClient(PYTHON_EXE); // ← PythonClient comes from DLL

			if (!File.Exists(PROJECT_JSON))
				throw new FileNotFoundException($"Project JSON not found: {PROJECT_JSON}");

			var request = JsonSerializer.Deserialize<DevicePredictRequest>( // ← DevicePredictRequest comes from DLL
				File.ReadAllText(PROJECT_JSON, Encoding.UTF8)
			);

			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			// STEP 1: Device Type
			Console.WriteLine("[Step 1/3] Predicting device types...");
			var sw = Stopwatch.StartNew();
			string typeJson = await client.RunAsync(SCRIPT_TYPE, request);
			sw.Stop();

			var typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, _jsonOpts); // <-DeviceTypeResult from DLL
			PrintDeviceTypeTable(typeResults);
			Console.WriteLine($"Time taken: {sw.Elapsed.TotalSeconds:F1} secs");

			var deviceTypeLookup = typeResults
				.GroupBy(r => r.data_id)
				.ToDictionary(g => g.Key, g => g.Last().data_type ?? "N/A");

			Console.Write("\nPress Enter to predict Section...");
			Console.ReadLine();

			// STEP 2: Section
			Console.WriteLine("[Step 2/3] Predicting sections...");
			var pipelineRequest = new PipelinePredictRequest            // <- PipelinePredictRequest from DLL
			{
				records = typeResults.Select(r => new PipelineRecord   // <- PipelineRecord from DLL
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

			var pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, _jsonOpts); // <-PipelineResult from DLL
			PrintSectionTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step2Secs:F1} secs");

			Console.Write("\nPress Enter to predict Cluster...");
			Console.ReadLine();

			// STEP 3: Cluster
			Console.WriteLine("[Step 3/3] Predicting clusters...");
			PrintClusterTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step2Secs:F1} secs");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"\nERROR: {ex.Message}\n{ex.StackTrace}");
		}
		finally
		{
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}
	}

	private static void PrintDeviceTypeTable(List<DeviceTypeResult> results) // <-DeviceTypeResult from DLL
	{
		Console.WriteLine("\n===== STEP 1: DEVICE TYPE =====\n");
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Confidence",10}");
		Console.WriteLine(new string('-', 80));
		foreach (var r in results)
		{
			string conf = r.confidence.HasValue ? r.confidence.Value.ToString("F3") : "N/A";
			Console.WriteLine($"{r.customer,-12} | {r.data_id,-25} | {r.data_type,-25} | {conf,10}");
		}
	}

	private static void PrintSectionTable(List<PipelineResult> results, Dictionary<string, string> lookup) // <-PipelineResult from DLL
	{
		Console.WriteLine("\n===== STEP 2: SECTION =====\n");
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Section",-20} | {"Confidence %",12}");
		Console.WriteLine(new string('-', 110));
		foreach (var r in results)
		{
			string devType = lookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "N/A";
			string conf = r.SECTION_CONFIDENCE.HasValue ? r.SECTION_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine($"{r.CUSTOMER,-12} | {r.DEVICE_ID,-25} | {devType,-25} | {r.PREDICTED_SECTION,-20} | {conf,12}");
		}
	}

	private static void PrintClusterTable(List<PipelineResult> results, Dictionary<string, string> lookup) // <-PipelineResult from DLL
	{
		Console.WriteLine("\n===== STEP 3: CLUSTER =====\n");
		Console.WriteLine($"{"Customer",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Section",-20} | {"Cluster",-20} | {"Confidence %",12}");
		Console.WriteLine(new string('-', 130));
		foreach (var r in results)
		{
			string devType = lookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "N/A";
			string conf = r.CLUSTER_CONFIDENCE.HasValue ? r.CLUSTER_CONFIDENCE.Value.ToString("F2") + "%" : "N/A";
			Console.WriteLine($"{r.CUSTOMER,-12} | {r.DEVICE_ID,-25} | {devType,-25} | {r.PREDICTED_SECTION,-20} | {r.PREDICTED_CLUSTER,-20} | {conf,12}");
		}
	}
}