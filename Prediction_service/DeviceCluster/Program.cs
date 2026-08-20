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
	private static readonly string SQL_SOURCE_TABLE = Environment.GetEnvironmentVariable("SQL_SOURCE_TABLE") ?? "DummyTestingData";
	private static readonly string SQL_QUOTA_TABLE = Environment.GetEnvironmentVariable("SQL_QUOTA_TABLE") ?? "dbo.PatternCluster";
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");
	private static readonly string SQL_OUTPUT_DIR = Path.Combine(_serviceDir, "data");
	private static readonly string FLOATING_DUMP = Path.Combine(_serviceDir, "data", "floating_deviceid.json");
	private static readonly string UNALLOCATED_DUMP = Path.Combine(_serviceDir, "data", "unallocated_device_ids.json");
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

	// PromptClusterChoice : keeps asking until a valid pick number or a "CLUSTER ..." string is entered, or "E" to exit
	private static string? PromptClusterChoice(string prompt, List<Model.ModelResult.ClusterSuggestionResult> suggestions)
	{
		string? input;
		while (true)
		{
			Console.Write(prompt);
			input = Console.ReadLine()?.Trim();

			if (string.IsNullOrWhiteSpace(input))
			{
				Console.WriteLine($"[OUTPUT RESULT] WRONG INFORMATION. Cluster cannot be blank. Enter [1-{suggestions.Count}], a cluster name (e.g. CLUSTER 1), or 'E' to exit.\n");
				continue;
			}

			if (input.Equals("E", StringComparison.OrdinalIgnoreCase))
				return null;

			if (int.TryParse(input, out int pick) && pick >= 1 && pick <= suggestions.Count)
				return suggestions[pick - 1].Cluster;

			string upper = input.ToUpper();
			if (upper.StartsWith("CLUSTER"))
				return upper;

			Console.WriteLine($"[OUTPUT RESULT] WRONG INFORMATION. Must be a number [1-{suggestions.Count}] or begin with 'CLUSTER'. Enter 'E' to exit.\n");
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
			string SQL_CONN = GetConnectionString();
			var sqlReader = new PythonSQL(SQL_CONN);

			// ProjectCode selects which project's rows to pull from the shared SQL table —
			// pass it as a CLI arg for automation, or leave blank to be prompted interactively.
			string? projectCodeArg = args.Length > 0 ? args[0].Trim().ToUpper() : null;
			string projectCode;

			if (!string.IsNullOrWhiteSpace(projectCodeArg))
			{
				projectCode = projectCodeArg;
			}
			else
			{
				var availableProjects = await sqlReader.ListAvailableProjectsAsync(SQL_SOURCE_TABLE);
				Console.WriteLine("\nProject Available:");
				foreach (var (code, customer) in availableProjects)
					Console.WriteLine($"  {code} ({customer})");
				Console.WriteLine();

				projectCode = PromptRequiredText("Enter Project Code to process: ", "Project Code")
					?? throw new InvalidOperationException("Project Code is required.");
			}

			client = new PythonClient(PYTHON_EXE);
			var clusterService = new ModelClusterSuggestionService(client, SCRIPT_PIPELINE);
			var logic = new LogicAssignment(FLOATING_DUMP, UNALLOCATED_DUMP);

			// ── STEP 1: SQL reads device IDs ──────────────────────────────────────────
			Console.WriteLine($"[Step 1/6] Loading reference data for project '{projectCode}' from SQL Server...");
			var (requestJson, sqlOutputJson) = await sqlReader.LoadProjectDataAsync(SQL_SOURCE_TABLE, projectCode, SQL_OUTPUT_DIR);
			Console.WriteLine($"[Step 1/6] Reference data saved → {sqlOutputJson}\n");

			request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, _jsonOpts);

			if (request == null || request.data_ids == null || request.data_ids.Count == 0)
				throw new InvalidOperationException($"No project data found in '{SQL_SOURCE_TABLE}' table.");

			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			Console.WriteLine($"[Step 1/6] Project detected : {request.project_code} ({request.customer_code})");
			Console.WriteLine($"[Step 1/6] Loaded {request.data_ids.Count} device IDs\n");


			// ── STEP 2: Predict device type ───────────────────────────────────────────
			Console.WriteLine("[Step 2/6] Predicting device types...");
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
			Console.WriteLine("[Step 3/6] Predicting sections...");
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

			Console.WriteLine("[Step 3/6] Predicting clusters...");
			PrintClusterTable(pipelineResults, deviceTypeLookup);
			Console.WriteLine($"Time taken: {step3Secs:F1} secs");

			Console.Write("\nPress Enter to run Logic...");
			Console.ReadLine();


			// ◄── STEP 3.5 — Quota allocation
			Console.WriteLine("\n[Step 3.5/6] Running quota-constrained cluster allocation...");

			var predictions = pipelineResults.Select(r => new DevicePrediction
			{
				Section = r.PREDICTED_SECTION ?? "UNKNOWN",
				Cluster = r.PREDICTED_CLUSTER ?? "UNKNOWN",
				DeviceId = r.DEVICE_ID,
				DeviceType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var predDt) ? predDt : "UNKNOWN",
				Score = r.CLUSTER_CONFIDENCE ?? 0,
				TopClusters = (r.TOP_CLUSTERS ?? new List<ClusterCandidate>())
					.Select(c => new ClusterPrediction { Cluster = c.Cluster, Probability = c.Probability })
					.ToList()
			}).ToList();

			var quotas = await QuotaCatalog.LoadQuotasFromDbAsync(SQL_CONN, SQL_QUOTA_TABLE, request.customer_code);

			var allocationResult = ClusterQuotaAllocator.Allocate(predictions, quotas);
			ClusterQuotaAllocator.PrintFulfilledReport(quotas, allocationResult.VacancyReport);
			ClusterQuotaAllocator.PrintVacancyReport(allocationResult.VacancyReport);
			Console.WriteLine($"[Step 3.5/6] {allocationResult.Assigned.Count} assigned, {allocationResult.Floating.Count} floating\n");

			// ── Floating devices → split by cause, print + dump via Logic.dll ─────────
			List<DeviceResult> unknownPredictionDevices = new();
			List<DeviceResult> unallocatedDevices = new();

			if (allocationResult.Floating.Count > 0)
			{
				var floatingDevices = allocationResult.Floating.Select(f => new DeviceResult
				{
					Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == f.DeviceId)?.CUSTOMER ?? request.customer_code,
					ProjectCode = request.project_code,
					DeviceId = f.DeviceId,
					DeviceType = f.DeviceType,
					Section = f.Section,
					Cluster = f.Cluster,
					Confidence = f.Score
				}).ToList();

				(unknownPredictionDevices, unallocatedDevices) = logic.SplitFloatingPool(floatingDevices);

				Console.WriteLine($"[Step 3.5/6] {allocationResult.Floating.Count} floating device ID(s) — not claimed by any quota bucket:\n");
				Console.WriteLine($"{"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
				Console.WriteLine(new string('-', 130));

				foreach (var d in floatingDevices)
				{
					Console.WriteLine(
						$"{d.Customer,-10} | {d.ProjectCode,-12} | {d.DeviceId,-25} | " +
						$"{d.DeviceType,-25} | {d.Section,-15} | {d.Cluster,-15}");
				}

				Console.WriteLine();
				logic.DumpFloating(unknownPredictionDevices);
				logic.DumpUnallocated(unallocatedDevices);
			}
			else
			{
				Console.WriteLine("[Step 3.5/6] No floating devices — all predictions claimed by quota allocation.\n");
			}


			Console.Write("\nPress Enter to run Logic...");
			Console.ReadLine();

			// ── STEP 4: Pass results into Logic.dll ───────────────────────────────────
			Console.WriteLine("\n[Step 4/6] Passing results into Logic.dll...");

			// Build DeviceResult list from model outputs

			// NEW : after inserting the Quota Allocation variable
			var allDeviceResults = allocationResult.Assigned.Select(a => new DeviceResult
			{
				Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == a.DeviceId)?.CUSTOMER ?? request.customer_code,
				ProjectCode = request.project_code,
				DeviceId = a.DeviceId,
				DeviceType = a.DeviceType,
				Section = a.Section,
				Cluster = a.Cluster,
				Confidence = a.Score
			}).ToList();

			Console.WriteLine($"[Step 4/6] {allDeviceResults.Count} devices passed into Logic.dll\n");

			// Every device here already matched a real quota bucket (Section+Cluster+DeviceType),
			// so it's guaranteed known — no separate known/unknown split needed at this point.


			// ── STEP 5: Placed into cluster groups ─────────────────────────────────────
			Console.WriteLine("[Step 5/6] Building cluster groups...");
			var clusterGroups = logic.BuildClusterGroups(allDeviceResults);
			Console.WriteLine($"[Step 5/6] {clusterGroups.Count} cluster groups built\n");


			// ── STEP 6: Print cluster grouping table ──────────────────────────────────
			Console.WriteLine("[Step 6/6] Printing cluster grouping table...");
			logic.PrintClusterTable(clusterGroups);


			// ── STEP 7: Print UNALLOCATED dump table ────────────────────────────────────
			Console.WriteLine($"\n[Step 7] Unallocated devices pending manual assignment on [Date: {DateTime.Now:yyyy-MM-dd}]:\n");

			if (unallocatedDevices.Count == 0)
			{
				Console.WriteLine("[Step 7] No unallocated devices found.\n");
			}
			else
			{
				Console.WriteLine($"{"DumpedAt",-10} | {"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
				Console.WriteLine(new string('-', 130));

				foreach (var u in unallocatedDevices)
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
				Console.WriteLine($"\n[Step 7] Total unallocated: {unallocatedDevices.Count} devices → saved to {UNALLOCATED_DUMP}\n");
			}



			// ── OUTPUT RESULT: Manual Correction ─────────────────────────────────────
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

									string deviceTypeForDisplay = correctType ?? matchedType?.data_type ?? "UNKNOWN";
									PrintSectionWithSuggestions(clusterGroups, suggestions, deviceId, deviceTypeForDisplay);

									// ◄── MODIFIED: validated cluster input, no more silent blank/spacebar acceptance
									correctCluster = PromptClusterChoice($"\n  Enter cluster number [1-{suggestions.Count}] or type manually: ", suggestions);
									if (correctCluster == null)
									{
										Console.WriteLine("[OUTPUT RESULT] Correction cancelled by user.\n");
										goto NextCorrection;
									}
								}
								else
								{
									correctCluster = PromptRequiredText("Correct equipment cluster : ", "Equipment cluster");
									if (correctCluster == null)
									{
										Console.WriteLine("[OUTPUT RESULT] Correction cancelled by user.\n");
										goto NextCorrection;
									}
									correctCluster = correctCluster.ToUpper();
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

							var correctedEntry = new UnallocatedDumpEntry
							{
								Customer = request!.customer_code,
								ProjectCode = request!.project_code,
								DeviceId = deviceId,
								DeviceType = resolvedType,
								PredictedSection = resolvedSection,
								PredictedCluster = resolvedCluster,
								Status = "assigned"
							};

							var placed = logic.AssignByNumericSimilarity(correctedEntry, allDeviceResults);
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
			Console.WriteLine($"\n{section} {g.Cluster} (total Device ID = {g.Devices.Count})");

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
			Console.WriteLine($"\n{section} {s.Cluster} (total Device ID = 0) (New cluster is generated)");
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