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
* Label encoding:

  * CUSTOMER, PROJECT
  * PREFIX / SUFFIX
  * SECTION, CLUSTER

---

## Training & Evaluation

* Train / Val / Test split (stratified)
* XGBoost multi-class model

**Observation:**

* Section → stable
* Cluster → needs improvement

---

## Output

* `model_section.pkl`
* `model_cluster.pkl`
* `label_encoders.pkl`

---

## Status 🚧

* ✅ Pipeline & chaining implemented
* ⚠️ Model tuning ongoing
* ⚠️ Not production-ready

---

## Next Steps

* Improve accuracy (especially Cluster)
* Hyperparameter tuning
* Integrate with C# service

---

## Reference

Training script:


---

## Summary

A **hierarchical XGBoost model**:

* Section → Cluster dependency
* Strong feature engineering
* Still under optimization

---
