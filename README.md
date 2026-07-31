# DeviceCluster — Reference Guide

| | |
|---|---|
| **Full pipeline** | Device Type → Section → Cluster → Quota Allocation → Logic Placement (cascading) |
| **Runtime** | C# reads SQL DB → saves as JSON → Python ML scripts predict → `ClusterQuotaAllocator` fits quotas → `Logic.dll` places/groups → C# outputs to UI|

> Last synced 31 July 2026 (previous version: 3 July 2026). Changes are marked with 🆕 where useful.

---


# 📍 Project Status

| Component | Status |
|------------|--------|
| Device Type Prediction              | ✅ Completed |
| Device Section Prediction           | ✅ Completed |
| Device Cluster Prediction           | ✅ Completed |
| C# Service Integration              | ✅ Completed |
| SQL Integration (`PythonSQL.cs`)    | ✅ Completed |
| Incremental Learning                | ✅ Completed |
| 🆕DLL Development (`AppRegistryEditor.dll`, `Logic.dll`, `Model.dll`) | ✅ Completed |
| 🆕Model-based Top-3 Cluster Suggestion `predict_sectioncluster.py` from XGBoost | ✅ Completed |
| Update Model                        | ✅ Completed (Once workflow is complete,need retraining) |
| 🆕`Logic.dll` Development             | ❗❗❗ **STUCK HERE** |
| 🆕Raw Prediction Output ins CSV       | ❌ Not Started (this subpoint is for the analysis purpose. at this moment, using dummy data for workflow cluster logic instead of extraction from dataset) |
| 🆕DLL Testing                         | ❌ Pending |


---
Script active : DeviceClusterConsoleApp [`Program.cs`]
---
# 📍 `Logic.dll` Status

| Stage | Status |
|-------|--------|
| Stage 1 – Initial Cluster Assignment | ✅ Completed |
| Stage 2 – Exceeded & Vacancy Evaluation | ✅ Completed |
| Stage 3 – Reassignment Pool & Allocation | ✅ Completed |
| Model-based Top-3 Cluster Prediction | ✅ Completed |
| UNKNOWN Device Handling | ✅ Completed |
| Logic.dll Testing | ❌ Pending |
| Project References Migration | ❌ Pending |

---

## 📍 Recent Fixes

- ✅ Fixed case-sensitivity issue (`OILTEK` vs `Oiltek`)
- ✅ Fixed device ID formatting issue (e.g. `V001.21` preserved correctly)

---

# 🍀 Architecture Overview

```
             Data Source DB SQL (XenCreator → DummyInput table)
                    │
                    │  PythonSQL.cs (C#): dynamic SELECT DISTINCT
                    │  project detection, SQL → JSON
                    ▼
                Input JSON
      (project_code, customer_code, data_ids[])
                    │
                    │
                    │
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 1 — Device Type [predict_equipment.py]       │
│ Hybrid: exact match + SGD + TF-IDF cosine         │
│         + dict 22 equipment classes               │
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  DeviceTypeResult[]
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 2 — Section [predict_sectioncluster.py]      │
│ XGBoost (27+ engineered features)                 │
│       + OOD KNN penalty applied                   │
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  PipelineResult[] (with PREDICTED_SECTION)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 3 — Cluster [predict_sectioncluster.py]      │
│ XGBoost chained on Predicted Section              │
│ Confidence penalised by Section confidence        │
│ 🆕 optional side-effect: export_raw_csv_path     │
│    writes full predict_proba matrix (see §8)      │
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  PipelineResult[] (with PREDICTED_CLUSTER)
                    ▼
┌─────────────────────────────────────────────────────┐
│ 🆕 Step 3.5 — Quota Allocation [Logic.dll, C#]      │
│ Split UNKNOWN (type) devices out BEFORE allocation  │
│ ClusterQuotaAllocator two-pass:                     │
│   Pass 1 — ranked assignment against quota          │
│   Pass 2 — floating-pool backfill vs InitialDeficits│
│ MinCascadeConfidence floor (60%) blocks bad forces  │
└───────────────────┬─────────────────────────────────┘
                    │
                    │  AllocationResult (Assigned / Floating /
                    │  InitialDeficits / VacancyReport)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 4 — Logic Placement [Logic.dll, C#]          │
│ Split known/unknown devices                       │
│ Cascading placement                               │
│ Cluster grouping → ClusterGroup / ScoredDevice    │
│ Unknown devices → JSON dump + SuggestTopClusters  │
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  DeviceResult[] / UnknownDumpEntry[]
                    ▼
        Program.cs — print tables, prompt manual
        correction, output DLL result to Faiz
```

