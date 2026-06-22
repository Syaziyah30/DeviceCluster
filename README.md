# DeviceIdentifierLibrary — Reference Guide

| | |
|---|---|
| **Full pipeline ML** | Device Type → Section → Cluster |
| **Runtime**           | C# reads SQL DB → saves as JSON → Python ML scripts process → output to UI  |


---
## 📍 Current Status

| Component                                           | Status |
|---                                                  |---|
| Device Type prediction                              | ✅ Completed |
| Device Section prediction                           | ✅ Completed |
| Device Cluster prediction                           | ✅ Completed |
| C# service integration                              | ✅ Complete |
| Incremental learning                                | ✅ Completed |
| Development DLL library                             | ✅ Completed |
| Logic Script                                        | ❗ In development draft |
| Testing DLL Library                                 | ❌ Pending |


---
# 🍀 Architecture Overview

```
             Data Source DB SQL
                    │ Using C# (SQL -> Json)
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
│ XGBoost chained on Predicted Section              │
│ Confidence penalised by Section confidence        │
└───────────────────────────────────────────────────┘
```

---

# 1️⃣ Device Type (`predict_equipment.py`)

## 📍 Model Approach : SGD Classifier

Hybrid classifier combining four sources, resolved in strict priority order:

```
1. All-letters                         → UNKNOWN 
2. Exact match from training dataset   → label from reference_df,  confidence = 1.0
3. SGD confidence >= 0.60              → SGD label
4. Composite similarity >= 0.60        → Nearest-neighbour cosine label
5. Dictionary (initial_map) match?     → dict label, confidence = 0.75
6. None of the above                   → UNKNOWN, confidence = max(composite, sgd)
```

## 📍 Configuration Thresholds

| Constant | Value | Purpose |
|---                      |---      |---|
| `SGD_STRONG_THRESHOLD`  | `0.60` | Minimum SGD confidence to accept SGD label |
| `COSINE_THRESHOLD`      | `0.60` | Minimum composite score to accept NN/cosine label |
| `INITIAL_DICT_CONF`     | `0.75` | Fixed confidence assigned for dictionary-only match |
| `ALPHA_PREFIX_WEIGHT`   | `0.65` (from `composite_config`) | Prefix weight in composite formula |
| `top_k_default`         | `10` (from `composite_config`) | Nearest neighbours retrieved per query |

📍 **Formula Composite similarity:**
```
composite_score = (0.65 × prefix_score) + (0.35 × cosine_similarity)
```

## 📍Batch & Buffer Settings

| Constant | Value | Purpose |
|---|---|---|
| `MAX_BATCH_SIZE` | `5000` (env: `MAX_BATCH_SIZE`) | Max `data_ids` per call |
| `BATCH_ADD_SIZE` | `50`                           | Incremental learning flush buffer |
| `ref_epoch_rebuild` | `50`                        | NN re-fit trigger (rows added) |

> Flush happens when `len(PENDING_NEW_ROWS) >= flush_batch_size`.

## 📍 Dictionary / Prefix Matching Rules

➡️ Prefix from `initial_map` accepted **only if** remainder after prefix is:
- **Empty** — exact match (e.g. `CR` matches key `CR`)
- **All digits** — numeric suffix (e.g. `CR1234` matches key `CR`)

➡️ Rejected if remainder contains any letters (`CR123ABC` does **not** match `CR`).  
➡️ Special case: inputs starting with `SP` also probe the stripped version (`probe[2:]`).

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
| `exact_match` | Found verbatim in reference set |
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

📍 **JSON payloads:**

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

— updates live via `partial_fit`, flushed to disk every 50 rows.  
`user_manual_assign` saves immediately · `import_equipment` does not.


## 📍 Features

| Variable                          | Role |
|---                                |---|
| `sgd_model`                       | Main classifier |
| `tfidf_sgd` / `tfidf_similarity`  | Text vectorisers |
| `nn`                              | Nearest neighbour (k=10, cosine) |
| `initial_map`                     | Prefix → equipment type |
| `reference_df` / `ref_id_set`     | Ground-truth reference |


---

# 🍀 Step 2️⃣ & 3️⃣ — Section & Cluster (`predict_sectioncluster.py`)

