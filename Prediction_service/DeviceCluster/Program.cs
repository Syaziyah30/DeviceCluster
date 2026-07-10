using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// derived from dll library
using Model.ModelRequest;
using Model.ModelResult;
using Model.Services;

// ◄── Logic.dll
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
	private static readonly string UNKNOWN_DUMP = Path.Combine(_serviceDir, "data", "unknown_dump.json"); 
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

	// PromptYesNo : keeps asking until the user enters a valid y/n 
	private static string PromptYesNo(string prompt)
	{
		string? input;
		while (true)
		{
			Console.Write(prompt);
			input = Console.ReadLine()?.Trim().ToLower();

			if (input == "y" || input == "n")
				return input;

			Console.WriteLine("[OUTPUT RESULT] Invalid input. Please enter 'y' or 'n'.\n");
		}
	}

	// PromptRequiredText : keeps asking until non-blank input, or returns null if user types "E" to exit
	private static string? PromptRequiredText(string prompt, string fieldLabel)
	{
		string? input;
		while (true)
		{
			Console.Write(prompt);
			input = Console.ReadLine()?.Trim();

			if (!string.IsNullOrWhiteSpace(input))
			{
				if (input.Equals("E", StringComparison.OrdinalIgnoreCase))
					return null;

				return input;
			}

			Console.WriteLine($"[OUTPUT RESULT] WRONG INFORMATION. {fieldLabel} cannot be blank. Type 'E' to exit or try again to fill up\n");
		}
	}

	// PromptSection : keeps asking until valid "SECTION ..." input, or returns null if user types "E" to exit
	private static string? PromptSection(string prompt)
	{
		string? input;
		while (true)
		{
			Console.Write(prompt);
			input = Console.ReadLine()?.Trim().ToUpper();

			if (input == "E")
				return null;

			if (!string.IsNullOrWhiteSpace(input) && input.StartsWith("SECTION"))
				return input;

			Console.WriteLine("[OUTPUT RESULT] WRONG INFORMATION. Section must begin with 'SECTION' (e.g. SECTION 1). Type 'E' to exit or try again to fill up\n");
		}
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
			var clusterService = new ModelClusterSuggestionService(client, SCRIPT_PIPELINE);   

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
			Console.WriteLine("\n[Step 4/8] Passing results into Logic.dll...");   
			var logic = new LogicAssignment(UNKNOWN_DUMP);                         

			// Build DeviceResult list from model outputs                          
			var allDeviceResults = pipelineResults.Select(r => new DeviceResult    
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
			Console.WriteLine("[Step 5/8] Splitting KNOWN vs UNKNOWN devices..."); 
			var (knownDevices, unknownDevices) = logic.SplitKnownUnknown(allDeviceResults);
			Console.WriteLine();


			// ── STEP 6: UNKNOWN → dumped to JSON ──────────────────────────────────────
			Console.WriteLine("[Step 6/8] Dumping UNKNOWN devices to JSON...");    
			logic.DumpUnknown(unknownDevices);                                     
			Console.WriteLine($"[Step 6/8] Dump file → {UNKNOWN_DUMP}\n");


			// ── STEP 7: KNOWN → placed into cluster groups ────────────────────────────
			Console.WriteLine("[Step 7/8] Building cluster groups from KNOWN devices...");
			var clusterGroups = logic.BuildClusterGroups(knownDevices);            
			Console.WriteLine($"[Step 7/8] {clusterGroups.Count} cluster groups built\n");


			// ── STEP 8: Print cluster grouping table ──────────────────────────────────
			Console.WriteLine("[Step 8/8] Printing cluster grouping table...");
			logic.PrintClusterTable(clusterGroups);


			// ── STEP 9: Print UNKNOWN dump table ────────────────────────────────────── 
			Console.WriteLine($"\n[Step 9] UNKNOWN devices pending manual assignment on [Date: {DateTime.Now:yyyy-MM-dd}]:\n");

			if (unknownDevices.Count == 0)
			{
				Console.WriteLine("[Step 9] No unknown devices found.\n");
			}
			else
			{
				Console.WriteLine($"{"DumpedAt",-10} | {"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
				Console.WriteLine(new string('-', 130));

				// ◄── Sort by most UNKNOWN fields first
				var sortedUnknown = unknownDevices
					.OrderByDescending(u => u.DeviceType == "UNKNOWN" ? 1 : 0)
					.ThenByDescending(u => u.Section == "UNKNOWN" ? 1 : 0)
					.ThenByDescending(u => u.Cluster == "UNKNOWN" ? 1 : 0)
					.ToList();

				foreach (var u in sortedUnknown)                                           // ◄── MODIFIED: was unknownDevices
				{
					Console.WriteLine(
						$"{DateTime.Now.ToString("HH:mm:ss"),-10} | " +
						$"{u.Customer,-10} | " +
						$"{u.ProjectCode,-12} | " +
						$"{u.DeviceId,-25} | " +
						$"{u.DeviceType,-25} | " +
						$"{u.Section,-15} | " +
						$"{u.Cluster,-15} ");
				}
				Console.WriteLine($"\n[Step 9] Total unknown: {unknownDevices.Count} devices → saved to {UNKNOWN_DUMP}\n");
			}



			// ── OUTPUT RESULT: Manual Correction ─────────────────────────────────────_
			string userInput = PromptYesNo("\n[OUTPUT RESULT] Correct any prediction? (y/n): ");

			while (userInput == "y")
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


							// typeIsUnknown
							if (typeIsUnknown)
							{
								string? rawType = PromptRequiredText("Correct equipment type    : ", "Equipment type");
								if (rawType == null)
								{
									Console.WriteLine("[OUTPUT RESULT] Correction cancelled by user.\n");
									goto NextCorrection;
								}
								correctType = char.ToUpper(rawType[0]) + rawType.Substring(1).ToLower();
							}
							if (sectionIsUnknown)
							{
								correctSection = PromptSection("Correct equipment section : ");
								if (correctSection == null)
								{
									Console.WriteLine("[OUTPUT RESULT] Correction cancelled by user.\n");
									goto NextCorrection;
								}
							}


							// clusterIsUnknown 
							if (clusterIsUnknown)
							{
								// Show top 3 suggested clusters (model-driven, via predict_sectioncluster.py)
								var suggestions = await clusterService.GetTopClustersAsync(deviceId, request!.customer_code, request!.project_code);
								if (suggestions.Count > 0)
								{
									Console.WriteLine("\n  Top 3 suggested clusters by model confidence:");
									for (int i = 0; i < suggestions.Count; i++)
									{
										var s = suggestions[i];
										Console.WriteLine($"  [{i + 1}] {s.Section,-12} | {s.Cluster,-12} " +
														  $"→ example: {s.ClosestDeviceId,-15} " +
														  $"(confidence: {s.Confidence:F2}%)");
									}

									string deviceTypeForDisplay = correctType ?? matchedType?.data_type ?? "UNKNOWN";   // ◄── NEW
									PrintSectionWithSuggestions(clusterGroups, suggestions, deviceId, deviceTypeForDisplay);   // ◄── MODIFIED

									Console.Write($"\n  Enter cluster number [1-{suggestions.Count}] or type manually: ");
									string? clusterInput = Console.ReadLine()?.Trim();

									if (int.TryParse(clusterInput, out int pick) && pick >= 1 && pick <= suggestions.Count)
										correctCluster = suggestions[pick - 1].Cluster;
									else
										correctCluster = clusterInput?.ToUpper();
								}
								else
								{
									Console.Write("Correct equipment cluster : ");
									correctCluster = Console.ReadLine()?.Trim().ToUpper();
								}
							}



							// ── Send type correction to Python only if type was corrected ─────────
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
							}

							// ── Run Logic placement for ANY correction ────────────────────────────
							string resolvedType = correctType ?? deviceTypeLookup.GetValueOrDefault(deviceId, "UNKNOWN");
							string resolvedSection = correctSection ?? matchedPipeline?.PREDICTED_SECTION ?? "UNKNOWN";
							string resolvedCluster = correctCluster ?? matchedPipeline?.PREDICTED_CLUSTER ?? "UNKNOWN";

							var correctedEntry = new UnknownDumpEntry
							{
								Customer = request!.customer_code,
								ProjectCode = request!.project_code,
								DeviceId = deviceId,
								DeviceType = resolvedType,
								PredictedSection = resolvedSection,
								PredictedCluster = resolvedCluster,
								Status = "assigned"
							};

							var placed = logic.AssignByNumericSimilarity(correctedEntry, knownDevices);
							if (placed != null)
							{
								logic.PlaceDevice(placed, clusterGroups);
								logic.MarkAsAssigned(deviceId, request!.project_code, placed.Section, placed.Cluster);
								Console.WriteLine("\n[Logic] Updated cluster grouping after correction:");
								logic.PrintClusterTable(clusterGroups, placed.Section);
							}

							Console.WriteLine($"[OUTPUT RESULT] Correction summary for '{deviceId}':");
							if (typeIsUnknown) Console.WriteLine($"  Type    : {correctType ?? "(skipped)"}");
							if (sectionIsUnknown) Console.WriteLine($"  Section : {correctSection ?? "(skipped)"}  ← logged only, pending section model support");
							if (clusterIsUnknown) Console.WriteLine($"  Cluster : {correctCluster ?? "(skipped)"}  ← logged only, pending cluster model support");
						}
					}
				}

				NextCorrection:
				userInput = PromptYesNo("\n[OUTPUT RESULT] Correct any prediction? (y/n): ");
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


	private static void PrintSectionWithSuggestions(
		List<ClusterGroup> clusterGroups,
		List<Model.ModelResult.ClusterSuggestionResult> suggestions,
		string deviceId,
		string deviceType)
	{
		string section = suggestions[0].Section;

		Console.WriteLine($"\n  Existing devices already placed in {section}:");
		Console.WriteLine($"===== LOGIC: CLUSTER GROUPING — {section} =====\n");
		Console.WriteLine($"{section} {new string('-', 88)}");
		Console.WriteLine($"{"Section",-12} | {"Cluster",-12} | {"Device ID",-25} | {"Device Type",-25} | {"Score %",10}");
		Console.WriteLine(new string('-', 95));

		var sectionGroups = clusterGroups
			.Where(g => g.Section == section)
			.OrderBy(g => g.Cluster)
			.ToList();

		foreach (var g in sectionGroups)
		{
			Console.WriteLine($"\n{section} {g.Cluster} (total Device ID = {g.Devices.Count})");   // ◄── MODIFIED

			var matched = suggestions.FirstOrDefault(s => s.Cluster == g.Cluster);

			var rows = g.Devices
				.Select(sd => (Id: sd.Device.DeviceId, Type: sd.Device.DeviceType, Score: sd.Score, IsSuggestion: false))
				.ToList();

			if (matched != null)
				rows.Add((deviceId, deviceType, matched.Confidence, true));

			foreach (var row in rows.OrderByDescending(r => r.Score))
			{
				string tag = row.IsSuggestion ? "  -- CLUSTER SUGGESTION" : "";
				Console.WriteLine($"{section,-12} | {g.Cluster,-12} | {row.Id,-25} | {row.Type,-25} | {row.Score,9:F1}%{tag}");
			}
		}

		// ◄── Suggested clusters that don't have any existing devices yet
		var newClusters = suggestions.Where(s => !sectionGroups.Any(g => g.Cluster == s.Cluster)).ToList();
		foreach (var s in newClusters)
		{
			Console.WriteLine($"\n{section} {s.Cluster} (total Device ID = 0) (New cluster is generated)");   // ◄── MODIFIED
			Console.WriteLine($"{section,-12} | {s.Cluster,-12} | {deviceId,-25} | {deviceType,-25} | {s.Confidence,9:F2}%  -- CLUSTER SUGGESTION");
		}

		Console.WriteLine("\n\nTop 3 suggested clusters by model confidence:");
		for (int i = 0; i < suggestions.Count; i++)
		{
			var s = suggestions[i];
			Console.WriteLine($"  [{i + 1}] {s.Section,-12} | {s.Cluster,-12} → example: {s.ClosestDeviceId,-15} (confidence: {s.Confidence:F2}%)");
		}
	}
}