---

# 1️⃣ Device Type (`predict_equipment.py`)

## 📍 Model Approach: SGD Classifier

Hybrid classifier combining four sources, resolved in strict priority order:

```
1. All-letters                         → UNKNOWN
2. Exact match from training dataset   → label from reference_df, confidence = 1.0
3. SGD confidence >= 0.60              → SGD label
4. Composite similarity >= 0.60        → Nearest-neighbour cosine label
5. Dictionary match (initial_map)      → dict label, confidence = 0.75
6. None of the above                   → UNKNOWN [confidence = max(composite, sgd)]
```

## 📍 Configuration Thresholds

| Constant | Value | Purpose |
|---|---|---|
| `SGD_STRONG_THRESHOLD`  | `0.60` | Minimum SGD confidence to accept SGD label |
| `COSINE_THRESHOLD`      | `0.60` | Minimum composite score to accept NN/cosine label |
| `INITIAL_DICT_CONF`     | `0.75` | Fixed confidence assigned for dictionary-only match |
| `ALPHA_PREFIX_WEIGHT`   | `0.65` (from `composite_config`) | Prefix weight in composite formula |
| `top_k_default` | `10` (from `composite_config`) | Nearest neighbours retrieved per query |

**Composite similarity formula:**
```
composite_score = (0.65 × prefix_score) + (0.35 × cosine_similarity)
```

## 📍 Batch & Buffer Settings

| Constant | Value | Purpose |
|---|---|---|
| `MAX_BATCH_SIZE` | `5000` (env: `MAX_BATCH_SIZE`) | Max `data_ids` per call |
| `BATCH_ADD_SIZE` | `50` | Incremental learning flush buffer |
| `ref_epoch_rebuild` | `50` | NN re-fit trigger (rows added) |

> Flush happens when `len(PENDING_NEW_ROWS) >= flush_batch_size`.

## 📍 Dictionary / Prefix Matching Rules
Dictionary Name = `initial_map`

➡️ A prefix is valid only IF the remaining characters are:
- Empty (exact match) - (e.g. `CR` matches key `CR`)
- Digits only         -  (e.g. `CR1234` matches key `CR`)

➡️ Rejected if remainder contains any letters (`CR123ABC` does **not** match `CR`). 

➡️ Special case: inputs starting with `SP` also probe the stripped version (`probe[2:]`).

### Batch prefix counting — `matches_prefix_strict()`

- Counts devices sharing the same prefix across a batch of `data_ids`.
- Applies the same strict matching rule: the remaining characters must be empty or digits only.
- Prevents false matches caused by alphabetic suffixes.
- Used for dictionary confidence scoring and candidate grouping.
- **TODO:** confirm exact call sites (`predict_equipment.py` function names/line refs) once finalized, so this section can point directly to them.

## 📍 Output Columns (Device Type)

| Column | Description |
|---|---|
| `customer` | OILTEK/LIPICO                 |
| `data_id` | Cleaned Device ID                        |
| `manual_check` | Empty placeholder for UI review flag |
| `data_type` | Predicted equipment type                |
| `confidence` | Final confidence (0.0–1.0)             |
| `sgd_conf` | Raw SGD confidence (may differ from final) |
| `reason` | Internal decision reason |

## 📍 `reason` Values

| `reason` | Trigger |
|---|---|
| `all_letters`             | Input is purely alphabetic — hard blocked |
| `exact_match`             | Found exactly similar in reference set |
| `sgd_strong`              | SGD confidence >= 0.60 |
| `cosine_prefix_accepted` | Composite similarity >= 0.60 |
| `initial_dict_only` | Dictionary match, no other confident source |
| `no_confident_source` | Fallback — returns UNKNOWN |

## 📍 `source` Values

| `source` | Meaning |
|---|---|
| `exact_match` | Verbatim match in reference |
| `sgd` | SGD was confident |
| `composite similarity` | NN + prefix composite was confident |
| `initial_dict` | Dictionary/prefix only |
| `unknown` | No confident source |

## 📍 CLI Actions (`predict_equipment.py`)

| `action` | Description | Changes Saved? |
|---|---|---|
| `predict` (default) | Predict device types for `data_ids` | No |
| `user_manual_assign` | Manually assign labels + incremental learn | ✅ Yes (full persist) |
| `import_equipment` | Bulk import authoritative list + incremental learn | ❌ No |

