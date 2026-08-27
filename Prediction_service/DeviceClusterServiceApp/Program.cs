// ============================================================
// DeviceClusterServiceApp
// ============================================================
// Same inputs, same reports and same expected output as the original
// DeviceClusterConsoleApp — but the predictions come from the FastAPI
// service over HTTP instead of launching python.exe as a subprocess.
//
// Everything below the prediction step (quota allocation, floating split,
// cluster grouping, both SQL writes) runs on the exact same Logic.dll code
// the original app uses. Only the transport changed.
//
// Start the service first (on this PC or a server):
//     python -m uvicorn service:app --host 0.0.0.0 --port 8000
//
// Point at it and run:
//     $env:ML_SERVICE_URL = "http://128.100.8.213:8000"
//     DeviceClusterServiceApp.exe A9998
//     DeviceClusterServiceApp.exe A9998 --unattended     (no prompts)
// ============================================================

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Win32;
using Logic;
using Logic.LogicAssignUser;
using Logic.Models;
using Model.ModelRequest;
using Model.ModelResult;
using Model.Services;

namespace DeviceClusterServiceApp;

internal static class Program
{
    private const string REGISTRY_PATH = @"SOFTWARE\XenxibleIdentifier";

    private static readonly string SERVICE_URL =
        Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://127.0.0.1:8000";

    private static readonly string SQL_SOURCE_TABLE =
        Environment.GetEnvironmentVariable("SQL_SOURCE_TABLE") ?? "DummyTestingData";
    private static readonly string SQL_QUOTA_TABLE =
        Environment.GetEnvironmentVariable("SQL_QUOTA_TABLE") ?? "dbo.PatternCluster";
    private static readonly string SQL_REVIEW_QUEUE_TABLE =
        Environment.GetEnvironmentVariable("SQL_REVIEW_QUEUE_TABLE") ?? "dbo.DeviceReviewQueue";
    private static readonly string SQL_ASSIGNMENT_TABLE =
        Environment.GetEnvironmentVariable("SQL_ASSIGNMENT_TABLE") ?? "dbo.OutputDeviceAssignment";

    private static readonly string SQL_OUTPUT_DIR =
        Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(SERVICE_URL),
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static bool _unattended;

