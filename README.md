# DeviceCluster — Reference Guide

| | |
|---|---|
| **Full pipeline** | SQL (per-project, filtered) → Device Type → Section → Cluster → Quota Allocation (device-centric reassignment) → Logic Placement |
| **Runtime** | `Model.dll` reads SQL Server → JSON → Python ML scripts predict → `Logic.dll`'s `DevicePipeline` fits quotas, splits floating devices, groups clusters → `Program.cs` (or any other caller) renders output |

> Last synced 21 August 2026 (previous version: 20 August 2026, same day). Changes are marked with 🆕 where useful.

---


# 📍 Project Status

| Component | Status |
|------------|--------|
| Device Type Prediction              | ✅ Completed |
| Device Section Prediction           | ✅ Completed |
| Device Cluster Prediction           | ✅ Completed |
| C# Service Integration              | ✅ Completed |
| SQL Integration (`PythonSQL.cs`)    | ✅ Completed — per-project filtering, project listing |
| Incremental Learning                | ✅ Completed |
| DLL Development (`AppRegistryEditor.dll`, `Logic.dll`, `Model.dll`) | ✅ Completed |
| Model-based Top-3 Cluster Suggestion `predict_sectioncluster.py` from XGBoost | ✅ Completed |
| Update Model                        | ✅ Completed (Once workflow is complete, need retraining) |
| 🆕 `Logic.dll` orchestration (`DevicePipeline`) | ✅ Completed — single callable entry point, no console dependency |
| 🆕 Quota patterns sourced from SQL (`dbo.PatternCluster`) | ✅ Completed for `SECTION 2` (real); Sections 1, 3-8 are placeholder/dummy data pending real numbers |
| 🆕 Device-centric reassignment pool (Stage 3) | ✅ Completed — matches the flowchart design, uses per-device ranked cluster candidates |
| Unattended / headless mode (`--unattended`) | ✅ Completed |
| Floating device output (`dbo.DeviceReviewQueue`) | ✅ Completed — replaces `floating_deviceid.json` / `unallocated_device_ids.json`, single SQL table with `Category` column, reclassification via `MERGE` |
| 🆕 Assigned device output (`dbo.OutputDeviceAssignment`) | ✅ Completed — successfully placed devices are now persisted to SQL too, not just returned in-memory to the caller |
| DLL Testing | ✅ Verified via live runs against real SQL Server + trained models (no automated test suite yet) |


---
Script active : DeviceClusterConsoleApp [`Program.cs`]
---
# 📍 `Logic.dll` Status

| Stage | Status |
|-------|--------|
| Stage 1 – Initial Cluster Assignment (quota-constrained top-N per bucket) | ✅ Completed |
| Stage 2 – Exceeded & Vacancy Evaluation | ✅ Completed |
| Stage 3 – Reassignment Pool (device-centric, ranked candidates) | ✅ Completed |
| Model-based Top-3 Cluster Prediction | ✅ Completed |
| Floating device handling (unknown-prediction vs known-but-unallocated) | ✅ Completed — persisted to `dbo.DeviceReviewQueue`, reclassification via SQL `MERGE` |
| `DevicePipeline` orchestration entry point | ✅ Completed |
| Logic.dll ↔ Model.dll dependency | ✅ `ProjectReference` (was previously undocumented/absent) |
| Automated test suite | ❌ Not started — verification so far is manual, live-run based |

---

## 📍 Recent Fixes

