# DeviceCluster — Reference Guide

| | |
|---|---|
| **Full pipeline** | Device Type → Section → Cluster → Logic Placement (cascading) |
| **Runtime** | C# reads SQL DB → saves as JSON → Python ML scripts predict → Logic.dll places/groups → C# outputs to UI / Faiz's consumer |

---

## 📍 Current Status

| Component | Status |
|---|---|
| Device Type prediction        | ✅ Completed |
| Device Section prediction     | ✅ Completed |
| Device Cluster prediction     | ✅ Completed |
| C# service integration        | ✅ Completed |
| Incremental learning          | ✅ Completed |
| Development DLL library (`DeviceIdentifierLibrary`) | ✅ Completed |
| SQL integration (`PythonSQL.cs`)      | ✅ Completed |
| `Logic.dll` — cascading placement     | ✅ Completed |
| `Logic.dll` — similarity scoring & cluster grouping | ✅ Completed |
| Update Model (Type, Section Cluster)  | ❗Partially Completed, Section DONE, Section Cluster PENDING |
| Unknown device handling (`SuggestTopClusters`, JSON dump) | ✅ Completed |
| Testing DLL library | ❌ Pending |

---

# 🍀 Architecture Overview

```
             Data Source DB SQL (XenCreator → DummyInput table)
                    │  PythonSQL.cs (C#): dynamic SELECT DISTINCT
                    │  project detection, SQL → JSON
                    ▼
                Input JSON
      (project_code, customer_code, data_ids[])
                    │
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 1 — Device Type [predict_equipment.py]       │
│ Hybrid: exact match + SGD + TF-IDF cosine + dict  │
│ 22 equipment classes                              │
└───────────────────┬───────────────────────────────┘
                    │  DeviceTypeResult[]
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 2 — Section [predict_sectioncluster.py]      │
│ XGBoost (27+ engineered features)                 │
│ OOD KNN penalty applied                           │
└───────────────────┬───────────────────────────────┘
                    │  PipelineResult[] (with PREDICTED_SECTION)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 3 — Cluster [predict_sectioncluster.py]      │
│ XGBoost chained on Predicted Section               │
│ Confidence penalised by Section confidence         │
└───────────────────┬───────────────────────────────┘
                    │  PipelineResult[] (with PREDICTED_CLUSTER)
                    ▼
┌───────────────────────────────────────────────────┐
│ Step 4 — Logic Placement [Logic.dll, C#]          │
│ Split known/unknown devices                       │
│ Cascading placement + numeric similarity scoring  │
│ Cluster grouping → ClusterGroup / ScoredDevice     │
│ Unknown devices → JSON dump + SuggestTopClusters   │
└───────────────────┬───────────────────────────────┘
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
5. Dictionary (initial_map) match?     → dict label, confidence = 0.75
6. None of the above                   → UNKNOWN, confidence = max(composite, sgd)
```

## 📍 Configuration Thresholds

| Constant | Value | Purpose |
|---|---|---|
| `SGD_STRONG_THRESHOLD` | `0.60` | Minimum SGD confidence to accept SGD label |
| `COSINE_THRESHOLD` | `0.60` | Minimum composite score to accept NN/cosine label |
| `INITIAL_DICT_CONF` | `0.75` | Fixed confidence assigned for dictionary-only match |
| `ALPHA_PREFIX_WEIGHT` | `0.65` (from `composite_config`) | Prefix weight in composite formula |
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

➡️ Prefix from `initial_map` accepted **only if** the remainder after the prefix is:
- **Empty** — exact match (e.g. `CR` matches key `CR`)
- **All digits** — numeric suffix (e.g. `CR1234` matches key `CR`)

➡️ Rejected if remainder contains any letters (`CR123ABC` does **not** match `CR`).
➡️ Special case: inputs starting with `SP` also probe the stripped version (`probe[2:]`).

### Batch prefix counting — `matches_prefix_strict()`

Added to correctly count how many devices in a **batch** share a given prefix, since naive substring counting over-counted devices whose remainder contained letters.

- Applies the same "remainder must be digits-only" rule as single-device matching, but across an entire batch of `data_ids` at once.
- Used wherever the pipeline needs a prefix-share count (e.g. deciding dictionary confidence, grouping candidates) rather than a single match/no-match check.
- **TODO:** confirm exact call sites (`predict_equipment.py` function names/line refs) once finalized, so this section can point directly to them.

## 📍 Output Columns (Device Type)

