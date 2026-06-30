using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// derived from dll library
using DeviceIdentifierLibrary.ModelRequest;
using DeviceIdentifierLibrary.ModelResult;
using DeviceIdentifierLibrary.Services;

public class Program
{
	private static readonly string _baseDir = AppContext.BaseDirectory;
	private static readonly string _projectDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\"));
	private static readonly string _serviceDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\.."));

	private static readonly string PYTHON_EXE = Environment.GetEnvironmentVariable("PYTHON_EXE") ?? "python";
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");
	private static readonly string SQL_OUTPUT_JSON = Path.Combine(_serviceDir, "data", "devices.json"); 
	private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

	private const string PROJECT_NAME = "XenxibleIdentifier";
	private const string PROJECT_FOLDER = "Software";

	private static string GetConnectionString()
	{
		Dictionary<string, object?> registryValues = AppRegistryEditor
			.RegistryEditor
			.GetAllRegistry(PROJECT_NAME, null);

		if (!registryValues.TryGetValue("connectionstring", out object? value) || value is null)
			throw new InvalidOperationException(
				$"Registry key 'connectionstring' not found under " +
				$"HKEY_CURRENT_USER\\{PROJECT_FOLDER}\\{PROJECT_NAME}.");

		string connectionString = value.ToString()!;

		if (string.IsNullOrWhiteSpace(connectionString))
			throw new InvalidOperationException("'connectionstring' in registry is empty.");

		return connectionString;
	}

	public static async Task Main(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;

		DevicePredictRequest? request = null;
		PythonClient? client = null;

		List<DeviceTypeResult>? typeResults = null;
		List<PipelineResult>? pipelineResults = null;

		try
		{
			client = new PythonClient(PYTHON_EXE);

			// ── PREPARATION 1/3: Load full reference data from SQL → save as JSON ────
			Console.WriteLine("[Preparation 1/3] Loading reference data from SQL Server...");
			string SQL_CONN = GetConnectionString();
			var sqlReader = new PythonSQL(SQL_CONN);
			await sqlReader.QueryToJsonFileAsync("SELECT * FROM DummyInput", SQL_OUTPUT_JSON);
			Console.WriteLine($"[Preparation 1/3] Reference data saved → {SQL_OUTPUT_JSON}\n");


			// ── PREPARATION 2/3: Auto-detect project + load DataIds from SQL ─────────
			Console.WriteLine("[Preparation 2/3] Detecting project from SQL Server...");

			// QueryToJsonAsync already returns { project_code, customer_code, data_ids[] }
			// so deserialize directly into DevicePredictRequest
			string requestJson = await sqlReader.QueryToJsonAsync(
				"SELECT ProjectCode, CustomerCode, DataIds FROM DummyInput" 
			);

			request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, _jsonOpts); 

			if (request == null || request.data_ids == null || request.data_ids.Count == 0)
				throw new InvalidOperationException("No project data found in DummyInput table.");

			// BOM cleanup
			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			Console.WriteLine($"[Preparation 2/3] Project detected : {request.project_code} ({request.customer_code})");
			Console.WriteLine($"[Preparation 2/3] Loaded {request.data_ids.Count} device IDs\n");


			// ── PREPARATION 3/3: Equipment list ──────────────────────────────────────
			Console.WriteLine("[Preparation 3/3] Importing equipment list from SQL Server...");
			Console.WriteLine("[Preparation 3/3] Skipped — equipment table not available yet.\n");


			// ── STEP 1: Device Type ───────────────────────────────────────────────────
			Console.WriteLine("[Model Prediction Step 1/3] Predicting device types...");
			var sw = Stopwatch.StartNew();
			string typeJson = await client.RunAsync(SCRIPT_TYPE, request);
			sw.Stop();

			typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, _jsonOpts);
			PrintDeviceTypeTable(typeResults);
			Console.WriteLine($"Time taken: {sw.Elapsed.TotalSeconds:F1} secs");

			var deviceTypeLookup = typeResults
				.GroupBy(r => r.data_id)
				.ToDictionary(g => g.Key, g => g.Last().data_type ?? "N/A");

			Console.Write("\nPress Enter to predict Section...");
			Console.ReadLine();


			// ── STEP 2: Section ───────────────────────────────────────────────────────
			Console.WriteLine("[Model Prediction Step 2/3] Predicting sections...");
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

			pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, _jsonOpts);
			PrintSectionTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step2Secs:F1} secs");

			Console.Write("\nPress Enter to predict Cluster...");
			Console.ReadLine();


			// ── STEP 3: Cluster ───────────────────────────────────────────────────────
			Console.WriteLine("[Model Prediction Step 3/3] Predicting clusters...");
			PrintClusterTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step2Secs:F1} secs");


			// ── OUTPUT RESULT: Manual Correction ─────────────────────────────────────
			Console.Write("\n[OUTPUT RESULT] Correct any prediction? (y/n): ");
			string? userInput = Console.ReadLine();

			while (userInput?.Trim().ToLower() == "y")
			{
				Console.Write("Device ID to correct: ");
				string? deviceId = Console.ReadLine()?.Trim().ToUpper();

				if (!string.IsNullOrEmpty(deviceId))
				{
					var matchedType = typeResults?.FirstOrDefault(r => r.data_id == deviceId);
					var matchedPipeline = pipelineResults?.FirstOrDefault(r => r.DEVICE_ID == deviceId);

					if (matchedType == null && matchedPipeline == null)
					{
						Console.WriteLine($"[OUTPUT RESULT] Device ID '{deviceId}' not found in results.");
					}
					else
					{
						bool typeIsUnknown = matchedType?.data_type?.ToUpper() == "UNKNOWN" || matchedType == null;
						bool sectionIsUnknown = matchedPipeline?.PREDICTED_SECTION?.ToUpper() == "UNKNOWN" || matchedPipeline == null;
						bool clusterIsUnknown = matchedPipeline?.PREDICTED_CLUSTER?.ToUpper() == "UNKNOWN" || matchedPipeline == null;

						if (!typeIsUnknown && !sectionIsUnknown && !clusterIsUnknown)
						{
							Console.WriteLine($"[OUTPUT RESULT] '{deviceId}' has no UNKNOWN fields. No correction needed.");
						}
						else
						{
							string? correctType = null;
							string? correctSection = null;
							string? correctCluster = null;

							if (typeIsUnknown)
							{
								Console.Write("Correct equipment type    : ");
								string? rawType = Console.ReadLine()?.Trim();                                                    
								correctType = string.IsNullOrEmpty(rawType) ? rawType
											: char.ToUpper(rawType[0]) + rawType.Substring(1).ToLower();                        

							}
							if (sectionIsUnknown)
							{
								Console.Write("Correct equipment section : ");
								correctSection = Console.ReadLine()?.Trim().ToUpper();
							}
							if (clusterIsUnknown)
							{
								Console.Write("Correct equipment cluster : ");
								correctCluster = Console.ReadLine()?.Trim().ToUpper();
							}

							if (!string.IsNullOrEmpty(correctType))
							{
								var assignPayload = new
								{
									action = "user_manual_assign",
									project_code = request!.project_code,
									customer = request!.customer_code,
									assignments = new[]
									{
										new { data_id = deviceId, equipment = correctType }
									}
								};

								Console.WriteLine($"[OUTPUT RESULT] Sending type correction for '{deviceId}'...");
								string assignResult = await client!.RunAsync(SCRIPT_TYPE, assignPayload);
								Console.WriteLine($"[OUTPUT RESULT] Done: {assignResult}\n");
							}

							Console.WriteLine($"[OUTPUT RESULT] Correction summary for '{deviceId}':");
							if (typeIsUnknown) Console.WriteLine($"  Type    : {correctType ?? "(skipped)"}");
							if (sectionIsUnknown) Console.WriteLine($"  Section : {correctSection ?? "(skipped)"}  ← logged only, pending section model support");
							if (clusterIsUnknown) Console.WriteLine($"  Cluster : {correctCluster ?? "(skipped)"}  ← logged only, pending cluster model support");
						}
					}
				}

				Console.Write("\n[OUTPUT RESULT] Correct any prediction? (y/n): ");
				userInput = Console.ReadLine();
			}
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

	private static void PrintDeviceTypeTable(List<DeviceTypeResult> results)
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

	private static void PrintSectionTable(List<PipelineResult> results, Dictionary<string, string> lookup)
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

	private static void PrintClusterTable(List<PipelineResult> results, Dictionary<string, string> lookup)
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