**JSON payloads:**

```jsonc
// predict
{ "action": "predict", "project_code": "A1825", "customer_code": "Lipico",
  "data_ids": ["CR1234", "PU001"] }

// user_manual_assign
{ "action": "user_manual_assign", "project_code": "A1825", "customer": "Lipico",
  "assignments": [{ "data_id": "CR1234", "equipment": "Control Room" }] }

// import_equipment
{ "action": "import_equipment", "project_code": "A1825", "customer": "Lipico",
  "equipment_list": [{ "data_id": "CR1234", "equipment": "Control Room" }] }
```

## 📍 Incremental Learning Notes

- Uses `partial_fit` for live model updates.
- Changes are saved every 50 records.
- `user_manual_assign` saves immediately.
- `import_equipment` updates the model in memory only.
- `initial_map.pkl` should not be edited directly, as it may cause SGD class mismatch errors.

## 📍 Features

| Variable | Role |
|---|---|
| `sgd_model` | Main classifier |
| `tfidf_sgd` / `tfidf_similarity` | Text vectorisers |
| `nn` | Nearest neighbour (k=10, cosine) |
| `initial_map` | Prefix → equipment type |
| `reference_df` / `ref_id_set` | Ground-truth reference |

---

# 2️⃣ & 3️⃣ Section & Cluster (`predict_sectioncluster.py`)

## 📍 Model Approach: Chained XGBoost classification

```
Features → [XGBoost] → Predicted Section → Features + Section → [XGBoost] → Cluster
```

- **Stage 1:** Predict Section
- **Stage 2:** Predict Cluster using original features **+** Predicted Section

## 📍 Thresholds & Penalties

| Parameter | Value | Purpose |
|---|---|---|
| `unknown_threshold` | `0.60` | Section confidence gate for chaining |
| OOD penalty formula | `adjusted = raw / (1 + max(0, dist - threshold) / threshold)` | KNN distance penalty on raw confidence |

**Confidence chaining rule:** if Section confidence < `0.60`, Cluster confidence is multiplied by Section confidence (joint probability).

```
cluster_conf_final = cluster_raw_conf × section_conf   (when section_conf < 0.60)
```

## 📍 Input Validation

| Condition | Result |
|-----------|--------|
| Unknown CUSTOMER or missing DEVICE_ID | Prediction rejected |
| Unexpected numeric format | Confidence reduced with `FORMAT_WARNING` |

## 📍 Output Columns (Section & Cluster)

| Column | Description |
|---|---|
| `PREDICTED_SECTION` | Predicted section label or `UNKNOWN` |
| `SECTION_CONFIDENCE` | Adjusted confidence (0–100%) |
| `PREDICTED_CLUSTER` | Predicted cluster label or `UNKNOWN` |
| `CLUSTER_CONFIDENCE` | Adjusted confidence, penalised if section is weak |
| `REJECTION_REASON` | Set if device is hard-blocked |
| `FORMAT_WARNING` | Set if numeric field width is outside training distribution |

## 📍 Model Artefacts

| File | Contents |
|---|---|
| `model_section.pkl` | Trained XGBoost section model |
| `model_cluster.pkl` | Trained XGBoost cluster model |
| `pipeline_config.pkl` | All label encoders, feature lists, OOD scaler/KNN, known customers, numeric width stats |

## 🆕 📍 Raw Cluster Probability Export (`cluster_prediction_raw.csv`)

Added so the full `predict_proba` distribution (one column per cluster, per device) can be reviewed offline, rather than only the single winning label.

- **Design decision:** implemented as an **optional side effect of the existing `predict` action**, not a new subprocess call — `predict()` already computes `clu_proba_raw` internally via `model_cluster.predict_proba(X_clu)`, so `_write_raw_cluster_csv()` reuses that matrix instead of loading the pickled models and re-running `predict_proba` a second time.
- Triggered by an optional `export_raw_csv_path` field on the same JSON payload already sent to Step 3.
- **Current status — in progress:** `export_csv_path` is arriving as `None` inside Python at runtime even though the console shows real predictions (ruling out `eligible_mask.any() == False`). Two live hypotheses, being verified in order:
  1. **Stale build** — Visual Studio may be running a compiled version predating the `export_raw_csv_path` field on `PipelinePredictRequest.cs`; a `Build` doesn't always force a full recompile of every project in the solution. Fix: `Clean Solution` → `Rebuild Solution`.
  2. The JSON payload genuinely isn't carrying the field (serialization issue).
