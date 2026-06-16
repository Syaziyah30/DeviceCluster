# Device Identifier
## 2 Part
1️⃣ Device Type ➡️ SGD Classifer
2️⃣ Device Section & Cluster ➡️ XG Boost

# Section & Cluster Classification (XGBoost Chained Model)

## Introduction

This project classifies equipment into Section and Cluster.

It applies a ML (chained approach), where Section prediction improves Cluster prediction.

⚠️ Currently in **development (training phase)**.

---
## Model Approach
* **Stage 1:** Predict Section
* **Stage 2:** Predict Cluster using:

  * Original features
  * **Predicted Section**

```
Features → Section → Predicted Section → Cluster
```

---

## Data Processing

* Combine multiple Excel files
* Label encoding (via `SafeLabelEncoder` with `__UNKNOWN__` sentinel):

  * `le_customer` — CUSTOMER
  * `le_suffix_letter` — trailing letter suffix of device ID
  * `le_suffix_last` — last character of suffix
  * `le_shape` — numeric+letter pattern shape (e.g. `DLLD`)
  * `le_section` — SECTION target
  * `le_cluster` — CLUSTER target

---

## Training & Evaluation

* Train / Val / Test split (stratified)
* XGBoost multi-class model
* **27+ engineered features** per device ID: numeric block, digit/letter counts, suffix shape, leading zeros, suffix analysis
* **OOD detection**: KNN-based distance penalty applied to raw confidence scores
  * Penalty formula: `adjusted = raw_conf / (1 + max(0, dist - threshold) / threshold)`
* **Confidence chaining**: if Section confidence < `unknown_threshold` (0.60), Cluster confidence is multiplied by Section confidence (joint probability)
* **Hard rejection**: unseen CUSTOMER or missing DEVICE_ID → returns `REJECTION_REASON`
* **Soft warning**: numeric field width outside training distribution → confidence penalised by KNN scorer, returns `FORMAT_WARNING`

**Observation:**

* Section → stable
* Cluster → needs improvement

---

## Output

**Model artefacts (production):**

* `model_section.pkl`
* `model_cluster.pkl`
* `pipeline_config.pkl` — contains all label encoders, feature lists, OOD scaler/KNN, known customers, numeric width stats

**Runtime output columns (per device):**

| Column | Description |
|---|---|
| `PREDICTED_SECTION` | Predicted section label or `UNKNOWN` |
| `SECTION_CONFIDENCE` | Adjusted confidence (0–100%) |
| `PREDICTED_CLUSTER` | Predicted cluster label or `UNKNOWN` |
| `CLUSTER_CONFIDENCE` | Adjusted confidence (0–100%), penalised if section is weak |
| `REJECTION_REASON` | Set if device is hard-blocked (unseen customer, missing ID) |
| `FORMAT_WARNING` | Set if numeric field width is outside training distribution |

---

## C# Service Execution

The ML pipeline is orchestrated by a **.NET 10.0 console application** that manages subprocess communication with Python.

**Solution:** `Prediction_service/DeviceCluster/DeviceCluster.slnx`
**Entry point:** `Prediction_service/DeviceCluster/Program.cs`
**Target framework:** `.NET 10.0` (console executable)

---

### Execution Flow

The service runs three sequential steps on a batch of device IDs read from a JSON input file:

```
Input JSON (project_code, customer_code, data_ids[])
        │
        ▼
┌───────────────────────────┐
│ Step 1 — Device Type      │  predict_equipment.py
│ Hybrid: exact match +     │  22 equipment classes
│ SGD + TF-IDF + dict       │
└───────────┬───────────────┘
            │  DeviceTypeResult[]
            ▼
┌───────────────────────────┐
│ Step 2 — Section          │  predict_sectioncluster.py
│ XGBoost (27+ features)    │  OOD penalty applied
│ + OOD KNN penalty         │
└───────────┬───────────────┘
            │  PipelineResult[] (with PREDICTED_SECTION)
            ▼
┌───────────────────────────┐
│ Step 3 — Cluster          │  Same script result
│ XGBoost chained on        │  Confidence chained on
│ predicted Section         │  Section confidence
└───────────────────────────┘
```

---

### Python Subprocess Protocol

C# spawns Python as a child process for each step using `System.Diagnostics.Process`:

| Parameter | Value |
|---|---|
| Python executable | `C:\Users\sitisyaziyah\AppData\Local\Programs\Python\Python313\python.exe` |
| Launch flag | `-u` (unbuffered stdout) |
| Shell execute | `false` (direct process, no cmd.exe) |
| Window | `CreateNoWindow = true` |
| Encoding | UTF-8 (stdout + stderr) |
| Input method | Write JSON to `stdin`, then close to signal EOF |

**Request/response per step:**

**Step 1 — Device Type**
```jsonc
// stdin → predict_equipment.py
{ "project_code": "A1825", "customer_code": "Lipico", "data_ids": ["CR1234", "PU001"] }

// stdout ← predict_equipment.py
[{ "data_id": "CR1234", "data_type": "COOLER", "confidence": 0.92, "reason": "sgd_strong" }, ...]
```

**Step 2 & 3 — Section + Cluster**
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

### Configuration

| File | Purpose |
|---|---|
| `config_sectioncluster.json` | `model_folder` path + `unknown_threshold` (0.60) |
| `Config_devicetype.json` | Paths to all Device Type model artefacts |
| `config.ini` | Runtime `MODEL_DIR`, `OUTPUT_DIR`, `UNKNOWN_THRESHOLD` |

---

### Deployment Notes

* Python 3.13 must be installed at the path hardcoded in `Program.cs`
* All `.pkl` model files must be present in `DeviceCluster_Prediction/model_config/`
* Input devices are read from `TestDevice/<project>.json` (`project_code`, `customer_code`, `data_ids`)
* The compiled executable is at `bin/Debug/net10.0/DeviceCluster.exe`; for release build use `dotnet publish`
* No network calls — fully local inference

---

## Status 🚧

* ✅ Pipeline & chaining implemented
* ✅ OOD detection & confidence penalty implemented
* ✅ Input validation (customer gate, format warnings)
* ✅ C# service integration complete (stdin/stdout JSON via `predict_sectioncluster.py`)
* ✅ Model tuning ongoing
* ⚠️ Cluster accuracy still under optimization — not production-ready

---

## Next Steps

* Improve Cluster accuracy
* Hyperparameter tuning
* Expand training data (more customers / projects)

---

## Reference

* Training notebook: `1.training_model/Section XGB Model - Model Training.ipynb`
* Inference script (Section/Cluster): `Prediction_service/DeviceCluster/predict_sectioncluster.py`
* Inference script (Device Type): `Prediction_service/DeviceCluster/predict_equipment.py`
* C# orchestrator: `Prediction_service/DeviceCluster/Program.cs`
* C# solution: `Prediction_service/DeviceCluster/DeviceCluster.slnx`
* Section/Cluster config: `Prediction_service/DeviceCluster_Prediction/config_sectioncluster.json`
* Device Type config: `Prediction_service/DeviceType_Prediction/Config_devicetype.json`

---

## Summary

A **hierarchical XGBoost model**:

* Section → Cluster dependency (chained prediction)
* 27+ engineered features per device ID
* OOD-aware confidence scoring (KNN distance penalty)
* Joint-probability confidence chaining (Section confidence gates Cluster)
* Input validation with hard rejection and soft format warnings
* Integrated with C# service via subprocess JSON interface
* Cluster accuracy still under optimization

---
