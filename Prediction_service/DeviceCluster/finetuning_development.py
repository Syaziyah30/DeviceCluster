# file created on 17/8/2026 (Monday) by sitisyaziyah
# load functions

import os
import re
import sys
import json
import shutil
import logging
import traceback
from datetime import datetime
from pathlib import Path

import joblib
import scipy.sparse as sp
import pandas as pd


# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.getLevelName(os.environ.get("LOG_LEVEL", "INFO")),
    format="%(asctime)s [%(levelname)s] %(message)s",
    stream=sys.stderr
)
logger = logging.getLogger(__name__)


# =============================================================================
# MODEL FOLDER
# =============================================================================

MODEL_FOLDER = Path(
    r"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceCluster\predict_equipment_folder\model_config_devicetype"
)

if not MODEL_FOLDER.exists():
    raise FileNotFoundError(
        f"Model folder not found: {MODEL_FOLDER}"
    )

MAX_BATCH_SIZE = int(os.environ.get("MAX_BATCH_SIZE", "5000"))


###### FROM PREVIOUS SCRIPT "predict_equipment.py" (Modify soon)
# _script_dir  = Path(__file__).resolve().parent

# _config_path = Path(os.environ.get(
#     "DEVICE_CLUSTER_CONFIG",
#     _script_dir / "predict_equipment_folder" / "Config_devicetype.json"
# ))

# if not _config_path.exists():
#     raise FileNotFoundError(
#         f"Config not found at: {_config_path}\n"
#         f"Set DEVICE_CLUSTER_CONFIG environment variable to override."
#     )

# with open(_config_path, "r", encoding="utf-8") as f:
#     config = json.load(f)

# _config_dir       = _config_path.parent                               # .../predict_equipment_folder/
# JSON_MODEL_FOLDER = (_config_dir / config["model_folder"]).resolve()  # .../predict_equipment_folder/model_config_devicetype/
# MODEL_FOLDER      = JSON_MODEL_FOLDER                                  # same folder

# MAX_BATCH_SIZE = int(os.environ.get("MAX_BATCH_SIZE", "5000"))
###### END HERE


# =============================================================================
# FILE LOADERS
# =============================================================================

def load_pkl(filename):
    """Load a joblib/pickle (.pkl) file."""
    path = MODEL_FOLDER / filename

    if not path.exists():
        raise FileNotFoundError(f"File not found: {path}")

    return joblib.load(path)


def load_npz(filename):
    """Load a scipy sparse matrix saved with scipy.sparse.save_npz (.npz)."""
    path = MODEL_FOLDER / filename

    if not path.exists():
        raise FileNotFoundError(f"File not found: {path}")

    return sp.load_npz(path)


def load_json(filename):
    path = MODEL_FOLDER / filename

    if not path.exists():
        raise FileNotFoundError(f"File not found: {path}")

    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def load_json_or_default(filename, default):
    """Like load_json, but returns `default` instead of raising if the file is missing."""
    path = MODEL_FOLDER / filename
    if not path.exists():
        return default
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def atomic_write_json(obj, path: Path):
    """Write JSON atomically: write to a temp file, then os.replace over the target."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


# =============================================================================
# LOAD ALL MODEL FILES
# =============================================================================

class_index_map = load_json("class_index_map.json")
class_prefix_map = load_pkl("class_prefix_map.pkl")
customer_initial_map = load_pkl("customer_initial_map.pkl")
customer_project_map = load_pkl("customer_project_map.pkl")
customer_specific_map = load_pkl("customer_specific_map.pkl")
initial_map = load_pkl("initial_map.pkl")
master_df = load_pkl("master_df.pkl")
reference_df = load_pkl("reference_df.pkl")
frequency_equipment = load_json_or_default("frequency_equipment.json", {})

logger.info("Model folder: %s", MODEL_FOLDER)
logger.info("All model/configuration files loaded successfully.")
logger.info("initial_map.pkl loaded")
logger.info("master_df.pkl loaded")
logger.info("reference_df.pkl loaded")

current_time = datetime.now().strftime("%H:%M %d/%m/%Y")
print(f"Model loaded on {current_time}", file=sys.stderr)


# =============================================================================
# HELPER FUNCTIONS
# =============================================================================

def clean_device_id(device_id):
    """Standardize a raw device id: uppercase, trim, collapse separators to spaces."""
    if not isinstance(device_id, str):
        return ""
    s = device_id.strip().upper()
    s = s.replace("_", " ").replace("-", " ")
    s = re.sub(r"\s+", " ", s).strip()
    return s


def extract_prefix_id(device_id):
    """Extract the leading alphabetic prefix from a cleaned device id."""
    cleaned = clean_device_id(device_id)
    if not cleaned:
        return ""
    parts = [p for p in cleaned.split() if p != "SP"]
    if not parts:
        return ""
    joined = "".join(parts)
    match = re.match(r"^([A-Z]+)", joined)
    return match.group(1) if match else ""


def assign_device_type(device_id, mapping=initial_map):
    """Clean a new device id, extract its prefix, and look it up in initial_map.
    Returns "Unknown" if the prefix isn't recognized."""
    prefix = extract_prefix_id(device_id)
    if not prefix:
        return "Unknown"
    return mapping.get(prefix, "Unknown")


