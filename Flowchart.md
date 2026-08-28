# Flowchart — Device Identifier

## 1. Model Development

![Model development pipeline: source dataset, data preparation, train/validation/test split, feature engineering, and per-target model training](1.training_model/supplementary/flowchart-picture/model-development.png)

## 2. System Architecture

![System architecture of the Device Identifier solution](1.training_model/supplementary/flowchart-picture/system-architecture.png)

## 3. Full Workflow

![Full workflow from source dataset through prediction, quota-constrained cluster allocation, and output](1.training_model/supplementary/flowchart-picture/full-flowchart.png)

![Annotated review of the full workflow](1.training_model/supplementary/flowchart-picture/flowchart-by-claude.png)

### 3.1 Quota Allocation

Source workbook: `Flowchart Development.xlsx` —
[open in OneDrive](https://senergycommy-my.sharepoint.com/personal/sitisyaziyah_senergy_com_my/Documents/Flowchart%20Development.xlsx?web=1)

> Note: the link above points to a personal OneDrive folder and may not be accessible to other team members. Consider moving the workbook into this repository or a shared library.

**Stage 1 — Assign cluster to each device by highest prediction score**

![Stage 1: assign cluster to each device by their highest prediction score](1.training_model/supplementary/flowchart-picture/stage-1.png)

**Stage 2 — Find exceeded and vacant clusters**

![Stage 2: evaluate each device type as full, exceeded, or vacant, and move excess devices to the reassignment pool](1.training_model/supplementary/flowchart-picture/stage-2.png)

**Stage 3 — Reassignment pool**

![Stage 3: device-centric reassignment, where each device tries its ranked cluster candidates by model percentage, highest-scoring device first](1.training_model/supplementary/flowchart-picture/stage-3.png)