| Column | Description |
|---|---|
| `customer` | Resolved customer code |
| `data_id` | Cleaned display ID |
| `manual_check` | Empty placeholder for UI review flag |
| `data_type` | Predicted equipment type |
| `confidence` | Final confidence (0.0–1.0) |
| `sgd_conf` | Raw SGD confidence (may differ from final) |
| `reason` | Internal decision reason |

## 📍 `reason` Values

| `reason` | Trigger |
|---|---|
| `all_letters` | Input is purely alphabetic — hard blocked |
| `exact_match` | Found exactly similar in reference set |
| `sgd_strong` | SGD confidence >= 0.60 |
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

| `action` | Description | Persists to disk? |
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

Updates live via `partial_fit`, flushed to disk every 50 rows.
`user_manual_assign` saves immediately · `import_equipment` does not.

⚠️ **Known gotcha:** modifying `initial_map.pkl` directly can shift the SGD label space and cause a `partial_fit` class-mismatch error, because SGD expects a fixed set of classes seen at first fit. This is why the **lightweight correction path** (see Manual Correction Flow below) updates `initial_map.pkl` only and deliberately avoids touching the SGD model.

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
- **Stage 2:** Predict Cluster using original features **plus** Predicted Section

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

| Condition | Behaviour |
|---|---|
| Unseen CUSTOMER or missing DEVICE_ID | Returns `REJECTION_REASON`, no prediction |
| Numeric field width outside training distribution | Confidence penalised by KNN scorer, returns `FORMAT_WARNING` |

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

---

# 4️⃣ Logic Placement (`Logic.dll`, C#)

Separate class library consumed by `Program.cs` after the Python pipeline returns `PipelineResult[]`. Handles cascading device placement, numeric similarity scoring, cluster grouping, and unknown device routing.

## 📍 Purpose

- Takes ML-predicted Section/Cluster results and resolves them into final placements using cascading logic (i.e. falling back through progressively looser matching criteria).
- Separates "known" devices (confidently placed) from "unknown" devices (routed to manual review / suggestion flow).
- Provides the final `DeviceResult` objects consumed by the C# UI and by Faiz's downstream integration.

## 📍 Core Models

| Model | Represents |
|---|---|
| `DeviceResult` | Final per-device placement result — device id, resolved section/cluster, confidence, placement source |
| `ClusterGroup` | A group of devices placed together under one cluster, used for table/UI display |
| `ScoredDevice` | A device paired with its numeric similarity score against a candidate cluster/group |
| `UnknownDumpEntry` | Record shape for devices that couldn't be confidently placed — dumped to JSON for review |

> **TODO:** fill in exact field lists for each model (property names/types) once the classes stabilize — useful for Faiz's integration reference.

## 📍 Numeric Similarity Scoring

Cascading placement uses numeric similarity between a device's ID structure (digit patterns, counts, leading zeros, etc. — see feature notes from the original XGBoost feature design) and existing cluster members, to decide whether an ambiguous device can be folded into an existing `ClusterGroup`.

> **TODO:** document the actual scoring formula/weights once finalized (mirrors the numeric convention features used in model training: `count_num_digit`, `leading_zero_count`, etc.).

## 📍 Unknown Device Handling

- Devices that fail confident placement are collected as `UnknownDumpEntry` records and dumped to JSON for manual review.
- `SuggestTopClusters` — proposes the top-N candidate clusters for an unknown device, likely based on the same numeric similarity scoring used for cascading placement.

> **TODO:** document `SuggestTopClusters` signature (inputs/outputs, how N is chosen, tie-breaking rules) once the method signature is stable.

---

# 5️⃣ Manual Correction Flow (C# ↔ Python)

Spans both the C# (`Logic.dll`, `Program.cs`) and Python (`predict_equipment.py`) layers, and has two independent parts that can fire separately:

| Trigger | What runs | Persists? |
|---|---|---|
| Any correction submitted | **Logic placement** re-runs in `Logic.dll` to re-place the device | Depends on placement logic |
| `correctType` is non-empty | **Python type correction** also fires (`user_manual_assign` style flow) | ✅ Yes — via lightweight path below |

## 📍 Lightweight Correction Path

When a manual type correction is submitted:

- Updates `initial_map.pkl` (the prefix → equipment type dictionary) directly.
- **Does not** call `partial_fit` on the SGD model.
- This avoids the class-mismatch error described in the Incremental Learning Notes above, where `initial_map.pkl` changes previously caused `partial_fit` to choke on an altered class set.

