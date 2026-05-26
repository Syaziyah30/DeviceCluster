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
* Inference script: `Prediction_service/DeviceCluster/predict_sectioncluster.py`
* Config: `Prediction_service/DeviceCluster_Prediction/config_sectioncluster.json`

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
