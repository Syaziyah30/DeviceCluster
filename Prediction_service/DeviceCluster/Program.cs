using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

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
	private static readonly string SQL_REVIEW_QUEUE_TABLE = Environment.GetEnvironmentVariable("SQL_REVIEW_QUEUE_TABLE") ?? "dbo.DeviceReviewQueue";
	private static readonly string SQL_ASSIGNMENT_TABLE = Environment.GetEnvironmentVariable("SQL_ASSIGNMENT_TABLE") ?? "dbo.OutputDeviceAssignment";
	private static readonly string SCRIPT_TYPE = Path.Combine(_projectDir, "predict_equipment.py");
	private static readonly string SCRIPT_PIPELINE = Path.Combine(_projectDir, "predict_sectioncluster.py");
	private static readonly string SQL_OUTPUT_DIR = Path.Combine(_serviceDir, "data");
	private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

	private const string REGISTRY_PATH = @"Software\XenxibleIdentifier";

	private static string GetConnectionString()
	{
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(REGISTRY_PATH);

		if (key?.GetValue("connectionstring") is not string connectionString)
			throw new InvalidOperationException(
				$"Registry key 'connectionstring' not found under " +
				$"HKEY_CURRENT_USER\\{REGISTRY_PATH}.");

		if (string.IsNullOrWhiteSpace(connectionString))
			throw new InvalidOperationException("'connectionstring' in registry is empty.");

		return connectionString;
	}




	// ReadLineOrFail : Console.ReadLine() returns null at end-of-stream — no console
	// attached, stdin redirected from an empty source, or the pipe closed. Every prompt
	// below loops until it gets valid input, so treating null as "blank, ask again"
	// spins forever at full CPU. That never happens interactively, but it is exactly
	// what a scheduled task or a service wrapper does. Fail with a usable message instead.
	private static string ReadLineOrFail(string fieldLabel)
	{
		string? line = Console.ReadLine();

		if (line is null)
			throw new InvalidOperationException(
				$"No console input is available while prompting for {fieldLabel}. " +
				"Pass the value as a command-line argument, or run with --unattended " +
				"and a ProjectCode.");

		return line;
	}

	// PromptYesNo : keeps asking until the user enters a valid y/n
	private static string PromptYesNo(string prompt)
	{
		string? input;
		while (true)
		{
			Console.Write(prompt);
			input = ReadLineOrFail("a yes/no answer").Trim().ToLower();

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
			input = ReadLineOrFail(fieldLabel).Trim();

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
			input = ReadLineOrFail("a cluster").Trim();

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
			input = ReadLineOrFail("a section").Trim().ToUpper();

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

		PythonClient? client = null;

		try
		{
			string SQL_CONN = GetConnectionString();
			var sqlReader = new PythonSQL(SQL_CONN);

			// --unattended runs with no console pauses and no manual-correction prompt — for a
			// scheduler/task runner. Requires the ProjectCode to also be passed as an arg, since
			// there's no one to answer an interactive prompt.
			bool unattended = args.Any(a => a.Equals("--unattended", StringComparison.OrdinalIgnoreCase));

			// ProjectCode selects which project's rows to pull from the shared SQL table —
			// pass it as a CLI arg for automation, or leave blank to be prompted interactively.
			string? projectCodeArg = args.FirstOrDefault(a => !a.StartsWith("--"))?.Trim().ToUpper();
			string projectCode;

			if (!string.IsNullOrWhiteSpace(projectCodeArg))
			{
				projectCode = projectCodeArg;
			}
			else if (unattended)
			{
				throw new InvalidOperationException("--unattended requires a ProjectCode to also be passed as an argument.");
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
			var logic = new LogicAssignment(SQL_CONN, SQL_REVIEW_QUEUE_TABLE, SQL_ASSIGNMENT_TABLE);

			Console.WriteLine($"[Step 1/6] Loading reference data for project '{projectCode}' from SQL Server...");

			void PauseUnlessUnattended(string message)
			{
				if (unattended) return;
				Console.Write(message);
				Console.ReadLine();
			}

			var callbacks = new DevicePipelineCallbacks
			{
				OnProjectLoaded = (req, outputPath) =>
				{
					Console.WriteLine($"[Step 1/6] Reference data saved \u2192 {outputPath}\n");
					Console.WriteLine($"[Step 1/6] Project detected : {req.project_code} ({req.customer_code})");
					Console.WriteLine($"[Step 1/6] Loaded {req.data_ids.Count} device IDs\n");
				},
				OnDeviceTypesPredicted = (results, elapsed) =>
				{
					Console.WriteLine("[Step 2/6] Predicting device types...");
					PrintDeviceTypeTable(results);
					Console.WriteLine($"Time taken: {elapsed:F1} secs");
					PauseUnlessUnattended("\nPress Enter to predict Section + Cluster...");
				},
				OnSectionsPredicted = (results, lookup, elapsed) =>
				{
					Console.WriteLine("[Step 3/6] Predicting sections...");
					PrintSectionTable(results, lookup);
					Console.WriteLine($"Time taken: {elapsed:F1} secs");
					PauseUnlessUnattended("\nPress Enter to predict Cluster...");
				},
				OnClustersPredicted = (results, lookup, elapsed) =>
				{
					Console.WriteLine("[Step 3/6] Predicting clusters...");
					PrintClusterTable(results, lookup);
					Console.WriteLine($"Time taken: {elapsed:F1} secs");
					PauseUnlessUnattended("\nPress Enter to run quota allocation...");
				},
				OnQuotaAllocated = (quotas, allocationResult) =>
				{
					Console.WriteLine("\n[Step 3.5/6] Running quota-constrained cluster allocation...");
					ClusterQuotaAllocator.PrintFulfilledReport(quotas, allocationResult.VacancyReport);
					ClusterQuotaAllocator.PrintVacancyReport(allocationResult.VacancyReport);
					Console.WriteLine($"[Step 3.5/6] {allocationResult.Assigned.Count} assigned, {allocationResult.Floating.Count} floating\n");
				},
				OnFloatingSplit = (allFloating, unknownPrediction, unallocated) =>
				{
					if (allFloating.Count > 0)
					{
					Console.WriteLine($"[Step 3.5/6] {allFloating.Count} floating device ID(s) — not claimed by any quota bucket:\n");
						Console.WriteLine($"{"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
						Console.WriteLine(new string('-', 130));

						foreach (var d in allFloating)
						{
							Console.WriteLine(
								$"{d.Customer,-10} | {d.ProjectCode,-12} | {d.DeviceId,-25} | " +
								$"{d.DeviceType,-25} | {d.Section,-15} | {d.Cluster,-15}");
						}

						Console.WriteLine();
					}
					else
					{
						Console.WriteLine("[Step 3.5/6] No floating devices — all predictions claimed by quota allocation.\n");
					}

					PauseUnlessUnattended("\nPress Enter to run Logic...");
				},
				OnClusterGroupsBuilt = (assignedDevices, clusterGroups) =>
				{
					Console.WriteLine("\n[Step 4/6] Passing results into Logic.dll...");
					Console.WriteLine($"[Step 4/6] {assignedDevices.Count} devices passed into Logic.dll\n");

					// Every device here already matched a real quota bucket (Section+Cluster+DeviceType),
					// so it's guaranteed known — no separate known/unknown split needed at this point.

					Console.WriteLine("[Step 5/6] Building cluster groups...");
					Console.WriteLine($"[Step 5/6] {clusterGroups.Count} cluster groups built\n");

					Console.WriteLine("[Step 6/6] Printing cluster grouping table...");
					logic.PrintClusterTable(clusterGroups);
				}
			};

			var result = await DevicePipeline.RunAsync(
				sqlReader, client, logic,
				SQL_SOURCE_TABLE, SQL_QUOTA_TABLE, SCRIPT_TYPE, SCRIPT_PIPELINE, SQL_OUTPUT_DIR,
				projectCode, callbacks);


			// ── STEP 7: Print UNALLOCATED dump table ────────────────────────────────────
			Console.WriteLine($"\n[Step 7] Unallocated devices pending manual assignment on [Date: {DateTime.Now:yyyy-MM-dd}]:\n");

			if (result.UnallocatedDevices.Count == 0)
			{
				Console.WriteLine("[Step 7] No unallocated devices found.\n");
			}
			else
			{
				Console.WriteLine($"{"DumpedAt",-10} | {"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
				Console.WriteLine(new string('-', 130));

				foreach (var u in result.UnallocatedDevices)
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
				Console.WriteLine($"\n[Step 7] Total unallocated: {result.UnallocatedDevices.Count} devices → saved to {SQL_REVIEW_QUEUE_TABLE}\n");
			}



			// ── OUTPUT RESULT: Manual Correction ─────────────────────────────────────
			// Skipped entirely when --unattended — there's no one to answer the prompts.
			if (!unattended)
			{
			string userInput = PromptYesNo("\n[OUTPUT RESULT] Correct any prediction? (y/n): ");

			while (userInput == "y")
			{
				Console.Write("Device ID to correct: ");
				string? deviceId = Console.ReadLine()?.Trim().ToUpper();

				if (!string.IsNullOrEmpty(deviceId))
				{
					var matchedType = result.TypeResults?.FirstOrDefault(r => r.data_id == deviceId);
					var matchedPipeline = result.PipelineResults?.FirstOrDefault(r => r.DEVICE_ID == deviceId);

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
								var suggestions = await clusterService.GetTopClustersAsync(deviceId, result.Request.customer_code, result.Request.project_code);
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
									PrintSectionWithSuggestions(result.ClusterGroups, suggestions, deviceId, deviceTypeForDisplay);

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
									project_code = result.Request.project_code,
									customer = result.Request.customer_code,
									assignments = new[]
									{
										new { data_id = deviceId, equipment = correctType }
									},
									batch_results = result.TypeResults.Select(r => new
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
							string resolvedType = correctType ?? result.DeviceTypeLookup.GetValueOrDefault(deviceId, "UNKNOWN");
							string resolvedSection = correctSection ?? matchedPipeline?.PREDICTED_SECTION ?? "UNKNOWN";
							string resolvedCluster = correctCluster ?? matchedPipeline?.PREDICTED_CLUSTER ?? "UNKNOWN";

							var correctedEntry = new UnallocatedDumpEntry
							{
								Customer = result.Request.customer_code,
								ProjectCode = result.Request.project_code,
								DeviceId = deviceId,
								DeviceType = resolvedType,
								PredictedSection = resolvedSection,
								PredictedCluster = resolvedCluster,
								Status = "assigned"
							};

							var placed = logic.AssignByNumericSimilarity(correctedEntry, result.AssignedDevices);
							if (placed != null)
							{
								logic.PlaceDevice(placed, result.ClusterGroups);
								await logic.MarkAsAssigned(deviceId, result.Request.project_code, placed.Section, placed.Cluster);
								Console.WriteLine("\n[Logic] Updated cluster grouping after correction:");
								logic.PrintClusterTable(result.ClusterGroups, placed.Section);
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
		}
		catch (Exception ex)
		{
			Console.WriteLine($"\nERROR: {ex.Message}\n{ex.StackTrace}");
		}
		finally
		{
			if (!args.Any(a => a.Equals("--unattended", StringComparison.OrdinalIgnoreCase)))
			{
				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
			}
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