> **TODO:** note if this means dictionary corrections and full SGD incremental learning (`user_manual_assign`) are now two distinct code paths — worth stating explicitly which one the UI's "correct" action triggers by default.

---

# 6️⃣ C# Orchestration (`Program.cs`)

Steps run in order after SQL retrieval and JSON input are ready:

| Step | Description |
|---|---|
| 1–3 | Call Python pipeline (Device Type → Section → Cluster), collect `PipelineResult[]` |
| 4 | Split devices into known vs. unknown based on pipeline confidence/results |
| 5 | Dump unknown devices to JSON (`UnknownDumpEntry[]`) |
| 6 | Build `ClusterGroup`s from known devices via `Logic.dll` |
| 7 | Print result tables (Unicode-safe console output) |
| 8 | Prompt for manual correction (see Manual Correction Flow) |
| 9 | Finalize/persist output for downstream consumption (Faiz's DLL integration) |

> **TODO:** confirm exact behavior of Step 9 (what gets persisted, in what format) once finalized.

---

# 7️⃣ SQL Integration (`PythonSQL.cs`)

Retrieves device data from SQL Server and converts it to the JSON input format the Python pipeline expects.

| Item | Value |
|---|---|
| Database | `XenCreator` |
| Table | `DummyInput` |
| Project detection | Dynamic `SELECT DISTINCT` query (replaces old hardcoded project list) |
| Output | JSON matching the `predict_equipment.py` input shape (`project_code`, `customer_code`, `data_ids[]`) |

**Registry (SQL connection string)**

| Key | Value |
|---|---|
| Hive | `HKEY_CURRENT_USER\Software\XenxibleIdentifier` |
| Field | `connectionstring` |

> **TODO:** document `PythonSQL.cs` method names/signatures and how `PythonClient.cs` invokes it in sequence with the Python subprocess calls.

---

# 🍀 C# Subprocess Protocol

C# spawns Python as a child process for each ML step via `System.Diagnostics.Process`.

**Paths (resolved at runtime, relative to executable)**

| Variable | Points to |
|---|---|
| `SCRIPT_TYPE` | `predict_equipment.py` |
| `SCRIPT_PIPELINE` | `predict_sectioncluster.py` |
| `PROJECT_JSON` | `DeviceCluster/Prediction_service/TestDevice/A1825.json` |
| `SQL_OUTPUT_JSON` | `DeviceCluster/Prediction_service/data/devices.json` |

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

> **TODO:** add a Step 4 entry here showing what `Program.cs` passes into `Logic.dll` and what it gets back (`DeviceResult[]` / `UnknownDumpEntry[]` shape), once the interface is stable — mirrors the Step 1–3 examples above.

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
| `TestDevice/<project>.json` | Input device IDs per project |
| `bin/Debug/net10.0/DeviceCluster.exe` | Debug build executable |
| `1.training_model/Section XGB Model - Model Training.ipynb` | Training notebook |

> **TODO:** confirm the actual `Logic.csproj` path — placeholder above assumes it sits under `DeviceCluster/Logic/`; update once the real project structure is checked.

---

## 📍 Deployment Notes

- Python 3.13 must be installed at the path hardcoded in `Program.cs`.
- All `.pkl` model files must be present in `DeviceCluster_Prediction/model_config/`.
- Input devices read from `TestDevice/<project>.json`.
- No network calls — fully local inference (SQL retrieval is the only external dependency).
- `Logic.dll`'s `.csproj` was manually converted to SDK-style, targeting `net10.0` — keep this in mind when referencing it from consumer projects (e.g. Faiz's integration) to avoid legacy-style project reference issues.
- For production: use `dotnet publish` instead of debug build.

---

## 📍 Open TODOs Before This Doc Is Fully Synced

- [ ] Confirm exact field lists for `DeviceResult`, `ClusterGroup`, `ScoredDevice`, `UnknownDumpEntry`
- [ ] Document `SuggestTopClusters` signature and selection logic
- [ ] Document numeric similarity scoring formula used in cascading placement
- [ ] Confirm `Logic.csproj` real path and add to filepath table
- [ ] Add Step 4 (Logic.dll) request/response example to the Subprocess Protocol section
- [ ] Confirm whether dictionary-only correction and full `user_manual_assign` are exposed as separate UI actions or unified under one "correct" flow
- [ ] Document `PythonSQL.cs` / `PythonClient.cs` method signatures