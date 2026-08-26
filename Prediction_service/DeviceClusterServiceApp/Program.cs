// ============================================================
// DeviceClusterServiceApp
// ============================================================
// Same inputs and same expected outputs as DeviceClusterConsoleApp, but the
// predictions come from the FastAPI service over HTTP instead of launching
// python.exe as a subprocess.
//
// Purpose: prove that separating the DLLs from Python changes nothing about
// the result. Everything below the prediction step — quota allocation,
// floating split, cluster grouping, and both SQL writes — runs on the exact
// same Logic.dll code the original app uses.
//
// Run the service first:
//     python -m uvicorn service:app --host 127.0.0.1 --port 8000
//
// Then:
//     DeviceClusterServiceApp.exe A9998
// ============================================================

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Win32;
using Logic;
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
        Timeout = TimeSpan.FromMinutes(10)   // large batches take a while
    };

    private static async Task<int> Main(string[] args)
    {
        string projectCode = args.Length > 0 ? args[0].Trim() : "A9998";

        Console.WriteLine("============================================================");
        Console.WriteLine(" DeviceCluster — via HTTP service (no python.exe subprocess)");
        Console.WriteLine("============================================================");
        Console.WriteLine($" Service    : {SERVICE_URL}");
        Console.WriteLine($" Project    : {projectCode}");
        Console.WriteLine();

        try
        {
            // ── STEP 0: is the service actually up? ───────────────────────
            if (!await ServiceIsReachableAsync())
                return 1;

            string sqlConn = GetConnectionString();
            var sqlReader = new PythonSQL(sqlConn);
            var logic = new LogicAssignment(sqlConn, SQL_REVIEW_QUEUE_TABLE, SQL_ASSIGNMENT_TABLE);

            var total = Stopwatch.StartNew();

            // ── STEP 1: read the project's devices from SQL (Model.dll) ───
            var (requestJson, _) = await sqlReader.LoadProjectDataAsync(
                SQL_SOURCE_TABLE, projectCode, SQL_OUTPUT_DIR);

            var request = JsonSerializer.Deserialize<DevicePredictRequest>(requestJson, JsonOpts);
            if (request?.data_ids is null || request.data_ids.Count == 0)
            {
                Console.WriteLine($"[!] No devices found in '{SQL_SOURCE_TABLE}' for '{projectCode}'.");
                return 1;
            }

            // Same cleaning DevicePipeline does — strip BOM, drop blanks.
            request.data_ids = request.data_ids
                .Select(id => id.Replace("﻿", "").Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            Console.WriteLine($"[Step 1] SQL      : {request.data_ids.Count} devices  " +
                              $"(customer {request.customer_code})");

            // ── STEP 2: device type — HTTP instead of subprocess ──────────
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

            Console.WriteLine($"[Step 2] Type    : {typeResults.Count} predicted   " +
                              $"({sw.Elapsed.TotalSeconds:F2}s via HTTP)");

            // ── STEP 3: section + cluster — HTTP instead of subprocess ────
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

            Console.WriteLine($"[Step 3] Cluster : {pipelineResults.Count} predicted   " +
                              $"({sw.Elapsed.TotalSeconds:F2}s via HTTP)");
            Console.WriteLine();

            // ══════════════════════════════════════════════════════════════
            // Everything from here down is UNCHANGED Logic.dll / Model.dll —
            // identical to what DevicePipeline.RunAsync does internally.
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

            var allocation = ClusterQuotaAllocator.Allocate(predictions, quotas);

            // Floating pool → split by cause → SQL review queue
            var unknownPrediction = new List<DeviceResult>();
            var unallocated = new List<DeviceResult>();

            if (allocation.Floating.Count > 0)
            {
                var floating = allocation.Floating.Select(f => new DeviceResult
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

                (unknownPrediction, unallocated) = logic.SplitFloatingPool(floating);
                await logic.DumpFloating(unknownPrediction);
                await logic.DumpUnallocated(unallocated);
            }

            // Assigned → cluster groups → SQL assignment table
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

            var clusterGroups = logic.BuildClusterGroups(assigned);
            await logic.DumpAssigned(assigned, allocation.Assigned);

            total.Stop();

            // ── RESULT ────────────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine(" RESULT");
            Console.WriteLine("============================================================");
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
            Console.WriteLine("      python -m uvicorn service:app --host 127.0.0.1 --port 8000");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ── Compare against the known-good numbers from the subprocess app ────
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
            Console.WriteLine($"[Step 0] Service : reachable, models loaded = {loaded}");
            if (!loaded)
                Console.WriteLine("         (models not loaded — predictions will fail)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Service not reachable at {SERVICE_URL} — {ex.Message}");
            Console.WriteLine("    Start it with:");
            Console.WriteLine("      python -m uvicorn service:app --host 127.0.0.1 --port 8000");
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // Same connection string the original console app uses.
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