def assign_device_types(device_ids, mapping=initial_map):
    """Batch version: returns a DataFrame with device_id, prefix, device_type."""
    records = [
        {
            "device_id": device_id,
            "prefix": (prefix := extract_prefix_id(device_id)),
            "device_type": mapping.get(prefix, "Unknown") if prefix else "Unknown",
        }
        for device_id in device_ids
    ]
    return pd.DataFrame(records)


def normalize_probe(s):
    """Uppercase and strip to [A-Z0-9] only - used for strict prefix checks."""
    return re.sub(r"[^A-Z0-9]", "", str(s).upper())


def matches_prefix_strict(data_id, prefix):
    """
    Strict prefix match: data_id must start with `prefix` followed by digits
    only (or nothing else). Rejects suffix variants like 'SPD'.
    """
    candidate = normalize_probe(data_id)
    if not candidate.startswith(prefix):
        return False
    remainder = candidate[len(prefix):]
    return len(remainder) == 0 or remainder.isdigit()


# =============================================================================
# PREDICTION
# =============================================================================

INITIAL_DICT_CONF = 1.0  # dictionary matches count as fully confident (exact-match tier removed)


def resolve_customer(project_code=None, customer_code=None):
    """Resolve the effective customer the same way predict_equipment.py does."""
    if customer_code is None:
        return customer_project_map.get(project_code, "UNKNOWN")
    return customer_code


def predict_device_type(device_id, project_code=None, customer_code=None):
    """
    Predict a single device's type (ML and exact-match history lookup both
    dropped on purpose), in priority order:
      1. initial_map prefix dictionary match -> confidence 1.0
      2. Otherwise                            -> UNKNOWN, confidence 0.0

    Returns a dict shaped to match the C# DeviceTypeResult contract:
    customer, data_id, manual_check, data_type, confidence, reason.
    """
    customer = resolve_customer(project_code, customer_code)

    prefix = extract_prefix_id(device_id)
    if prefix and prefix in initial_map:
        return {
            "customer": customer,
            "data_id": device_id,
            "manual_check": "",
            "data_type": initial_map[prefix],
            "confidence": INITIAL_DICT_CONF,
            "reason": "initial_dict_match",
        }

    return {
        "customer": customer,
        "data_id": device_id,
        "manual_check": "",
        "data_type": "UNKNOWN",
        "confidence": 0.0,
        "reason": "no_confident_source",
    }


def predict_device_types(device_ids, project_code=None, customer_code=None):
    """Batch version of predict_device_type -> DataFrame matching DeviceTypeResult columns."""
    if isinstance(device_ids, str):
        device_ids = [device_ids]

    records = [
        predict_device_type(device_id, project_code=project_code, customer_code=customer_code)
        for device_id in device_ids
    ]
    return pd.DataFrame(
        records,
        columns=["customer", "data_id", "manual_check", "data_type", "confidence", "reason"]
    )


# =============================================================================
# MANUAL ASSIGNMENT FOR NEW / UNKNOWN DEVICES
# =============================================================================

# Log of every manual assignment made in this process
equipment_manual_assign = pd.DataFrame(
    columns=["data_id", "prefix", "equipment", "previous_equipment", "is_new_class", "count", "assigned_at"]
)