- ✅ `Logic.dll` now has a `ProjectReference` to `Model.dll` — fixes stale-DLL rebuild issues from the old `HintPath`-only setup
- 🆕 `floating_deviceid.json` / `unallocated_device_ids.json` replaced by a single SQL table (`dbo.DeviceReviewQueue`, `Category` column) — reclassification is now a plain SQL `MERGE` (UPDATE on conflict), eliminating the cross-file reconciliation logic entirely
- 🆕 Cross-**table** reconciliation between `dbo.OutputDeviceAssignment` and `dbo.DeviceReviewQueue` — a device that flips outcome between runs (assigned → floating, or floating → assigned) has its stale row deleted from the other table in the same statement, so a device is never recorded in both at once
- ✅ Quota allocator's Stage 3 rewritten from bucket-centric backfill to device-centric reassignment (matches the flowchart: each floating device tries its own ranked cluster candidates by model percentage, highest-scoring device first)
- ✅ SQL queries are project-scoped (`WHERE ProjectCode = @ProjectCode`) — previously the whole shared table was read unfiltered
- ✅ Fixed case-sensitivity issue (`OILTEK` vs `Oiltek`)
- ✅ Fixed device ID formatting issue (e.g. `V001.21` preserved correctly)

---

# 🍀 Architecture Overview

```
     Data Source: SQL Server (XenCreator DB → dbo.DummyTestingData table)
     One shared table, many projects — every query filtered by ProjectCode
                    │
                    │  PythonSQL.cs (Model.dll): ListAvailableProjectsAsync,
                    │  LoadProjectDataAsync(table, projectCode, outputDir)
                    ▼
                Input JSON  (data/{ProjectCode}_devices.json)
      (project_code, customer_code, data_ids[])
                    │
                    │
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 1 — Device Type [predict_equipment.py]       │
│ Hybrid: exact match + SGD + TF-IDF cosine         │
│         + dict 22 equipment classes               │
└───────────────────┬───────────────────────────────┘
                    │
                    │  DeviceTypeResult[]
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 2 — Section [predict_sectioncluster.py]      │
│ XGBoost (27+ engineered features)                 │
│       + OOD KNN penalty applied                   │
└───────────────────┬───────────────────────────────┘
                    │
                    │  PipelineResult[] (with PREDICTED_SECTION)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 3 — Cluster [predict_sectioncluster.py]      │
│ XGBoost chained on Predicted Section              │
│ Confidence penalised by Section confidence        │
│ 🆕 also returns TOP_CLUSTERS: each device's        │
│    top-N ranked cluster candidates + % (from the  │
│    same predict_proba() call, not discarded)      │
└───────────────────┬───────────────────────────────┘
                    │
                    │  PipelineResult[] (PREDICTED_CLUSTER + TOP_CLUSTERS)
                    ▼
┌─────────────────────────────────────────────────────┐
│ Step 3.5 — Quota Allocation [Logic.dll]             │
│ Quotas loaded from dbo.PatternCluster, filtered by  │
│ CustomerCode (QuotaCatalog.LoadQuotasFromDbAsync)   │
│                                                      │
│ ClusterQuotaAllocator.Allocate:                     │
│  Stage 1 — per bucket, take top-N by score          │
│  Stage 2 — build floating pool from the rest        │
│  Stage 3 — device-centric reassignment pool:        │
│    highest-score device first, tries its own        │
│    TOP_CLUSTERS candidates in order, takes the      │
│    first one with room; exhausted → stays floating  │
│                                                      │
│ Floating pool split by cause (SplitFloatingPool):    │
│   any UNKNOWN field    → dbo.DeviceReviewQueue       │
│                           Category=UnknownPrediction │
│   known, no quota room → dbo.DeviceReviewQueue       │
│                           Category=Unallocated       │
│ (single table, upserted via SQL MERGE — a device's   │
│  Category flips with a plain UPDATE if its           │
│  classification changes between runs)                │
└───────────────────┬─────────────────────────────────┘
                    │
                    │  AllocationResult (Assigned / Floating /
                    │  InitialDeficits / VacancyReport)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 4/5 — Logic Placement [Logic.dll]            │
│ Cluster grouping → ClusterGroup / ScoredDevice    │
│ (Assigned devices are already known by construction│
│  — no separate known/unknown split needed here)   │
│                                                     │
│ 🆕 Upserted into dbo.OutputDeviceAssignment (MERGE │
│    on DeviceId+ProjectCode) — Section, Cluster,    │
│    Confidence, IsBackfill, OriginalCluster         │
└───────────────────┬───────────────────────────────┘
                    │
                    │  DevicePipelineResult (everything above,
                    │  bundled — Request, predictions, allocation
                    │  result, floating splits, cluster groups)
                    ▼
        Program.cs — prints tables via DevicePipelineCallbacks
        (or --unattended: no console interaction at all),
        prompts for manual correction, exits
```