- **Diagnostic added:** a temporary sentinel file write (`_debug_received.txt`, unconditional, independent of `eligible_mask`) right where `export_csv_path` is read in `run_cli()`, to confirm exactly what Python received without digging through stderr routing.
- File is written to `cluster_prediction_raw.csv` (see filepath glossary in §11).

---

# 🆕 3.5️⃣ Quota Allocation (`ClusterQuotaAllocator`, `Logic.dll`)

New allocation layer sitting between the raw model predictions (Step 3) and the existing cascading placement logic (Step 4). Solves a different problem than cascading placement: **each Section/Cluster/DeviceType combination has a fixed quota (`TargetCount`)**, and devices need to be distributed to fit those quotas rather than simply placed wherever the model's top prediction points.

## 📍 Design

Quota is treated as a plain, source-agnostic **input parameter** — the allocator has no opinion on where the quota numbers come from (currently a hardcoded table in `Program.cs`'s `Main()`; historical-data lookup or manual entry can be wired in later without touching the allocator).

```
ClusterQuotaAllocator.Allocate(
    predictions: List<DevicePrediction>,   // full raw model output, unfiltered
    quotas: List<ClusterQuota>              // Section, Cluster, DeviceType, TargetCount
) -> AllocationResult
```

## 📍 Two-Pass Logic

1. **Pass 1 — Ranked assignment.** Devices are assigned to their predicted cluster up to quota, ranked by confidence.
2. **Pass 2 — Floating-pool backfill.** Devices that didn't fit in Pass 1 go into a floating pool and get redistributed against remaining vacancies (`InitialDeficits`).

## 📍 `MinCascadeConfidence` Floor

🆕 During re-evaluation (a device cascading to its next-highest candidate cluster because its top choice was full), the candidate's confidence is checked against a floor (currently reusing the existing `0.60` threshold used elsewhere in the pipeline) **before** treating a vacancy as fillable:

- Candidate ≥ `MinCascadeConfidence` → cascade normally, assign if vacant.
- Candidate < `MinCascadeConfidence` → do **not** force-place. Route to the unknown/floating dump instead of accepting a low-confidence placement that would just need manual correction later anyway.

## 📍 UNKNOWN Split — Moved Earlier 🆕

**Bug found and fixed:** UNKNOWN devices (by `DeviceType`, not just Section/Cluster) were previously only detected *after* allocation, at the old Step 4/5 `SplitKnownUnknown` check. Because `allDeviceResults` was built only from `AllocationResult.Assigned`, any device that got silently resolved by backfill — or fell into `Floating` (invisible past that point) — meant `SplitKnownUnknown` never found any unknowns, even when some existed. A secondary bug also mislabeled `DeviceType == "UNKNOWN"` devices as `"Assign"` because the floating-dump status logic only checked `Section`/`Cluster`, not `DeviceType`.

**Fix:** UNKNOWN devices are now split out from the raw predictions **before** allocation runs (new Step 3.5a), so `Floating` only ever contains devices that are fully known (type, section, cluster all resolved) but simply didn't fit a quota.

## 📍 Core Data Contracts

| Model | Represents |
|---|---|
| `DevicePrediction` | Normalized shape (Section, Cluster, DeviceId, DeviceType, Score) built from `PipelineResult` before allocation runs |
| `ClusterQuota` | One row of the quota table (Section, Cluster, DeviceType, TargetCount) — currently hardcoded inline in `Program.cs`'s `Main()` |
| `AllocationResult` | `Assigned` (placed devices) · `Floating` (unplaced, fully-known devices) · `InitialDeficits` (vacancies before backfill) · `VacancyReport` (vacancies after backfill) |
| `AllocatedDevice` | A device once placed by the allocator |
| `VacancyReportEntry` | One row of "this quota bucket still has N slots open" |

`ClusterQuotaAllocator.PrintVacancyReport(...)` prints how many slots per quota bucket remain open.

---

# 4️⃣ Logic Placement (`Logic.dll`, C#)

Separate class library consumed by `Program.cs` after the Python pipeline and `ClusterQuotaAllocator` return their results. Handles cascading device placement, numeric similarity scoring, cluster grouping, and unknown device routing. 🆕 Internally split into distinct namespaces:

| Namespace | Role |
|---|---|
| `Logic` (root) | Quota allocation engine — `ClusterQuotaAllocator`, `DevicePrediction`, `ClusterQuota`, `AllocationResult`, `AllocatedDevice`, `ClusterSuggestion`, `VacancyReportEntry` (see §3.5) |
| `Logic.LogicAssignUser` | The "human in the loop" layer, driven by `LogicAssignment` — `ClusterGroup`, `ScoredDevice`, `UnknownDumpEntry` |
| `Logic.Models` | `DeviceResult` (final per-device record fed into `LogicAssignment`) and `ClusterPrediction` (lighter internal prediction shape) |
| `Logic.SimilarityScore` | `NumericalSimilarity` — the older device-ID-numeric-proximity method; superseded for cluster *suggestion* by the model-driven top-3 feature, but still the actual placement mechanism when a user manually corrects a device (`AssignByNumericSimilarity`) |

## 📍 Purpose

- Takes ML-predicted Section/Cluster results (now post-quota-allocation) and resolves them into final placements using cascading logic.
- Separates "known" devices (confidently placed) from "unknown" devices (routed to manual review / suggestion flow).
- Provides the final `DeviceResult` objects consumed by the C# UI and by Faiz's downstream integration.

## 📍 Core Models

| Model | Represents |
|---|---|
| `DeviceResult` | Final per-device placement result. 🆕 Fields confirmed: `Customer`, `ProjectCode`, `DeviceId`, `DeviceType`, `Section`, `Cluster`, `Confidence` |
| `ClusterGroup` | A group of devices placed together under one cluster, used for table/UI display |
| `ScoredDevice` | A device paired with its numeric similarity score against a candidate cluster/group |
| `UnknownDumpEntry` | Record shape for devices that couldn't be confidently placed — dumped to `unknown_dump.json` for review |

> **TODO:** fill in exact property types (not just names) for each model — useful for Faiz's integration reference.

## 📍 Key `LogicAssignment` Methods (used in `Program.cs`) 🆕

| Method | Purpose |
|---|---|
| `SplitKnownUnknown(allDeviceResults)` | → `(knownDevices, unknownDevices)` |
| `DumpUnknown(unknownDevices)` | Writes `unknown_dump.json` |
| `BuildClusterGroups(knownDevices)` | → `List<ClusterGroup>` |
| `PrintClusterTable(clusterGroups[, sectionFilter])` | Console table output |
| `AssignByNumericSimilarity(correctedEntry, knownDevices)` | Placement for a manually corrected device |
| `PlaceDevice(placed, clusterGroups)` / `MarkAsAssigned(deviceId, projectCode, section, cluster)` | Placement bookkeeping |

## 📍 Numeric Similarity Scoring

Cascading placement uses numeric similarity between a device's ID structure (digit patterns, counts, leading zeros, etc.) and existing cluster members, to decide whether an ambiguous device can be folded into an existing `ClusterGroup`. 🆕 Confirmed: leading zeros and field width are meaningful (not noise) — `count_num_digit` (field width) and `numeric_remove_zero` (significant value) are both used, so an unusually wide/narrow numeric field naturally lowers confidence via the KNN OOD scorer rather than being hard-blocked.

> **TODO:** document the actual scoring formula/weights once finalized.

## 📍 Unknown Device Handling

- Devices that fail confident placement are collected as `UnknownDumpEntry` records and dumped to `unknown_dump.json` for manual review.
- `SuggestTopClusters` — 🆕 retired in favor of the model-driven top-3 suggestion feature (`ModelClusterSuggestionService`, calling a new `predict_sectioncluster.py` action branch `top_clusters` that exposes `predict_proba()` directly), which replaced the old `NumericalSimilarity`-based suggestion approach.

> **TODO:** document `ModelClusterSuggestionService` / `top_clusters` action signature (inputs/outputs, how N is chosen, tie-breaking rules).

---

# 5️⃣ Manual Correction Flow (C# ↔ Python)

Spans both the C# (`Logic.dll`, `Program.cs`) and Python (`predict_equipment.py` / `predict_sectioncluster.py`) layers. 🆕 Confirmed: **Type** corrections and **Section/Cluster** corrections are two distinct code paths, not unified:

| Correction type | What runs | Persists? |
|---|---|---|
| Device **Type** correction (`correctType` non-empty) | Lightweight dict path — updates `initial_map.pkl` directly, skips `partial_fit` | ✅ Yes, immediately |
| Device **Section/Cluster** correction | Logged to `manual_assign_sectioncluster.json` via `save_manual_assign_sectioncluster()` in `predict_sectioncluster.py` | ✅ Yes, but only as a queued record — XGBoost requires full retraining (no incremental `partial_fit` equivalent), so these feed a **periodic retrain cycle**, not a live model update |
| Any correction submitted | **Logic placement** re-runs in `Logic.dll` to re-place the device | Depends on placement logic |

## 📍 Lightweight Correction Path (Type only)

When a manual type correction is submitted:

- Updates `initial_map.pkl` (the prefix → equipment type dictionary) directly.
- **Does not** call `partial_fit` on the SGD model.
- This avoids the class-mismatch error described in the Incremental Learning Notes above, where `initial_map.pkl` changes previously caused `partial_fit` to choke on an altered class set.

## 📍 Section/Cluster Correction Path 🆕

- Corrections are written to `manual_assign_sectioncluster.json` (constant: `MANUAL_ASSIGN_SECTION_CLUSTER`) via atomic file writes.
- Routed through `save_correction` vs `predict` action dispatch in `run_cli()`.
- Not consumed automatically — intended for the data team to pull into the next XGBoost retrain cycle.

---

# 6️⃣ C# Orchestration (`Program.cs`)

Steps run in order after SQL retrieval and JSON input are ready. 🆕 Updated to reflect the new quota-allocation step and confirmed Step 9 behaviour:

| Step | Description |
|---|---|
| 1–3 | Call Python pipeline (Device Type → Section → Cluster), collect `PipelineResult[]` |
| 🆕 3.5a | Split devices into KNOWN vs UNKNOWN **from raw predictions**, before allocation (`LogicAssignment` instantiated here, moved up from old Step 4) |
| 🆕 3.5b | `ClusterQuotaAllocator.Allocate(...)` — two-pass quota fitting on known devices, producing `AllocationResult` |
| 4 | Build `ClusterGroup`s from `AllocationResult.Assigned` via `Logic.dll` |
| 5 | Dump unknown devices to `unknown_dump.json` (`UnknownDumpEntry[]`) |
| 6 | `LogicAssignment.DumpUnknown` — writes unknown dump |
| 7 | Print result tables (Unicode-safe console output, `PrintClusterTable`) |
| 8 | Prompt for manual correction (see Manual Correction Flow) |
| 9 | Finalize/persist output for downstream consumption (Faiz's DLL integration) |

> **TODO:** confirm exact behavior of Step 9 (what gets persisted, in what format) once finalized — Faiz still consumes `Logic.dll` output directly.

---

# 7️⃣ SQL Integration (`PythonSQL.cs`)

Retrieves device data from SQL Server and converts it to the JSON input format the Python pipeline expects.

| Item | Value |
|---|---|
| Database | `XenCreator` |
| Table | `DummyInput` |
| Project detection | Dynamic `SELECT DISTINCT` query (replaces old hardcoded project list) |
| Output | JSON matching the `predict_equipment.py` input shape (`project_code`, `customer_code`, `data_ids[]`) |
| 🆕 Methods | `QueryToJsonAsync` (returns JSON string in memory), `QueryToJsonFileAsync` (saves JSON to disk — writes `data/devices.json`, used as Step 1) |

**Registry (SQL connection string)**

| Key | Value |
|---|---|
| Hive | `HKEY_CURRENT_USER\Software\XenxibleIdentifier` |
| Field | `connectionstring` |

> Confirmed working via `Microsoft.Win32.Registry.GetValue()` directly (an earlier registry path mismatch — `SOFTWARE\XenxibleIdentifier\Software` instead of `HKEY_CURRENT_USER\Software\XenxibleIdentifier` — was fixed in a prior cycle).

---

# 🍀 C# Subprocess Protocol

C# spawns Python as a child process for each ML step via `System.Diagnostics.Process`.

**Paths (resolved at runtime, relative to executable)**

| Variable | Points to |
|---|---|
| `SCRIPT_TYPE` | `predict_equipment.py` |
| `SCRIPT_PIPELINE` | `predict_sectioncluster.py` |
| `SQL_OUTPUT_JSON` | `DeviceCluster/Prediction_service/data/devices.json` |

> 🆕 `PROJECT_JSON` (hardcoded test-file input path) has been removed — device data now sources exclusively from SQL via `PythonSQL.cs`.

**Step 1 request/response:**
```jsonc
// stdin → predict_equipment.py
{ "project_code": "A1825", "customer_code": "Lipico", "data_ids": ["CR1234", "PU001"] }

// stdout ← predict_equipment.py
[{ "data_id": "CR1234", "data_type": "COOLER", "confidence": 0.92, "reason": "sgd_strong" }]
```

**Step 2 & 3 request/response:**
```jsonc
// stdin → predict_sectioncluster.py
{ "records": [{ "device_id": "CR1234", "customer": "Lipico", "project": "A1825" }],
  "export_raw_csv_path": "cluster_prediction_raw.csv" }   // 🆕 optional field, see §2/3 raw export

// stdout ← predict_sectioncluster.py
[{
  "DEVICE_ID": "CR1234", "CUSTOMER": "Lipico", "PROJECT": "A1825",
  "PREDICTED_SECTION": "SectionA", "SECTION_CONFIDENCE": 87.4,
  "PREDICTED_CLUSTER": "Cluster2", "CLUSTER_CONFIDENCE": 74.1,
  "REJECTION_REASON": "", "FORMAT_WARNING": ""
}]
```

> **TODO:** add a Step 3.5/4 entry here showing what `Program.cs` passes into `ClusterQuotaAllocator` and `Logic.dll`, and what it gets back (`AllocationResult` / `DeviceResult[]` / `UnknownDumpEntry[]` shape), once fully stable.

---

## 📍 Configuration Files

| File | Purpose |
|---|---|
| `Config_devicetype.json` | Paths to all Device Type model artefacts |
| `config_sectioncluster.json` | `model_folder` path + `unknown_threshold` (0.60) |
| `config.ini` | Runtime `MODEL_DIR`, `OUTPUT_DIR`, `UNKNOWN_THRESHOLD` |

## 📍 Environment Variables (`predict_equipment.py`)

| Variable | Default | Purpose |
|---|---|---|
| `DEVICE_CLUSTER_CONFIG` | `./predict_equipment_folder/Config_devicetype.json` | Override config path |
| `MAX_BATCH_SIZE` | `5000` | Override max prediction batch size |
| `LOG_LEVEL` | `INFO` | Python logging level |

---

# 🆕 9️⃣ Known Bugs — Fixed This Cycle

| Bug | Root cause | Fix |
|---|---|---|
| Oiltek devices all returning `UNKNOWN` / `N/A` confidence at Section prediction | Training data stored customer as `'OILTEK'` (uppercase) in `pipeline_config.pkl`'s `known_customers`, but SQL source passed `'Oiltek'` (mixed case) — `check_entities()` rejected every row as an unseen customer before the model ever ran | Added `.upper()` normalization in both `check_entities()` (on `cust`) and `build_features()` (on `df["CUSTOMER"]` before `le_customer.transform()`) |
| Device IDs with periods getting mangled (`V001.21` → `V00121`) in output | `make_display_id_series()` applied the internal feature-normalization regex `r"[\s\-_\.]"` to the display/output ID as well, not just internal features | Removed the character-stripping regex from `make_display_id_series()`, leaving only `.str.strip().str.upper()` |
| XGBoost model predicting only 3 sections for devices trained across 5 sections (Oiltek A1827) | `PROJECT` is absent from `feature_columns` in the training script — model can't distinguish between different projects under the same customer | **Identified, not yet fixed** — requires adding `PROJECT` to `feature_columns` and retraining `model_section.pkl` |
| `export_csv_path` arriving as `None` in Python despite valid predictions returning | Almost certainly a stale build — VS running a compiled version predating the `export_raw_csv_path` field on `PipelinePredictRequest.cs` | **In progress** — sentinel debug file added; Clean Solution → Rebuild Solution is the next step to confirm |
| `initial_map.pkl` corrections silently breaking `predict_equipment.py` persistence (`ref_id_set` desync, duplicate-index `ValueError`) | `new_row` DataFrame built with only 3/4 required columns (missing `initial`); `ref_id_set` not updated by `manual_correction_lightweight()`, causing re-append duplicates on next predict | Replaced `ref_id_set`-based dedup with direct membership check against `reference_df['data_id']`, upsert pattern with `existing_ids.add(...)` inside the loop, wrapped append block in `try/except` with `traceback.print_exc()` |

---

## 📍 Filepath Reference

| Path | Description |
|---|---|
| `Prediction_service/DeviceCluster/Program.cs` | C# orchestrator entry point |
| `Prediction_service/DeviceCluster/DeviceCluster.slnx` | .NET 10 solution file |
| `Prediction_service/DeviceCluster/predict_equipment.py` | Device Type inference script |
| `Prediction_service/DeviceCluster/predict_sectioncluster.py` | Section & Cluster inference script |
| `Prediction_service/DeviceCluster/Logic/Logic.csproj` | Logic.dll class library project (SDK-style, `net10.0`) |
| `Prediction_service/DeviceCluster/PythonSQL.cs` | SQL Server → JSON retrieval |
| `Prediction_service/DeviceCluster/PythonClient.cs` | Python subprocess invocation wrapper |
| `Prediction_service/DeviceCluster_Prediction/model_config/` | All `.pkl` model files |
| `Prediction_service/DeviceType_Prediction/Config_devicetype.json` | Device Type config |
| `Prediction_service/DeviceCluster_Prediction/config_sectioncluster.json` | Section/Cluster config |
| `TestDevice/<project>.json` | Input device IDs per project (🆕 now legacy — SQL is the live source) |
| `bin/Debug/net10.0/DeviceCluster.exe` | Debug build executable |
| `1.training_model/Section XGB Model - Model Training.ipynb` | Training notebook |
| 🆕 `data/devices.json` | Raw dump of the SQL source table, written by `PythonSQL.QueryToJsonFileAsync` (Step 1) |
| 🆕 `data/floating_deviceid.json` | Devices the quota allocator couldn't place (Step 3.5) |
| 🆕 `data/unknown_dump.json` | Devices needing manual type/section/cluster assignment (Step 5–6) |
| 🆕 `cluster_prediction_raw.csv` | Wide-format CSV of raw cluster probabilities per device, triggered via `export_raw_csv_path` in Step 3 — currently being debugged |
| 🆕 `manual_assign_sectioncluster.json` | Queued Section/Cluster corrections pending the next XGBoost retrain cycle |

> **TODO:** confirm the actual `Logic.csproj` path — placeholder above assumes it sits under `DeviceCluster/Logic/`; update once the real project structure is checked.

---

## 📍 Deployment Notes

- Python 3.13 must be installed at the path hardcoded in `Program.cs`.
- All `.pkl` model files must be present in `DeviceCluster_Prediction/model_config/`.
- Input devices read from SQL (`XenCreator` → `DummyInput`) — `TestDevice/<project>.json` is now a legacy fallback, not the live path.
- No network calls — fully local inference (SQL retrieval is the only external dependency).
- `Logic.dll`'s `.csproj` was manually converted to SDK-style, targeting `net10.0` — keep this in mind when referencing it from consumer projects (e.g. Faiz's integration) to avoid legacy-style project reference issues.
- 🆕 `DeviceClusterConsoleApp` currently references the three DLLs via **Assembly file references**, not **Project references** — this is a known recurring source of "I rebuilt but nothing changed" bugs (including the current `export_csv_path` issue). Switching to Project references has been discussed repeatedly but not yet implemented; recommended as a permanent fix.
- For production: use `dotnet publish` instead of debug build.

---

## 📍 Open TODOs Before This Doc Is Fully Synced

- [ ] Confirm exact property **types** (not just names) for `DeviceResult`, `ClusterGroup`, `ScoredDevice`, `UnknownDumpEntry`, `AllocationResult`
- [x] ~~Document `SuggestTopClusters` signature and selection logic~~ → retired; document `ModelClusterSuggestionService` / `top_clusters` action instead
- [ ] Document numeric similarity scoring formula used in cascading placement
- [ ] Confirm `Logic.csproj` real path and add to filepath table
- [ ] Add Step 3.5/4 (quota allocation + Logic.dll) request/response example to the Subprocess Protocol section
- [x] ~~Confirm whether dictionary-only correction and full `user_manual_assign` are exposed as separate UI actions or unified~~ → confirmed: Type corrections use the lightweight dict path; Section/Cluster corrections are logged separately for periodic retrain
- [ ] Document `PythonSQL.cs` / `PythonClient.cs` method signatures in full
- [ ] 🆕 Resolve `export_csv_path` arriving as `None` — confirm via sentinel debug file, then Clean/Rebuild
- [ ] 🆕 Retrain `model_section.pkl` with `PROJECT` added to `feature_columns`
- [ ] 🆕 Switch `DeviceClusterConsoleApp` to Project references to eliminate the stale-DLL trap
- [ ] 🆕 Confirm exact `MinCascadeConfidence` value if it diverges from the reused `0.60` threshold