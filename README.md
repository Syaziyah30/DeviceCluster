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
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  PipelineResult[] (with PREDICTED_CLUSTER)
                    ▼
┌─────────────────────────────────────────────────────┐
│ 🆕 Step 3.5 — Quota Allocation [Logic.dll, C#]      │
│ ClusterQuotaAllocator two-pass:                     │
│   Pass 1 — ranked assignment against quota          │
│   Pass 2 — floating-pool backfill vs InitialDeficits│
│ MinCascadeConfidence floor (60%) blocks bad forces  │
│ Floating pool split by cause:                       │
│   any UNKNOWN field   → floating_deviceid.json      │
│   known, no quota room → unallocated_device_ids.json│
└───────────────────┬─────────────────────────────────┘
                    │
                    │  AllocationResult (Assigned / Floating /
                    │  InitialDeficits / VacancyReport)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 4 — Logic Placement [Logic.dll, C#]          │
│ Cluster grouping → ClusterGroup / ScoredDevice    │
│ (Assigned devices are already known by construction│
│  — no separate known/unknown split needed here)   │
│ Unallocated devices → JSON dump + SuggestTopClusters│
└───────────────────┬───────────────────────────────┘
                    │
                    │
                    │  DeviceResult[] / FloatingDumpEntry[] / UnallocatedDumpEntry[]
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
| `REJECTION_REASON` | Set if device is hard-blocked |
| `FORMAT_WARNING` | Set if numeric field width is outside training distribution |

## 📍 Model Artefacts

| File | Contents |
|---|---|
| `model_section.pkl` | Trained XGBoost section model |
| `model_cluster.pkl` | Trained XGBoost cluster model |
| `pipeline_config.pkl` | All label encoders, feature lists, OOD scaler/KNN, known customers, numeric width stats |

> Note: the `export_raw_csv_path` field previously on `PipelinePredictRequest` (a debug-only raw `predict_proba` dump per device) has been removed from the C# request — `Program.cs` no longer sets it. `predict_sectioncluster.py` still supports it (it just always receives `None` now), and the separate `export_cluster_csv` action is untouched.

---

# 🆕 3. Quota Allocation (`ClusterQuotaAllocator`, `Logic.dll`)

Allocates devices to clusters based on predefined quotas before cascading placement.

### 📍 Purpose
- Ensures each **Section–Cluster–DeviceType** meets its target quota.
- Uses model predictions while respecting capacity constraints.

### 📍 Allocation Flow
**Pass 1 – Initial Allocation**
- Assign devices to their highest-confidence cluster until the quota is reached.

**Pass 2 – Vacancy Backfill**
- Redistribute remaining devices to clusters with available capacity.

### 📍 Confidence Threshold
- Reassigned devices must meet the minimum confidence threshold.
- Low-confidence predictions are routed to the **UNKNOWN** list instead of being force-assigned.

### 📍 UNKNOWN Handling
- UNKNOWN devices are separated **before** quota allocation.
- Only fully identified devices participate in quota allocation and backfill.

### 📍 Data Models

| Model | Description |
|--------|-------------|
| `DevicePrediction` | Prediction result used for allocation |
| `ClusterQuota` | Target quota for each Section–Cluster–DeviceType |
| `AllocationResult` | Assigned devices, unassigned devices, and vacancy summary |
| `AllocatedDevice` | Successfully allocated device |
| `VacancyReportEntry` | Remaining quota after allocation |

---

# 4️⃣ Logic Placement (`Logic.dll`, C#)


Processes the prediction results after quota allocation and determines the final device placement.

### 📍 Responsibilities
- Apply cascading placement logic.
- Group devices into clusters.
- Route low-confidence devices to the Unknown list.
- Generate the final placement results for the C# application.

### 📍 Components

| Namespace | Purpose |
|-----------|---------|
| `Logic` | Quota allocation and core allocation models |
| `Logic.LogicAssignUser` | Device placement, cluster grouping, and Unknown handling |
| `Logic.Models` | Shared data models for prediction and placement |
| `Logic.SimilarityScore` | Numeric similarity used for manual device reassignment |

### 📍 Core Models

| Model | Description |
|-------|-------------|
| `DeviceResult` | Final device placement result |
| `ClusterGroup` | Devices grouped under the same cluster |
| `ScoredDevice` | Device with similarity score |
| `FloatingDumpEntry` | Floating device with an UNKNOWN prediction, needs re-prediction |
| `UnallocatedDumpEntry` | Floating device with a known prediction but no quota room, needs manual placement |

### 📍 Processing Flow
1. Split the quota allocator's floating pool by cause (`LogicAssignment.SplitFloatingPool`).
2. Group assigned devices by cluster.
3. Apply numeric similarity for manual reassignment.
4. Export the two floating populations to `floating_deviceid.json` and `unallocated_device_ids.json`.

### 📍 Floating Device Handling
- Devices with an UNKNOWN type/section/cluster are routed to `floating_deviceid.json`.
- Devices with a known prediction but no quota vacancy are routed to `unallocated_device_ids.json`.
- Cluster suggestions are generated using the model-based Top-3 prediction service.
---

# 5️⃣ Manual Correction Flow (C# ↔ Python)

Processes user corrections for device predictions across the C# and Python layers.

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

Steps run in order after SQL retrieval and JSON input are ready. 🆕 Updated to reflect the retired known/unknown split and the new floating-pool routing:

| Step | Description |
|---|---|
| 1–3 | Call Python pipeline (Device Type → Section → Cluster), collect `PipelineResult[]` |
| 🆕 3.5 | `ClusterQuotaAllocator.Allocate(...)` — two-pass quota fitting, producing `AllocationResult` (`LogicAssignment` instantiated before this step) |
| 🆕 3.5 (floating) | `LogicAssignment.SplitFloatingPool` splits `AllocationResult.Floating` by cause, then `DumpFloating`/`DumpUnallocated` write `floating_deviceid.json` / `unallocated_device_ids.json` |
| 4 | Build `DeviceResult[]` from `AllocationResult.Assigned` — every entry is guaranteed known (it matched a real quota bucket), so no separate known/unknown split is needed |
| 5 | Build `ClusterGroup`s from those devices via `Logic.dll` |
| 6 | Print result tables (Unicode-safe console output, `PrintClusterTable`) |
| 7 | Print unallocated devices pending manual assignment; prompt for manual correction (see Manual Correction Flow) |

> Note: the previous Step 5/6 (`SplitKnownUnknown` / `DumpUnknown` / `unknown_dump.json`) was retired — it only ever ran on already-`Assigned` devices, which can never be "unknown" by construction, so that path was permanently dead code. Its role is now covered by the floating-pool split above.

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
{ "records": [{ "device_id": "CR1234", "customer": "Lipico", "project": "A1825" }] }

// stdout ← predict_sectioncluster.py
[{
  "DEVICE_ID": "CR1234", "CUSTOMER": "Lipico", "PROJECT": "A1825",
  "PREDICTED_SECTION": "SectionA", "SECTION_CONFIDENCE": 87.4,
  "PREDICTED_CLUSTER": "Cluster2", "CLUSTER_CONFIDENCE": 74.1,
  "REJECTION_REASON": "", "FORMAT_WARNING": ""
}]
```

> **TODO:** add a Step 3.5/4 entry here showing what `Program.cs` passes into `ClusterQuotaAllocator` and `Logic.dll`, and what it gets back (`AllocationResult` / `DeviceResult[]` / `FloatingDumpEntry[]` / `UnallocatedDumpEntry[]` shape), once fully stable.

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
| 🆕 `data/{ProjectCode}_devices.json` | Raw dump of the SQL source table, named per project, written by `PythonSQL.QueryToJsonFileByProjectCodeAsync` (Step 1) |
| 🆕 `data/floating_deviceid.json` | Floating devices with an UNKNOWN type/section/cluster prediction (Step 3.5) |
| 🆕 `data/unallocated_device_ids.json` | Floating devices with a known prediction but no quota room — needs manual assignment (Step 3.5) |
| 🆕 `manual_assign_sectioncluster.json` | Queued Section/Cluster corrections pending the next XGBoost retrain cycle |

> **TODO:** confirm the actual `Logic.csproj` path — placeholder above assumes it sits under `DeviceCluster/Logic/`; update once the real project structure is checked.

---

## 📍 Deployment Notes


- Install **Python 3.13** and update the Python path in `Program.cs`.
- Ensure all model (`.pkl`) files are available in `DeviceCluster_Prediction/model_config/`.
- Device input is retrieved from SQL (`XenCreator` → `DummyInput`); JSON input is for legacy/testing only.
- Runs entirely locally; SQL is the only external dependency.
- `Logic.dll` targets **.NET 10.0** (SDK-style project).
- `DeviceClusterConsoleApp` currently uses **Assembly References** (known issue); **Project References** are recommended.
- Use `dotnet publish` for production deployment.

---