**Orchestration note:** the entire Step 1 → Step 5 sequence above is one callable method — `Logic.DevicePipeline.RunAsync(sqlReader, client, logic, sqlSourceTable, sqlQuotaTable, scriptType, scriptPipeline, sqlOutputDir, projectCode, callbacks)` — living in `Logic.dll`, not hardcoded into `Program.cs`. Any caller (a future UI, a scheduler, an API) can call it directly with no console dependency; `callbacks` is fully optional and the method never touches `Console` itself.

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
composite_scores  = (ALPHA_PREFIX_WEIGHT * prefix_scores) + ((1 - ALPHA_PREFIX_WEIGHT) * sims)
composite_score   = (0.65 × prefix_score)                 + (0.35 × cosine_similarity)
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
| 🆕 `TOP_CLUSTERS` | Top-N (default 3) ranked `{cluster, probability}` candidates per device, from the same `predict_proba()` call — not just the argmax winner. Used by quota allocation's Stage 3 reassignment pool. |
| `REJECTION_REASON` | Set if device is hard-blocked |
| `FORMAT_WARNING` | Set if numeric field width is outside training distribution |

## 📍 Model Artefacts

| File | Contents |
|---|---|
| `model_section.pkl` | Trained XGBoost section model |
| `model_cluster.pkl` | Trained XGBoost cluster model |
| `pipeline_config.pkl` | All label encoders, feature lists, OOD scaler/KNN, known customers, numeric width stats |

> Note: the `export_raw_csv_path` field previously on `PipelinePredictRequest` (a debug-only raw `predict_proba` dump per device, written to a CSV file) has been removed from the C# request — `Program.cs` no longer sets it. `predict_sectioncluster.py` still supports it (it just always receives `None` now), and the separate `export_cluster_csv` action is untouched. Its role — surfacing more than just the top-1 cluster prediction — is now covered properly by `TOP_CLUSTERS`, which feeds live reassignment logic instead of a static debug file.

---

# 🆕 3. Quota Allocation (`ClusterQuotaAllocator`, `Logic.dll`)

Allocates devices to clusters based on predefined quotas before cascading placement.

### 📍 Purpose
- Ensures each **Section–Cluster–DeviceType** meets its target quota.
- Uses model predictions while respecting capacity constraints.

### 📍 Quota Source
Quotas are **not** hardcoded anywhere in code. They're loaded live from SQL Server:

```
QuotaCatalog.LoadQuotasFromDbAsync(connectionString, tableName, customerCode)
  → SELECT Section, Cluster, DeviceType, TargetCount
    FROM dbo.PatternCluster WHERE CustomerCode = @CustomerCode
```

- Table: `dbo.PatternCluster` (created/seeded via `Prediction_service/DeviceCluster/sql/PatternCluster.sql`)
- Keyed by `CustomerCode` — the working assumption is that a client's different projects share the same physical plant layout, so every project under one customer gets the same pattern
- **Only `SECTION 2` reflects a real, confirmed pattern** (OILTEK). `SECTION 1, 3-8` are randomly fabricated placeholder rows, clearly marked in the seed script, so the pipeline has *something* to allocate against during testing — not real capacity numbers.

### 📍 Allocation Flow (`ClusterQuotaAllocator.Allocate`)

