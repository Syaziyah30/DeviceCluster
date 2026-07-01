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

// ◄── NEW: Logic.dll
using Logic;
using Logic.Models;
using Logic.LogicAssignUser;

public class Program
{
	private static readonly string _baseDir = AppContext.BaseDirectory;
	private static readonly string _projectDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\"));
	private static readonly string _serviceDir = Path.GetFullPath(Path.Combine(_baseDir, @"..\..\..\.."));

	private static readonly string PYTHON_EXE = Environment.GetEnvironmentVariable("PYTHON_EXE") ?? "python";
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");
	private static readonly string SQL_OUTPUT_JSON = Path.Combine(_serviceDir, "data", "devices.json");
	private static readonly string UNKNOWN_DUMP = Path.Combine(_serviceDir, "data", "unknown_dump.json"); // ◄── NEW: dump file path
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

			// ── STEP 1: SQL reads device IDs ──────────────────────────────────────────
			Console.WriteLine("[Step 1/8] Loading reference data from SQL Server...");
			string SQL_CONN = GetConnectionString();
			var sqlReader = new PythonSQL(SQL_CONN);
			await sqlReader.QueryToJsonFileAsync("SELECT * FROM DummyInput", SQL_OUTPUT_JSON);
			Console.WriteLine($"[Step 1/8] Reference data saved → {SQL_OUTPUT_JSON}\n");

			string requestJson = await sqlReader.QueryToJsonAsync(
				"SELECT ProjectCode, CustomerCode, DataIds FROM DummyInput"
			);

			request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, _jsonOpts);

			if (request == null || request.data_ids == null || request.data_ids.Count == 0)
				throw new InvalidOperationException("No project data found in DummyInput table.");

			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			Console.WriteLine($"[Step 1/8] Project detected : {request.project_code} ({request.customer_code})");
			Console.WriteLine($"[Step 1/8] Loaded {request.data_ids.Count} device IDs\n");


			// ── STEP 2: Predict device type ───────────────────────────────────────────
			Console.WriteLine("[Step 2/8] Predicting device types...");
			var sw = Stopwatch.StartNew();
			string typeJson = await client.RunAsync(SCRIPT_TYPE, request);
			sw.Stop();

			typeResults = JsonSerializer.Deserialize<List<DeviceTypeResult>>(typeJson, _jsonOpts);
			PrintDeviceTypeTable(typeResults);
			Console.WriteLine($"Time taken: {sw.Elapsed.TotalSeconds:F1} secs");

			var deviceTypeLookup = typeResults
				.GroupBy(r => r.data_id)
				.ToDictionary(g => g.Key, g => g.Last().data_type ?? "N/A");

			Console.Write("\nPress Enter to predict Section + Cluster...");
			Console.ReadLine();


			// ── STEP 3: Predict section + cluster ─────────────────────────────────────
			Console.WriteLine("[Step 3/8] Predicting sections...");
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
			double step3Secs = sw.Elapsed.TotalSeconds;

			pipelineResults = JsonSerializer.Deserialize<List<PipelineResult>>(pipelineJson, _jsonOpts);
			PrintSectionTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step3Secs:F1} secs");

			Console.Write("\nPress Enter to predict Cluster...");
			Console.ReadLine();

			Console.WriteLine("[Step 3/8] Predicting clusters...");
			PrintClusterTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step3Secs:F1} secs");

			Console.Write("\nPress Enter to run Logic...");
			Console.ReadLine();


			// ── STEP 4: Pass results into Logic.dll ───────────────────────────────────
			Console.WriteLine("\n[Step 4/8] Passing results into Logic.dll...");    // ◄── NEW
			var logic = new LogicAssignment(UNKNOWN_DUMP);                          // ◄── NEW

			// Build DeviceResult list from model outputs                           // ◄── NEW
			var allDeviceResults = pipelineResults.Select(r => new DeviceResult     // ◄── NEW
			{
				Customer = r.CUSTOMER,
				ProjectCode = request.project_code,
				DeviceId = r.DEVICE_ID,
				DeviceType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "UNKNOWN",
				Section = r.PREDICTED_SECTION ?? "UNKNOWN",
				Cluster = r.PREDICTED_CLUSTER ?? "UNKNOWN",
				Confidence = r.CLUSTER_CONFIDENCE ?? 0
			}).ToList();

			Console.WriteLine($"[Step 4/8] {allDeviceResults.Count} devices passed into Logic.dll\n");


			// ── STEP 5: Logic splits KNOWN vs UNKNOWN ─────────────────────────────────
			Console.WriteLine("[Step 5/8] Splitting KNOWN vs UNKNOWN devices...");  // ◄── NEW
			var (knownDevices, unknownDevices) = logic.SplitKnownUnknown(allDeviceResults); // ◄── NEW
			Console.WriteLine();


			// ── STEP 6: UNKNOWN → dumped to JSON ──────────────────────────────────────
			Console.WriteLine("[Step 6/8] Dumping UNKNOWN devices to JSON...");     // ◄── NEW
			logic.DumpUnknown(unknownDevices);                                      // ◄── NEW
			Console.WriteLine($"[Step 6/8] Dump file → {UNKNOWN_DUMP}\n");


			// ── STEP 7: KNOWN → placed into cluster groups ────────────────────────────
			Console.WriteLine("[Step 7/8] Building cluster groups from KNOWN devices..."); // ◄── NEW
			var clusterGroups = logic.BuildClusterGroups(knownDevices);             // ◄── NEW
			Console.WriteLine($"[Step 7/8] {clusterGroups.Count} cluster groups built\n");


			// ── STEP 8: Print cluster grouping table ──────────────────────────────────
			Console.WriteLine("[Step 8/8] Printing cluster grouping table...");     // ◄── NEW
			logic.PrintClusterTable(clusterGroups);                                 // ◄── NEW


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
									},
									batch_results = typeResults!.Select(r => new
									{
										data_id = r.data_id,
										data_type = r.data_type
									}).ToList()
								};

								Console.WriteLine($"[OUTPUT RESULT] Sending type correction for '{deviceId}'...");
								string assignResult = await client!.RunAsync(SCRIPT_TYPE, assignPayload);
								Console.WriteLine($"[OUTPUT RESULT] Done: {assignResult}\n");

								// ◄── NEW: after correction, place device into logic cluster
								var correctedEntry = new UnknownDumpEntry
								{
									Customer = request!.customer_code,
									ProjectCode = request!.project_code,
									DeviceId = deviceId,
									DeviceType = correctType,
									PredictedSection = correctSection ?? "UNKNOWN",
									PredictedCluster = correctCluster ?? "UNKNOWN",
									Status = "assigned"
								};

								var placed = logic.AssignByNumericSimilarity(correctedEntry, knownDevices);
								if (placed != null)
								{
									logic.PlaceDevice(placed, clusterGroups);
									Console.WriteLine("\n[Logic] Updated cluster grouping after correction:");
									logic.PrintClusterTable(clusterGroups);
								}
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
		Console.WriteLine("\n===== STEP 2: DEVICE TYPE =====\n");
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
		Console.WriteLine("\n===== STEP 3: SECTION =====\n");
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