    private static async Task<int> Main(string[] args)
    {
        _unattended = args.Any(a => a.Equals("--unattended", StringComparison.OrdinalIgnoreCase));
        string? projectCodeArg = args.FirstOrDefault(a => !a.StartsWith("--"))?.Trim().ToUpper();

        Console.WriteLine("============================================================");
        Console.WriteLine(" DeviceCluster — predictions via HTTP service");
        Console.WriteLine("============================================================");
        Console.WriteLine($" Service : {SERVICE_URL}");
        Console.WriteLine($" Mode    : {(_unattended ? "unattended" : "interactive")}");
        Console.WriteLine();

        try
        {
            if (!await ServiceIsReachableAsync()) return 1;

            string sqlConn = GetConnectionString();
            var sqlReader = new PythonSQL(sqlConn);
            var logic = new LogicAssignment(sqlConn, SQL_REVIEW_QUEUE_TABLE, SQL_ASSIGNMENT_TABLE);

            // ProjectCode picks which project's rows to pull from the shared SQL table.
            // Pass it as an argument for automation, or leave it off to choose from a list.
            string projectCode;

            if (!string.IsNullOrWhiteSpace(projectCodeArg))
            {
                projectCode = projectCodeArg;
            }
            else if (_unattended)
            {
                throw new InvalidOperationException(
                    "--unattended requires a ProjectCode to also be passed as an argument.");
            }
            else
            {
                var availableProjects = await sqlReader.ListAvailableProjectsAsync(SQL_SOURCE_TABLE);
                Console.WriteLine("Project Available:");
                foreach (var (code, customer) in availableProjects)
                    Console.WriteLine($"  {code} ({customer})");
                Console.WriteLine();

                projectCode = PromptRequiredText("Enter Project Code to process: ")
                    ?? throw new InvalidOperationException("Project Code is required.");
            }

            Console.WriteLine($"[Step 1/6] Loading reference data for project '{projectCode}' from SQL Server...");

            var total = Stopwatch.StartNew();

            // ── STEP 1/6: read the project's devices from SQL ─────────────
            var (requestJson, outputPath) = await sqlReader.LoadProjectDataAsync(
                SQL_SOURCE_TABLE, projectCode, SQL_OUTPUT_DIR);

            var request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, JsonOpts);
            if (request?.data_ids is null || request.data_ids.Count == 0)
            {
                Console.WriteLine($"[!] No devices found in '{SQL_SOURCE_TABLE}' for '{projectCode}'.");
                return 1;
            }

            request.data_ids = request.data_ids
                .Select(id => id.Replace("﻿", "").Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            Console.WriteLine($"[Step 1/6] Reference data saved → {outputPath}\n");
            Console.WriteLine($"[Step 1/6] Project detected : {request.project_code} ({request.customer_code})");
            Console.WriteLine($"[Step 1/6] Loaded {request.data_ids.Count} device IDs\n");

            // ── STEP 2/6: device type — over HTTP ─────────────────────────
            Console.WriteLine("[Step 2/6] Predicting device types...");
            var sw = Stopwatch.StartNew();
            var typeResults = await PostAsync<List<DeviceTypeResult>>(
                "/predict/device-type",
                new
                {
                    project_code = request.project_code,
                    customer_code = request.customer_code,
                    data_ids = request.data_ids
                }) ?? new();
            sw.Stop();

            var deviceTypeLookup = typeResults
                .GroupBy(r => r.data_id)
                .ToDictionary(g => g.Key, g => g.Last().data_type ?? "N/A");

            PrintDeviceTypeTable(typeResults);
            Console.WriteLine($"Time taken: {sw.Elapsed.TotalSeconds:F2} secs  (HTTP)");
            Pause("\nPress Enter to predict Section + Cluster...");

            // ── STEP 3/6: section + cluster — over HTTP ───────────────────
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
            var pipelineResults = await PostAsync<List<PipelineResult>>(
                "/predict/section-cluster", pipelineRequest) ?? new();
            sw.Stop();
            double predictSecs = sw.Elapsed.TotalSeconds;

            Console.WriteLine("[Step 3/6] Predicting sections...");
            PrintSectionTable(pipelineResults, deviceTypeLookup);
            Console.WriteLine($"Time taken: {predictSecs:F2} secs  (HTTP)");
            Pause("\nPress Enter to predict Cluster...");

            Console.WriteLine("[Step 3/6] Predicting clusters...");
            PrintClusterTable(pipelineResults, deviceTypeLookup);
            Console.WriteLine($"Time taken: {predictSecs:F2} secs  (HTTP)");
            Pause("\nPress Enter to run quota allocation...");

            // ══════════════════════════════════════════════════════════════
            // From here down: unchanged Logic.dll / Model.dll
            // ══════════════════════════════════════════════════════════════

            var predictions = pipelineResults.Select(r => new DevicePrediction
            {
                Section = r.PREDICTED_SECTION ?? "UNKNOWN",
                Cluster = r.PREDICTED_CLUSTER ?? "UNKNOWN",
                DeviceId = r.DEVICE_ID,
                DeviceType = deviceTypeLookup.TryGetValue(r.DEVICE_ID, out var dt) ? dt : "UNKNOWN",
                Score = r.CLUSTER_CONFIDENCE ?? 0,
                TopClusters = (r.TOP_CLUSTERS ?? new List<ClusterCandidate>())
                    .Select(c => new ClusterPrediction { Cluster = c.Cluster, Probability = c.Probability })
                    .ToList()
            }).ToList();

            var quotas = await QuotaCatalog.LoadQuotasFromDbAsync(
                sqlReader.ConnectionString, SQL_QUOTA_TABLE, request.customer_code);

            // ── STEP 3.5/6: quota allocation ──────────────────────────────
            Console.WriteLine("\n[Step 3.5/6] Running quota-constrained cluster allocation...");
            var allocation = ClusterQuotaAllocator.Allocate(predictions, quotas);
            ClusterQuotaAllocator.PrintFulfilledReport(quotas, allocation.VacancyReport);
            ClusterQuotaAllocator.PrintVacancyReport(allocation.VacancyReport);
            Console.WriteLine($"[Step 3.5/6] {allocation.Assigned.Count} assigned, {allocation.Floating.Count} floating\n");

            // ── Floating pool → split by cause → SQL review queue ─────────
            var unknownPrediction = new List<DeviceResult>();
            var unallocated = new List<DeviceResult>();
            var floating = new List<DeviceResult>();

            if (allocation.Floating.Count > 0)
            {
                floating = allocation.Floating.Select(f => new DeviceResult
                {
                    Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == f.DeviceId)?.CUSTOMER
                               ?? request.customer_code,
                    ProjectCode = request.project_code,
                    DeviceId = f.DeviceId,
                    DeviceType = f.DeviceType,
                    Section = f.Section,
                    Cluster = f.Cluster,
                    Confidence = f.Score
                }).ToList();

                Console.WriteLine($"[Step 3.5/6] {floating.Count} floating device ID(s) — not claimed by any quota bucket:\n");
                Console.WriteLine($"{"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
                Console.WriteLine(new string('-', 130));
                foreach (var d in floating)
                    Console.WriteLine($"{d.Customer,-10} | {d.ProjectCode,-12} | {d.DeviceId,-25} | " +
                                      $"{d.DeviceType,-25} | {d.Section,-15} | {d.Cluster,-15}");
                Console.WriteLine();

                (unknownPrediction, unallocated) = logic.SplitFloatingPool(floating);
                await logic.DumpFloating(unknownPrediction);
                await logic.DumpUnallocated(unallocated);
            }
            else
            {
                Console.WriteLine("[Step 3.5/6] No floating devices — all predictions claimed by quota allocation.\n");
            }

            Pause("\nPress Enter to run Logic...");

            // ── STEP 4-6/6: cluster groups ────────────────────────────────
            var assigned = allocation.Assigned.Select(a => new DeviceResult
            {
                Customer = pipelineResults.FirstOrDefault(r => r.DEVICE_ID == a.DeviceId)?.CUSTOMER
                           ?? request.customer_code,
                ProjectCode = request.project_code,
                DeviceId = a.DeviceId,
                DeviceType = a.DeviceType,
                Section = a.Section,
                Cluster = a.Cluster,
                Confidence = a.Score
            }).ToList();

            Console.WriteLine("\n[Step 4/6] Passing results into Logic.dll...");
            Console.WriteLine($"[Step 4/6] {assigned.Count} devices passed into Logic.dll\n");

            Console.WriteLine("[Step 5/6] Building cluster groups...");
            var clusterGroups = logic.BuildClusterGroups(assigned);
            Console.WriteLine($"[Step 5/6] {clusterGroups.Count} cluster groups built\n");

            await logic.DumpAssigned(assigned, allocation.Assigned);

            Console.WriteLine("[Step 6/6] Printing cluster grouping table...");
            logic.PrintClusterTable(clusterGroups);

            // ── STEP 7: unallocated dump table ────────────────────────────
            Console.WriteLine($"\n[Step 7] Unallocated devices pending manual assignment on [Date: {DateTime.Now:yyyy-MM-dd}]:\n");
            if (unallocated.Count == 0)
            {
                Console.WriteLine("[Step 7] No unallocated devices found.\n");
            }
            else
            {
                Console.WriteLine($"{"DumpedAt",-10} | {"Customer",-10} | {"ProjectCode",-12} | {"DeviceId",-25} | {"DeviceType",-25} | {"PredictedSection",-15} | {"PredictedCluster",-15}");
                Console.WriteLine(new string('-', 130));
                foreach (var u in unallocated)
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss,-10} | {u.Customer,-10} | {u.ProjectCode,-12} | " +
                                      $"{u.DeviceId,-25} | {u.DeviceType,-25} | {u.Section,-15} | {u.Cluster,-15}");
                Console.WriteLine($"\n[Step 7] Total unallocated: {unallocated.Count} devices → saved to {SQL_REVIEW_QUEUE_TABLE}\n");
            }