**Stage 1 – Initial quota-constrained allocation**
- For each `(Section, Cluster, DeviceType)` quota bucket, take the top-N highest-scoring predictions that match it exactly, up to `TargetCount`.
- Any shortfall here is recorded into `InitialDeficits`.

**Stage 2 – Build the floating pool**
- Everything not claimed in Stage 1 becomes the floating pool.

**Stage 3 – Device-centric reassignment pool** 🆕
- Floating devices are processed **highest-score first**.
- Each device tries its own **ranked cluster candidates** (`TOP_CLUSTERS`, highest model percentage first, same Section) in order.
- The first candidate bucket that still has room wins — the device is assigned there (`IsBackfill = true`, `OriginalCluster` records its original top-1 pick).
- A device that exhausts all its candidates without finding room stays floating.
- Any quota bucket still short after Stage 3 is reported into `VacancyReport`.

> This replaced an earlier bucket-centric backfill (iterate deficits, pull best-scoring device matching Section+DeviceType, ignoring the device's own cluster preference). The device-centric version matches the actual hand-drawn Stage 1/2/3 flowchart design.

### 📍 Floating Device Handling
- Floating devices (didn't get placed in Stage 1 or 3) are split by `LogicAssignment.SplitFloatingPool`, **not** separated before allocation — every device is attempted in Stages 1 and 3 regardless of whether its prediction is `UNKNOWN`.
- Split outcome: any `UNKNOWN` field (`DeviceType`/`Section`/`Cluster`) → `dbo.DeviceReviewQueue` with `Category='UnknownPrediction'`; fully known prediction but no quota room → same table with `Category='Unallocated'`.

### 📍 Data Models

| Model | Description |
|--------|-------------|
| `DevicePrediction` | Prediction result used for allocation — now includes `TopClusters` (ranked candidates) |
| `ClusterQuota` | Target quota for each Section–Cluster–DeviceType (loaded from SQL, not hardcoded) |
| `AllocationResult` | Assigned devices, floating devices, initial deficits, and vacancy report |
| `AllocatedDevice` | Successfully allocated device (`IsBackfill`, `OriginalCluster`) |
| `VacancyReportEntry` | Remaining quota after allocation |

### 📍 Console Reports
- `PrintFulfilledReport(quotas, vacancyReport)` — every bucket fully satisfied, sorted numerically by Section then Cluster: `SECTION X CLUSTER Y - N fulfilled DeviceType`
- `PrintVacancyReport(vacancyReport)` — every bucket still short, same sort/format with `vacant` instead of `fulfilled`

---

# 4️⃣ Logic Placement (`Logic.dll`, C#)


Processes the prediction results after quota allocation and determines the final device placement.

### 📍 Responsibilities
- Group assigned devices into clusters, and upsert them into `dbo.OutputDeviceAssignment`.
- Split the floating pool by cause and upsert into `dbo.DeviceReviewQueue`.
- Support manual correction (numeric-similarity reassignment, cluster suggestion, placement).
- Orchestrate the whole pipeline end-to-end (`DevicePipeline`).

### 📍 Components

| Namespace / File | Purpose |
|-----------|---------|
| `Logic` (`ClusterQuotaAllocator.cs`) | Quota allocation and core allocation models |
| `Logic` (`DevicePipeline.cs`, `DevicePipelineCallbacks.cs`, `DevicePipelineResult.cs`) 🆕 | Single-entry-point orchestration for the whole SQL → predict → allocate → group pipeline |
| `Logic` (`QuotaCatalog.cs`) 🆕 | Loads quota patterns from `dbo.PatternCluster` |
| `Logic.LogicAssignUser` | Device placement, cluster grouping, floating-device handling |
| `Logic.Models` | Shared data models for prediction and placement (`DeviceResult`, `ClusterPrediction`) |
| `Logic.SimilarityScore` | Numeric similarity used for manual device reassignment |

### 📍 Core Models

| Model | Description |
|-------|-------------|
| `DeviceResult` | Final device placement result |
| `ClusterGroup` | Devices grouped under the same cluster |
| `ScoredDevice` | Device with similarity score |
| `UnallocatedDumpEntry` | Floating device with a known prediction but no quota room, needs manual placement |

### 📍 Processing Flow
1. `DevicePipeline.RunAsync` runs the whole automated sequence (SQL → predict → allocate → group), firing `DevicePipelineCallbacks` at each checkpoint.
2. `LogicAssignment.SplitFloatingPool` splits the quota allocator's floating pool by cause.
3. `DumpFloating` / `DumpUnallocated` upsert into `dbo.DeviceReviewQueue` via a per-device SQL `MERGE` (keyed on `DeviceId`+`ProjectCode`) — insert if new, update `Category`/prediction/`DumpedAt` if the device already exists, so a reclassified device is a plain update, not a delete-from-one-table-insert-into-another.
4. Group assigned devices by cluster (`BuildClusterGroups`).
5. 🆕 `DumpAssigned` upserts the same assigned devices into `dbo.OutputDeviceAssignment` via `MERGE` (keyed on `DeviceId`+`ProjectCode`) — joins back `IsBackfill`/`OriginalCluster` from the allocator's `AllocatedDevice` list, since that detail is dropped once devices are mapped to the generic `DeviceResult` shape used for grouping.
6. 🆕 Both dump paths end with a `DELETE` against the *other* output table for the same `DeviceId`+`ProjectCode`, in the same round trip — so a device that changes outcome between runs (assigned ↔ floating) never lingers as a stale row in the table it left. Verified by planting stale rows in both directions and confirming a single run cleared both.
7. Apply numeric similarity for manual reassignment on request (`AssignByNumericSimilarity`, `PlaceDevice`, `MarkAsAssigned` — the latter sets `Status='assigned'` in `dbo.DeviceReviewQueue`).

### 📍 Floating Device Handling
- Devices with an UNKNOWN type/section/cluster are routed to `dbo.DeviceReviewQueue` with `Category='UnknownPrediction'`.
- Devices with a known prediction but no quota vacancy are routed to the same table with `Category='Unallocated'`.
- Cluster suggestions during manual correction are generated using the model-based Top-3 prediction service (`ModelClusterSuggestionService` / `NumericalSimilarity` — a *different* mechanism from `TOP_CLUSTERS`, based on similarity to already-known devices rather than the model's own probability output).
---

# 5️⃣ Manual Correction Flow (C# ↔ Python)

Processes user corrections for device predictions across the C# and Python layers. Only runs in interactive mode — skipped entirely when `Program.cs` is invoked with `--unattended`.

### 📍 Correction Types

| Correction | Action | Saved |
|------------|--------|-------|
| Device Type | Updates `initial_map.pkl` | ✅ Immediate |
| Device Section/Cluster | Records correction in `manual_assign_sectioncluster.json` for future model retraining | ✅ Queued |

### 📍 Processing Flow

- **Device Type**
  - Updates the prefix dictionary (`initial_map.pkl`).
  - Does not retrain the SGD model.

- **Device Section/Cluster**
  - Stores corrections for the next XGBoost retraining cycle.
  - No incremental model update.

- **After Any Correction**
  - `Logic.dll` re-runs the placement process to generate the updated device assignment.
---

# 6️⃣ C# Orchestration (`Program.cs`)

`Program.cs` is now a thin caller around `Logic.DevicePipeline.RunAsync` — it owns console I/O (prompts, printing, pauses) but none of the pipeline logic itself.

### 📍 Startup

| Step | Description |
|---|---|
| Connection | `GetConnectionString()` reads the SQL connection string from the Windows Registry (`HKEY_CURRENT_USER\Software\XenxibleIdentifier\connectionstring`) |
| ProjectCode | From `args` (CLI arg, e.g. `DeviceClusterConsoleApp.exe A9998`), or — if omitted and not `--unattended` — lists every available project (`ListAvailableProjectsAsync`) and prompts interactively |
| 🆕 `--unattended` | Skips all console pauses, the manual-correction loop, and the final exit prompt. Requires `ProjectCode` to also be passed as an arg (there's no one to prompt). e.g. `DeviceClusterConsoleApp.exe A9998 --unattended` |

### 📍 Pipeline Call

| Step | Description |
|---|---|
| 1–5 | `DevicePipeline.RunAsync(...)` runs the full automated pipeline (SQL load → predict type → predict section/cluster → quota allocation → floating split/dump → cluster groups), firing `DevicePipelineCallbacks` at each checkpoint so `Program.cs` can print progress (and pause, unless `--unattended`) |
| 6 | Print cluster grouping table (inside the last callback, `OnClusterGroupsBuilt`) |
| 7 | Print unallocated devices pending manual assignment, from the returned `DevicePipelineResult.UnallocatedDevices` |
| Manual correction | Interactive loop (see §5) — skipped when `--unattended` |

### 📍 `DevicePipelineCallbacks` hooks

| Hook | Fires after |
|---|---|
| `OnProjectLoaded` | SQL load (Step 1) |
| `OnDeviceTypesPredicted` | Device type prediction (Step 2) |
| `OnSectionsPredicted` | Section prediction (Step 3) |
| `OnClustersPredicted` | Cluster prediction (Step 3) |
| `OnQuotaAllocated` | Quota allocation (Step 3.5) |
| `OnFloatingSplit` | Floating pool split + dump (Step 3.5) |
| `OnClusterGroupsBuilt` | Cluster grouping (Step 4/5) |

Every hook is nullable — a caller that supplies none of them gets a fully silent, non-interactive run.

---

# 7️⃣ SQL Integration (`PythonSQL.cs`, `Model.dll`)

Retrieves device data from SQL Server and converts it to the JSON input format the Python pipeline expects. Lives in `Model/Services/PythonSQL.cs` — has no knowledge of table names, project codes, or quota patterns; every method takes them as parameters.

| Item | Value |
|---|---|
| Database | `XenCreator` |
| Device table | configurable via `SQL_SOURCE_TABLE` env var, default `DummyTestingData` — one shared table, many projects, every query filtered `WHERE ProjectCode = @ProjectCode` |
| Quota table | configurable via `SQL_QUOTA_TABLE` env var, default `dbo.PatternCluster` — filtered `WHERE CustomerCode = @CustomerCode` |
| Output | JSON matching the `predict_equipment.py` input shape (`project_code`, `customer_code`, `data_ids[]`), written to `data/{ProjectCode}_devices.json` |

### 📍 Methods (`PythonSQL`)

| Method | Purpose |
|---|---|
| `ListAvailableProjectsAsync(tableName)` | `SELECT DISTINCT ProjectCode, CustomerCode` — lets `Program.cs` show a project picker without SSMS |
| `LoadProjectDataAsync(tableName, projectCode, outputDir, suffix)` | One query, filtered by `ProjectCode`, writes `{ProjectCode}_devices.json` and returns both the JSON content and the file path |
| `QueryToJsonAsync(sql)` / `QueryToJsonFileAsync(sql, path)` / `QueryToJsonFileByProjectCodeAsync(sql, dir, suffix)` | Lower-level generic query helpers, predate the project-scoped methods above |
| `ConnectionString` (property) | Exposes the connection string so a caller (e.g. `DevicePipeline`) holding a `PythonSQL` instance can reuse it for other SQL-backed services without threading the raw string separately |

**Registry (SQL connection string)**

| Key | Value |
|---|---|
| Hive | `HKEY_CURRENT_USER\Software\XenxibleIdentifier` |
| Field | `connectionstring` |

> Confirmed working via `Microsoft.Win32.Registry.GetValue()` directly (an earlier registry path mismatch — `SOFTWARE\XenxibleIdentifier\Software` instead of `HKEY_CURRENT_USER\Software\XenxibleIdentifier` — was fixed in a prior cycle).

---

# 🍀 C# Subprocess Protocol

C# spawns Python as a child process for each ML step via `System.Diagnostics.Process` (`Model.Services.PythonClient`).

**Paths (resolved at runtime, relative to executable)**

| Variable | Points to |
|---|---|
| `SCRIPT_TYPE` | `predict_equipment.py` |
| `SCRIPT_PIPELINE` | `predict_sectioncluster.py` |
| `SQL_OUTPUT_DIR` | `Prediction_service/data/` — actual filename is `{ProjectCode}_devices.json`, not a fixed name |

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
{ "records": [{ "device_id": "CR1234", "customer": "Lipico", "project": "A1825" }] }

// stdout ← predict_sectioncluster.py
[{
  "DEVICE_ID": "CR1234", "CUSTOMER": "Lipico", "PROJECT": "A1825",
  "PREDICTED_SECTION": "SectionA", "SECTION_CONFIDENCE": 87.4,
  "PREDICTED_CLUSTER": "Cluster2", "CLUSTER_CONFIDENCE": 74.1,
  "TOP_CLUSTERS": [
    { "cluster": "Cluster2", "probability": 74.1 },
    { "cluster": "Cluster1", "probability": 12.3 },
    { "cluster": "Cluster5", "probability": 6.8 }
  ],
  "REJECTION_REASON": "", "FORMAT_WARNING": ""
}]
```

---

## 📍 Configuration Files

| File | Purpose |
|---|---|
| `Config_devicetype.json` | Paths to all Device Type model artefacts |
| `config_sectioncluster.json` | `model_folder` path + `unknown_threshold` (0.60) |
| `config.ini` | Runtime `MODEL_DIR`, `OUTPUT_DIR`, `UNKNOWN_THRESHOLD` |

## 📍 Environment Variables (Python — `predict_equipment.py`)

| Variable | Default | Purpose |
|---|---|---|
| `DEVICE_CLUSTER_CONFIG` | `./predict_equipment_folder/Config_devicetype.json` | Override config path |
| `MAX_BATCH_SIZE` | `5000` | Override max prediction batch size |
| `LOG_LEVEL` | `INFO` | Python logging level |

## 📍 Environment Variables (C# — `Program.cs`) 🆕

| Variable | Default | Purpose |
|---|---|---|
| `PYTHON_EXE` | `python` | Python executable used to launch the ML scripts |
| `SQL_SOURCE_TABLE` | `DummyTestingData` | Device data table (see §7) |
| `SQL_QUOTA_TABLE` | `dbo.PatternCluster` | Quota pattern table (see §3) |

---
## 📍 Filepath Reference

| Path | Description |
|---|---|
| `Prediction_service/DeviceCluster/Program.cs` | C# console orchestrator entry point |
| `Prediction_service/DeviceCluster/DeviceClusterConsoleApp.csproj` / `.slnx` | Console app project + .NET 10 solution file |
| `Prediction_service/DeviceCluster/predict_equipment.py` | Device Type inference script |
| `Prediction_service/DeviceCluster/predict_sectioncluster.py` | Section & Cluster inference script |
| `Prediction_service/DeviceCluster/sql/PatternCluster.sql` 🆕 | Creates & seeds `dbo.PatternCluster` (real `SECTION 2` + placeholder Sections 1, 3-8) |
| `Model/Model.csproj` | `Model.dll` class library project |
| `Model/Services/PythonSQL.cs` | SQL Server → JSON retrieval |
| `Model/Services/PythonClient.cs` | Python subprocess invocation wrapper |
| `Model/ModelResult/ClusterCandidate.cs` 🆕 | `{Cluster, Probability}` shape for `TOP_CLUSTERS` |
| `Logic/Logic.csproj` | `Logic.dll` class library project — `ProjectReference` to `Model.csproj` |
| `Logic/DevicePipeline.cs` 🆕 | Single-entry-point pipeline orchestration |
| `Logic/DevicePipelineCallbacks.cs` / `DevicePipelineResult.cs` 🆕 | Progress hooks / bundled output for `DevicePipeline.RunAsync` |
| `Logic/QuotaCatalog.cs` 🆕 | Loads quota patterns from `dbo.PatternCluster` |
| `Logic/ClusterQuotaAllocator.cs` | Quota allocation algorithm (Stages 1–3) |
| `Logic/LogicAssignment.cs` | Floating-pool split, `dbo.DeviceReviewQueue` upsert, cluster grouping, `dbo.OutputDeviceAssignment` upsert (both via SQL `MERGE`), manual correction |
| `Prediction_service/DeviceCluster_Prediction/model_config/` | All `.pkl` model files |
| `Prediction_service/DeviceType_Prediction/Config_devicetype.json` | Device Type config |
| `Prediction_service/DeviceCluster_Prediction/config_sectioncluster.json` | Section/Cluster config |
| `TestDevice/<project>.json` | Input device IDs per project (now legacy — SQL is the live source) |
| `1.training_model/Section XGB Model - Model Training.ipynb` | Training notebook |
| `data/{ProjectCode}_devices.json` | Raw dump of the SQL source table, named per project (redundant local copy of SQL data, kept for quick inspection) |
| `Prediction_service/DeviceCluster/sql/DeviceReviewQueue.sql` | Creates `dbo.DeviceReviewQueue` — replaces `floating_deviceid.json` / `unallocated_device_ids.json`, one table with `Category` (`UnknownPrediction` / `Unallocated`) and `Status` (`pending` / `assigned`) columns, unique on `(DeviceId, ProjectCode)` |
| `Prediction_service/DeviceCluster/sql/OutputDeviceAssignment.sql` 🆕 | Creates `dbo.OutputDeviceAssignment` — persists successfully assigned devices (previously only returned in-memory to the caller), includes `IsBackfill`/`OriginalCluster` diagnostics, unique on `(DeviceId, ProjectCode)` |
| `manual_assign_sectioncluster.json` | Queued Section/Cluster corrections pending the next XGBoost retrain cycle |

---

## 📍 Deployment Notes

- Install **Python 3.13** and set `PYTHON_EXE` if it's not on PATH.
- Ensure all model (`.pkl`) files are available in `DeviceCluster_Prediction/model_config/`.
- Device input is retrieved from SQL (`XenCreator` → `SQL_SOURCE_TABLE`, default `DummyTestingData`); JSON input is for legacy/testing only.
- Quota patterns are retrieved from SQL (`SQL_QUOTA_TABLE`, default `dbo.PatternCluster`) — run `Prediction_service/DeviceCluster/sql/PatternCluster.sql` once to create/seed the table. Only `SECTION 2` is real data; other sections are placeholders.
- Runs entirely locally; SQL Server is the only external dependency (plus a Python environment for the ML scripts).
- `Logic.dll` and `Model.dll` both target **.NET 10.0** (SDK-style projects). `Logic.dll` has a proper `ProjectReference` to `Model.dll`.
- `DeviceClusterConsoleApp` still references `Model.dll`/`Logic.dll` via `HintPath` to prebuilt binaries, not `ProjectReference` — remember to rebuild `Model.dll` → `Logic.dll` → the console app in that order after any library change, or switch this to `ProjectReference` too.
- For unattended/scheduled runs, use `DeviceClusterConsoleApp.exe <ProjectCode> --unattended` — no console interaction, no manual-correction prompt.
- For a UI or other non-console caller, reference `Model.dll` + `Logic.dll` directly and call `Logic.DevicePipeline.RunAsync(...)` — it has no console dependency of its own.
- Use `dotnet publish` for production deployment.

---