def update_frequency_json(prefix, equipment, count):
    """
    Maintain the in-memory frequency_equipment dict (written to disk by
    persist_light_state()):
        {"CR": {"equipment": "Agitator", "count": 12, "last_updated": "...", "source": "manual"}}
    """
    global frequency_equipment
    frequency_equipment[prefix] = {
        "equipment": equipment,
        "count": count,
        "last_updated": datetime.now().strftime("%Y-%m-%d"),
        "source": "manual",
    }
    logger.info("frequency_equipment updated: %s -> count=%d", prefix, count)


def manual_assign_equipment(data_id, equipment, project_code=None, customer=None, batch_results=None):
    """
    Manually assign an equipment type to a device id whose prefix came back
    UNKNOWN (or to correct an existing prefix mapping) - matches the payload
    Program.cs's correction flow actually sends: {"data_id": "KK101", "equipment": "Fan"}.

    Merges the assignment into every in-memory reference structure:
      - initial_map             : prefix -> equipment                (always updated)
      - class_prefix_map        : equipment -> representative prefix
                                   (only set the first time this equipment appears)
      - class_index_map         : equipment -> classifier class index
                                   (only assigned the first time this equipment appears)
      - reference_df/master_df  : appends a new row for this device id (feeds the strict prefix-count used by frequency_equipment)
      - frequency_equipment      : tracks how many known devices share this prefix

    Note: this only updates in-memory state. Call persist_light_state()
    afterwards to write the changes back to MODEL_FOLDER.

    Returns the logged record as a dict.
    """
    global equipment_manual_assign, reference_df, master_df

    prefix = extract_prefix_id(data_id)
    if not prefix:
        raise ValueError(f"Could not extract a prefix from data_id={data_id!r}")

    equipment = str(equipment).strip()
    if not equipment:
        raise ValueError("equipment must not be empty")

    previous_equipment = initial_map.get(prefix, "UNKNOWN")
    is_new_class = equipment not in class_index_map

    # 1) initial_map: always record/overwrite this prefix -> equipment
    # if same prefix overlap - the older deleted, the newer assignment takes precedence
    initial_map[prefix] = equipment

    # 2) class_prefix_map / class_index_map: only touch these when the
    #    equipment itself is brand-new to the classifier
    if is_new_class:
        next_idx = max(class_index_map.values(), default=-1) + 1
        class_index_map[equipment] = next_idx
        class_prefix_map.setdefault(equipment, prefix)
        logger.info("New equipment class registered: %s -> index %d (prefix %s)", equipment, next_idx, prefix)
    else:
        logger.info("Existing equipment class '%s' - initial_map updated with prefix '%s' only.", equipment, prefix)

    # 3) count devices sharing this prefix (strict: prefix + digits only)
    customer_value = customer or "UNKNOWN"
    if batch_results:
        count = sum(
            1 for row in batch_results
            if row.get("data_id") and matches_prefix_strict(row["data_id"], prefix)
        )
    else:
        count = int(reference_df["data_id"].astype(str).apply(
            lambda x: matches_prefix_strict(x, prefix)
        ).sum())

    update_frequency_json(prefix, equipment, count)

    # 4) register this device id into reference_df/master_df so future
    #    frequency counts (step 3, on repeat assignments to this prefix) see it
    if data_id not in reference_df["data_id"].values:
        new_row = pd.DataFrame(
            [[data_id, equipment, prefix, customer_value]],
            columns=["data_id", "data_type", "initial", "customer"],
        )
        reference_df = pd.concat([reference_df, new_row], ignore_index=True)
        master_df = pd.concat([master_df, new_row], ignore_index=True)

    record = {
        "data_id": data_id,
        "prefix": prefix,
        "equipment": equipment,
        "previous_equipment": previous_equipment,
        "is_new_class": is_new_class,
        "count": count,
        "assigned_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }

    equipment_manual_assign = pd.concat(
        [equipment_manual_assign, pd.DataFrame([record])],
        ignore_index=True
    )

    return record


# =============================================================================
# PERSISTENCE
# =============================================================================

def _backup_file(path: Path):
    """Copy an existing file to a timestamped .bak before it gets overwritten."""
    if not path.exists():
        return
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_path = path.with_name(f"{path.name}.{stamp}.bak")
    shutil.copy2(path, backup_path)
    logger.info("Backed up %s -> %s", path.name, backup_path.name)


def persist_light_state():
    """
    Write the in-memory reference structures back to MODEL_FOLDER, matching
    predict_equipment.py's persist_light_model_state()/update_initial_map(),
    plus a timestamped .bak copy of each file taken before it's overwritten
    (extra safety production doesn't currently have).
    """
    pkl_targets = {
        "initial_map.pkl": initial_map,
        "class_prefix_map.pkl": class_prefix_map,
        "master_df.pkl": master_df,
        "reference_df.pkl": reference_df,
    }

    for filename, obj in pkl_targets.items():
        path = MODEL_FOLDER / filename
        _backup_file(path)
        joblib.dump(obj, path)
        logger.info("Persisted %s", filename)

    class_index_path = MODEL_FOLDER / "class_index_map.json"
    _backup_file(class_index_path)
    atomic_write_json(class_index_map, class_index_path)
    logger.info("Persisted class_index_map.json")

    freq_path = MODEL_FOLDER / "frequency_equipment.json"
    _backup_file(freq_path)
    atomic_write_json(frequency_equipment, freq_path)
    logger.info("Persisted frequency_equipment.json")


# =============================================================================
# CLI INTERFACE
# =============================================================================

def run_cli():
    """
    Command line entry point - matches the JSON contract the C# service
    (Program.cs via PythonClient.RunAsync) actually sends on stdin.

    (1) Predict device types (default action if "action" is omitted; "score"
        kept as a backward-compatible alias):
        {"project_code": "A1825", "customer_code": "Lipico", "data_ids": ["HT778", "CR1234"]}
        -> JSON array of {customer, data_id, manual_check, data_type, confidence, reason}

    (2) Manually assign equipment type(s) ("manual_assign" kept as an alias
        for the real action name Program.cs sends):
        {
          "action": "user_manual_assign",
          "project_code": "A1825",
          "customer": "Lipico",
          "assignments": [{"data_id": "KK101", "equipment": "Fan"}],
          "batch_results": [{"data_id": "...", "data_type": "..."}]
        }
        -> persists the change to MODEL_FOLDER (with .bak backups), then prints
           {"status": "ok", "applied_count": N, "applied": [...]}
    """
    try:
        payload = json.load(sys.stdin)
        action = payload.get("action", "predict")

        if action in ("user_manual_assign", "manual_assign"):
            assignments = payload.get("assignments", [])
            project_code = payload.get("project_code")
            customer = payload.get("customer") or payload.get("customer_code")
            batch_results = payload.get("batch_results")

            if not isinstance(assignments, list) or not assignments:
                raise ValueError("assignments must be a non-empty list.")

            applied = []
            for item in assignments:
                data_id = item.get("data_id") or item.get("device_id")
                equipment = item.get("equipment") or item.get("equipment_type")
                if not data_id or not equipment:
                    raise ValueError("Each assignment needs data_id and equipment.")
                record = manual_assign_equipment(
                    data_id, equipment,
                    project_code=project_code, customer=customer,
                    batch_results=batch_results,
                )
                applied.append(record)

            persist_light_state()

            print(json.dumps({
                "status": "ok",
                "applied_count": len(applied),
                "applied": applied,
            }, ensure_ascii=False))

        else:
            project_code = payload.get("project_code")
            customer_code = payload.get("customer_code")
            data_ids = payload.get("data_ids")
            if data_ids is None:
                data_ids = payload.get("device_ids", [])

            if not isinstance(data_ids, list) or not data_ids:
                raise ValueError("data_ids must be a non-empty list.")
            if len(data_ids) > MAX_BATCH_SIZE:
                raise ValueError(
                    f"data_ids length {len(data_ids)} exceeds maximum allowed batch size of {MAX_BATCH_SIZE}."
                )

            results_df = predict_device_types(data_ids, project_code=project_code, customer_code=customer_code)
            out = results_df.to_dict(orient="records")

            print(json.dumps(out, ensure_ascii=False))

    except Exception as e:
        traceback.print_exc(file=sys.stderr)
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    run_cli()