            total.Stop();

            // ── SUMMARY + verification ────────────────────────────────────
            Console.WriteLine("============================================================");
            Console.WriteLine(" RESULT");
            Console.WriteLine("============================================================");
            Console.WriteLine($"  Predictions from : {SERVICE_URL}");
            Console.WriteLine($"  Total devices    : {predictions.Count}");
            Console.WriteLine($"  Assigned         : {assigned.Count}");
            Console.WriteLine($"  Unknown          : {unknownPrediction.Count}");
            Console.WriteLine($"  Unallocated      : {unallocated.Count}");
            Console.WriteLine($"  Cluster groups   : {clusterGroups.Count}");
            Console.WriteLine($"  Elapsed          : {total.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine();

            Compare(projectCode, assigned.Count, unknownPrediction.Count, unallocated.Count);
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[!] Cannot reach the prediction service at {SERVICE_URL}");
            Console.WriteLine($"    {ex.Message}");
            Console.WriteLine("    Start it with:");
            Console.WriteLine("      python -m uvicorn service:app --host 0.0.0.0 --port 8000");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ── PRINT HELPERS — same formats as the original console app ─────────

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

    private static void Pause(string message)
    {
        if (_unattended) return;
        Console.Write(message);
        Console.ReadLine();
    }

    // Keeps asking until non-blank input, or returns null if the user types "E" to exit.
    private static string? PromptRequiredText(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine()?.Trim();

            if (!string.IsNullOrWhiteSpace(input))
            {
                if (input.Equals("E", StringComparison.OrdinalIgnoreCase)) return null;
                return input.ToUpper();
            }

            Console.WriteLine("[OUTPUT RESULT] Invalid input. Project Code cannot be blank. (Type E to exit)\n");
        }
    }