## 📍 Model Approach = Chained XGBoost classification

```
Features → [XGBoost] → Predicted Section → Features + Section → [XGBoost] → Cluster
```

- **Stage 1:** Predict Section
- **Stage 2:** Predict Cluster using original features **plus** Predicted Section

## 📍 Thresholds & Penalties

| Parameter           | Value                     | Purpose  |
|---                  |---                        |---|
| `unknown_threshold` | `0.60`                    |  Section confidence gate for chaining |
| OOD penalty formula | `adjusted = raw / (1 + max(0, dist - threshold) / threshold)` | KNN distance penalty on raw confidence |

**Confidence chaining rule:**  
If Section confidence < `0.60`, Cluster confidence is multiplied by Section confidence (joint probability).

```
cluster_conf_final = cluster_raw_conf × section_conf   (when section_conf < 0.60)
```

## 📍Input Validation

| Condition                                         | Behaviour |
|---                                                |---|
|Unseen CUSTOMER or missing DEVICE_ID               | Returns `REJECTION_REASON`, no prediction |
|Numeric field width outside training distribution  | Confidence penalised by KNN scorer, returns `FORMAT_WARNING` |

## 📍Output Columns (Section & Cluster)

| Column | Description |
|---|---|
| `PREDICTED_SECTION` | Predicted section label or `UNKNOWN` |
| `SECTION_CONFIDENCE` | Adjusted confidence (0–100%) |
| `PREDICTED_CLUSTER` | Predicted cluster label or `UNKNOWN` |
| `CLUSTER_CONFIDENCE` | Adjusted confidence, penalised if section is weak |
| `REJECTION_REASON` | Set if device is hard-blocked |
| `FORMAT_WARNING` | Set if numeric field width is outside training distribution |

# 🍀Model Artefacts

| File | Contents |
|---|---|
| `model_section.pkl` | Trained XGBoost section model |
| `model_cluster.pkl` | Trained XGBoost cluster model |
| `pipeline_config.pkl` | All label encoders, feature lists, OOD scaler/KNN, known customers, numeric width stats |

---

# 🍀C# Subprocess Protocol

C# spawns Python as a child process for each step via `System.Diagnostics.Process`.

**Paths (resolved at runtime, relative to executable)**

| Variable          | Points to                     |
|---                |---                            |
| `SCRIPT_TYPE`     | `predict_equipment.py`        |
| `SCRIPT_PIPELINE` | `predict_sectioncluster.py`   |
| `PROJECT_JSON`    | `DeviceCluster/Prediction_service/TestDevice/A1825.json`       |
| `SQL_OUTPUT_JSON` | `DeviceCluster/Prediction_service/data/devices.json`           |

**Registry (SQL connection string)**

| Key   | Value                                             |
|---    |---                                                |
| Hive  | `HKEY_CURRENT_USER\Software\XenxibleIdentifier`   |
| Field | `connectionstring`                                |

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

---

## 📍 Configuration Files

| File | Purpose |
|---|---|
| `Config_devicetype.json` | Paths to all Device Type model artefacts |
| `config_sectioncluster.json` | `model_folder` path + `unknown_threshold` (0.60) |
| `config.ini` | Runtime `MODEL_DIR`, `OUTPUT_DIR`, `UNKNOWN_THRESHOLD` |

---

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
| `Prediction_service/DeviceCluster_Prediction/model_config/` | All `.pkl` model files |
| `Prediction_service/DeviceType_Prediction/Config_devicetype.json` | Device Type config |
| `Prediction_service/DeviceCluster_Prediction/config_sectioncluster.json` | Section/Cluster config |
| `TestDevice/<project>.json` | Input device IDs per project |
| `bin/Debug/net10.0/DeviceCluster.exe` | Debug build executable |
| `1.training_model/Section XGB Model - Model Training.ipynb` | Training notebook |

---

## 📍 Deployment Notes

- Python 3.13 must be installed at the path hardcoded in `Program.cs`
- All `.pkl` model files must be present in `DeviceCluster_Prediction/model_config/`
- Input devices read from `TestDevice/<project>.json`
- No network calls — fully local inference
- For production: use `dotnet publish` instead of debug build

---