    private static void Compare(string projectCode, int assigned, int unknown, int unallocated)
    {
        if (!projectCode.Equals("A9998", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  (no reference figures recorded for this project — nothing to compare)");
            return;
        }

        const int expAssigned = 90, expUnknown = 28, expUnallocated = 240;
        bool ok = assigned == expAssigned && unknown == expUnknown && unallocated == expUnallocated;

        Console.WriteLine("  Comparison with the subprocess run of the same project:");
        Row("Assigned", assigned, expAssigned);
        Row("Unknown", unknown, expUnknown);
        Row("Unallocated", unallocated, expUnallocated);
        Console.WriteLine();

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(ok
            ? "  MATCH — the HTTP service produces the same result as the subprocess."
            : "  MISMATCH — investigate before relying on the service.");
        Console.ForegroundColor = prev;

        static void Row(string label, int got, int expected) =>
            Console.WriteLine($"    {label,-14} got {got,4}   expected {expected,4}   " +
                              (got == expected ? "OK" : "DIFFERENT"));
    }

    private static async Task<bool> ServiceIsReachableAsync()
    {
        try
        {
            var resp = await Http.GetAsync("/ml-device-identifier");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[!] Service responded {(int)resp.StatusCode} at /ml-device-identifier.");
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            bool loaded = doc.RootElement.TryGetProperty("pipeline_loaded", out var p) && p.GetBoolean();
            Console.WriteLine($"[Step 0/6] Service : reachable, models loaded = {loaded}\n");
            if (!loaded)
                Console.WriteLine("           (models not loaded — predictions will fail)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Service not reachable at {SERVICE_URL} — {ex.Message}");
            Console.WriteLine("    Start it with:");
            Console.WriteLine("      python -m uvicorn service:app --host 0.0.0.0 --port 8000");
            return false;
        }
    }

    private static async Task<T?> PostAsync<T>(string endpoint, object payload)
    {
        var resp = await Http.PostAsJsonAsync(endpoint, payload);
        string body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{endpoint} returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        return JsonSerializer.Deserialize<T>(body, JsonOpts);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string GetConnectionString()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SQL_CONN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_PATH);
        string? cs = key?.GetValue("connectionstring") as string;

        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"'connectionstring' not found under HKEY_CURRENT_USER\\{REGISTRY_PATH}. " +
                "Set the SQL_CONN environment variable instead.");

        return cs;
    }
}
