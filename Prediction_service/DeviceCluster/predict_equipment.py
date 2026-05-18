# Claude script - Hybrid Model with Fusion Decision Prediction
# Refined and optimized version

import time
start = time.time()
from datetime import datetime
import sys
import json
import os
import re
import warnings
from pathlib import Path
import joblib
import numpy as np
import pandas as pd
from scipy.sparse import load_npz, vstack, save_npz
from sklearn.neighbors import NearestNeighbors
import traceback

warnings.filterwarnings("ignore")

print("Import time:", round(time.time() - start, 2), "seconds", file=sys.stderr)

# =============================================================================
# CONFIGURATION & FILE LOADING
# =============================================================================

# Read configuration (------------change path-----------)
with open(r"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceEquipment_Prediction\JSON\Config_filepath_application.json", "r") as f:
    config = json.load(f)


JSON_MODEL_FOLDER = Path(config["model_folder"]) # (------------change path-----------)
MODEL_FOLDER = Path(r"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceType_Prediction\model_config_devicetype")

def load_file(filename):
    """Helper function for loading pkl/npz files"""
    path = JSON_MODEL_FOLDER / filename
    return joblib.load(path)

# Load all model components
tfidf_similarity        = load_file(config["tfidf_similarity"])
tfidf_sgd               = load_file(config["tfidf_sgd"])
label_encoder           = load_file(config["label_encoder"])
initial_map             = load_file(config["initial_map"])
customer_specific_map   = load_file(config["customer_specific_map"])
customer_initial_map    = load_file(config["customer_initial_map"])
customer_project_map    = load_file(config["customer_project_map"])
sgd_model               = load_file(config["sgd_model"])
X_reference             = load_file(config["x_reference"])
pipeline                = load_file(config["classifier_pipeline"])
ref_row_map             = load_file(config['ref_row_map'])
ref_id_set              = load_file(config['ref_id_set'])
unknown_df              = load_file(config['unknown_df'])
nn                      = load_file(config["nn"])
reference_index_map     = load_file(config["reference_index_map"])
centroids               = load_file(config["centroids"])
class_prefix_map        = load_file(config["class_prefix_map"])
composite_config        = load_file(config["composite_config"])
master_df               = load_file(config["master_df"])
reference_df            = load_file(config["reference_df"])

# Composite config parameters
alpha = composite_config.get("alpha", 0.5)
prefix_boost = composite_config.get("prefix_boost", 1.0)
top_k_default = composite_config.get("top_k_default", 10)

print("[INFO] All features loaded successfully.", file=sys.stderr)
print(f"[INFO] master_df loaded: {len(master_df)} rows.", file=sys.stderr)
print(f"[INFO] reference_df loaded: {len(reference_df)} rows.", file=sys.stderr)

# =============================================================================
# CLASS INDEX MAP HANDLING
# =============================================================================

def readjson(path):
    """Read JSON file with UTF-8 encoding"""
    with open(path, "r", encoding="utf8") as f:
        return json.load(f)

def atomic_write_json(obj, dst: Path):
    """Atomic JSON write to prevent corruption"""
    tmp = dst.with_suffix(dst.suffix + ".tmp")
    with open(tmp, "w", encoding="utf8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    os.replace(tmp, dst)

def load_class_index_map(path):
    """
    Load class index mapping from JSON file.
    Handles both label->index and index->label formats.
    Returns: (class_index_map, int2label_map)
    """
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"{p} not found. Create it in the model training script.")
    
    data = readjson(p)

    # Case 1: values are ints -> label->index mapping
    if all(isinstance(v, int) or (isinstance(v, str) and v.isdigit()) for v in data.values()):
        class_index_map = {str(k): int(v) for k, v in data.items()}
    
    # Case 2: keys are digit-strings -> index->label mapping, invert it
    elif all(isinstance(k, str) and k.isdigit() for k in data.keys()):
        class_index_map = {str(v): int(k) for k, v in data.items()}
    
    else:
        # Try forgiving conversion
        numeric_keys = [k for k in data.keys() if isinstance(k, str) and k.isdigit()]
        numeric_values = [v for v in data.values() if isinstance(v, int) or (isinstance(v, str) and v.isdigit())]
        
        if len(numeric_keys) >= len(numeric_values):
            class_index_map = {str(v): int(k) for k, v in data.items()}
        elif numeric_values:
            class_index_map = {str(k): int(v) for k, v in data.items()}
        else:
            raise ValueError("Unrecognized class_index_map format.")

    # Build reverse map
    int2label_map = {int(v): str(k) for k, v in class_index_map.items()}

    # Sanity check: contiguous indices
    vals = sorted(class_index_map.values())
    if vals != list(range(len(vals))):
        raise AssertionError("class_index_map indices must be contiguous 0..N-1")

    return class_index_map, int2label_map

# Load or create class index map
CLASS_MAP_FILE = MODEL_FOLDER / "class_index_map.json"

if not CLASS_MAP_FILE.exists():
    class_index_map = {str(lbl): int(i) for i, lbl in enumerate(label_encoder.classes_)}
    atomic_write_json(class_index_map, CLASS_MAP_FILE)
    print("[WARN] class_index_map.json not found; created from label_encoder.", file=sys.stderr)
else:
    class_index_map, int2label_map = load_class_index_map(CLASS_MAP_FILE)

int2label_map = {int(v): str(k) for k, v in class_index_map.items()}

print(f"[INFO] Loaded {len(class_index_map)} classes.", file=sys.stderr)
print(f"[INFO] Example classes: {list(class_index_map.items())[:5]}", file=sys.stderr)

# Build reference_index for fast lookups
reference_index = reference_df.set_index('data_id', drop=False)

# Global variables for incremental learning
PENDING_NEW_ROWS = []
PENDING_ADDS_COUNTER = 0
BATCH_ADD_SIZE = 50

current_time = datetime.now().strftime("%H:%M %d/%m/%Y")
print(f"Model loaded on {current_time}", file=sys.stderr)

# =============================================================================
# HELPER FUNCTIONS
# =============================================================================

def extract_initial(s):
    """Extract leading alphabetic characters from equipment code"""
    if not isinstance(s, str):
        return ""
    s = s.upper().replace("_", " ").replace("-", " ")
    parts = [p for p in s.split() if p != "SP"]
    if not parts:
        return ""
    joined = "".join(parts)
    m = re.match(r'^([A-Z]+)', joined)
    return m.group(1) if m else ""

def normalize_probe(s):
    """Uppercase and remove non-alphanumeric for prefix checks"""
    return re.sub(r'[^A-Z0-9]', '', str(s).upper())

def preprocess_input_raw_series_vectorized(series):
    """
    Vectorized preprocessing of equipment IDs using pandas string methods.
    Returns list of cleaned IDs.
    """
    s = series.astype(str).str.strip().str.upper()
    s = s.str.replace(r"[\s\-_\.]", "", regex=True)
    s = s.str.replace(r"^SP(?=[A-Z0-9])", r"SP ", regex=True)
    s = s.str.replace(r"([A-Z]+)SP(?=[A-Z0-9])", r"\1 SP", regex=True)
    s = s.str.replace(r"\s+", " ", regex=True).str.strip()
    s = s.str.replace(r"(\D+)(\d+)", r"\1 \2", regex=True)
    s = s.str.replace(r"^\d+(?=[A-Z])", "", regex=True)
    return s.tolist()

def make_display_id_series(series):
    """Build display IDs for output (keep original format)"""
    s = series.astype(str).str.strip().str.upper()
    s = s.str.replace(r"[\s\-_\.]", "", regex=True)
    return s.tolist()

def safe_float(v, default=np.nan):
    """Safely convert value to float"""
    try:
        f = float(v)
        return default if np.isnan(f) else f
    except Exception:
        return default

def get_reference_row(data_id):
    """Fast lookup helper for reference data"""
    r = ref_row_map.get(data_id)
    if r is not None:
        return r
    try:
        row = reference_index.loc[data_id]
        return row.to_dict()
    except KeyError:
        return None

def replace_nan(obj):
    """Recursively replace NaN values with None for JSON serialization"""
    if isinstance(obj, float):
        return None if np.isnan(obj) else obj
    if isinstance(obj, dict):
        return {k: replace_nan(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [replace_nan(v) for v in obj]
    return obj

# =============================================================================
# SGD MODEL PREDICTION HELPERS
# =============================================================================

def _scores_to_probs(decision_scores):
    """Convert decision_function output to pseudo-probabilities"""
    dec = np.asarray(decision_scores)
    if dec.ndim == 0:
        return np.array([1.0 / (1.0 + np.exp(-float(dec)))])
    if dec.ndim == 1:
        return 1.0 / (1.0 + np.exp(-dec))
    # Multiclass: softmax
    ex = np.exp(dec - np.max(dec, axis=1, keepdims=True))
    probs = ex / ex.sum(axis=1, keepdims=True)
    return probs

def predict_with_sgd_model_single(id_str, model=sgd_model):
    """Single string prediction using SGD model"""
    if model is None:
        raise RuntimeError("sgd_model is not loaded.")
    
    Xq = tfidf_sgd.transform([id_str])
    
    if hasattr(model, "predict_proba"):
        probs = model.predict_proba(Xq)[0]
        pos = int(np.argmax(probs))
        class_val = model.classes_[pos] if hasattr(model, "classes_") else pos
        try:
            lbl = label_encoder.inverse_transform([class_val])[0]
        except Exception:
            lbl = int2label_map.get(int(class_val), str(class_val))
        return lbl, class_val, probs, float(probs.max())
    else:
        dec = model.decision_function(Xq)
        dec = np.atleast_2d(dec)
        probs = _scores_to_probs(dec)[0]
        pos = int(np.argmax(probs))
        class_val = model.classes_[pos] if hasattr(model, "classes_") else pos
        try:
            lbl = label_encoder.inverse_transform([class_val])[0]
        except Exception:
            lbl = int2label_map.get(int(class_val), str(class_val))
        return lbl, class_val, probs, float(probs.max())

def predict_with_sgd_model_batch(X_matrix, model=sgd_model):
    """Vectorized batch prediction using SGD model"""
    N = X_matrix.shape[0]
    if model is None:
        raise RuntimeError("sgd_model is not loaded.")

    if hasattr(model, "predict_proba"):
        probs = model.predict_proba(X_matrix)
        probs = np.atleast_2d(probs)
        top_pos = np.argmax(probs, axis=1)
        model_classes = np.array(getattr(model, "classes_", None))
        
        if model_classes is None:
            class_values = top_pos
            labels = [int2label_map.get(int(p), str(p)) for p in class_values]
        else:
            class_values = model_classes[top_pos]
            labels = []
            for cv in class_values:
                try:
                    labels.append(label_encoder.inverse_transform([cv])[0])
                except Exception:
                    labels.append(int2label_map.get(int(cv), str(cv)))
        
        top_conf = probs.max(axis=1).astype(float)
        return labels, class_values, probs, top_conf

    try:
        dec = model.decision_function(X_matrix)
        dec = np.atleast_2d(dec)
        probs = _scores_to_probs(dec)
        
        if probs.ndim == 1:
            probs = probs.reshape(-1, 1)
        
        top_pos = np.argmax(probs, axis=1)
        model_classes = np.array(getattr(model, "classes_", None))
        
        if model_classes is None:
            class_values = top_pos
            labels = [int2label_map.get(int(p), str(p)) for p in class_values]
        else:
            class_values = model_classes[top_pos]
            labels = []
            for cv in class_values:
                try:
                    labels.append(label_encoder.inverse_transform([cv])[0])
                except Exception:
                    labels.append(int2label_map.get(int(cv), str(cv)))
        
        top_conf = probs.max(axis=1).astype(float)
        return labels, class_values, probs, top_conf
    
    except Exception as e:
        print("[WARN] sgd_model decision_function failed:", e, file=sys.stderr)
        return [None]*N, np.full(N, -1), np.full((N, 1), np.nan), np.full(N, np.nan)

# =============================================================================
# INCREMENTAL LEARNING
# =============================================================================

def _flush_pending_new_rows(reference_df, X_reference, ref_id_set):
    """Flush pending rows into reference_df"""
    global PENDING_NEW_ROWS, reference_index, ref_row_map, PENDING_ADDS_COUNTER

    if not PENDING_NEW_ROWS:
        return reference_df, X_reference, ref_id_set

    pending_df = pd.DataFrame(PENDING_NEW_ROWS, columns=['data_id', 'data_type', 'client'])
    reference_df = pd.concat([reference_df, pending_df], ignore_index=True)

    PENDING_NEW_ROWS = []
    PENDING_ADDS_COUNTER = 0

    reference_index = reference_df.set_index('data_id', drop=False)
    ref_row_map = reference_index.to_dict(orient='index')

    return reference_df, X_reference, ref_id_set

def update_initial_map(code, equipment):
    """Update initial_map and persist to disk"""
    code = code.strip().upper()
    initial_map[code] = equipment
    
    save_path = JSON_MODEL_FOLDER / config["initial_map"]
    joblib.dump(initial_map, save_path)
    
    print(f"[INFO] Updated initial_map: {code} → {equipment}", file=sys.stderr)

def incremental_learning(
    id_str, true_label, customer,
    sgd_model, label_encoder, tfidf_sgd,
    reference_df, X_reference, ref_id_set,
    nn=None, persist_folder=None,
    ref_epoch_rebuild=50, flush_batch_size=None
):
    """Buffered incremental learning with model update"""
    global class_index_map, int2label_map, CLASS_MAP_FILE
    global PENDING_NEW_ROWS, PENDING_ADDS_COUNTER, BATCH_ADD_SIZE
    global reference_index, ref_row_map

    id_norm = id_str

    if flush_batch_size is None:
        flush_batch_size = ref_epoch_rebuild if ref_epoch_rebuild is not None else BATCH_ADD_SIZE

    # Add new class if needed
    if true_label not in class_index_map:
        next_idx = max(class_index_map.values()) + 1 if class_index_map else 0
        class_index_map[true_label] = int(next_idx)
        atomic_write_json(class_index_map, CLASS_MAP_FILE)
        int2label_map[int(next_idx)] = str(true_label)
        print(f"[INFO] Added new class '{true_label}' -> idx {next_idx}", file=sys.stderr)

    # Transform and label
    X_new = tfidf_sgd.transform([id_norm])
    y_new = np.array([class_index_map[true_label]], dtype=int)

    # Partial fit
    classes_idx = np.arange(0, max(class_index_map.values()) + 1, dtype=int)
    try:
        sgd_model.partial_fit(X_new, y_new, classes=classes_idx)
    except Exception as e:
        raise RuntimeError(f"partial_fit failed: {e}")

    # Add to reference if new
    if id_norm not in ref_id_set:
        try:
            X_reference = vstack([X_reference, X_new])
        except Exception as e:
            print(f"[WARN] vstack failed: {e}", file=sys.stderr)

        ref_id_set.add(id_norm)
        PENDING_NEW_ROWS.append([id_norm, true_label, customer])
        PENDING_ADDS_COUNTER += 1
        ref_row_map[id_norm] = {'data_id': id_norm, 'data_type': true_label, 'customer': customer}

        # Flush if buffer is full
        if len(PENDING_NEW_ROWS) >= flush_batch_size:
            try:
                reference_df, X_reference, ref_id_set = _flush_pending_new_rows(reference_df, X_reference, ref_id_set)
                reference_index = reference_df.set_index('data_id', drop=False)
                ref_row_map = reference_index.to_dict(orient='index')
                print(f"[INFO] Flushed {flush_batch_size} pending rows.", file=sys.stderr)
            except Exception as e:
                print(f"[WARN] Flush failed: {e}", file=sys.stderr)

    # Re-fit NN if provided
    if nn is not None:
        try:
            nn = NearestNeighbors(
                n_neighbors=min(10, X_reference.shape[0]),
                metric='cosine',
                algorithm='brute'
            ).fit(X_reference)
        except Exception as e:
            print(f"[WARN] NN re-fit failed: {e}", file=sys.stderr)

    # Persist if requested
    if persist_folder is not None:
        try:
            reference_df, X_reference, ref_id_set = _flush_pending_new_rows(reference_df, X_reference, ref_id_set)
        except Exception as e:
            print(f"[WARN] Flush before persist failed: {e}", file=sys.stderr)

        persist_folder = Path(persist_folder)
        persist_folder.mkdir(parents=True, exist_ok=True)
        joblib.dump(sgd_model, persist_folder / "sgd_model.pkl")
        joblib.dump(label_encoder, persist_folder / "label_encoder.pkl")
        joblib.dump(reference_df, persist_folder / "reference_df.pkl")
        save_npz(persist_folder / "X_reference.npz", X_reference)
        atomic_write_json(class_index_map, persist_folder / "class_index_map.json")

    return reference_df, X_reference, ref_id_set, label_encoder, nn

def persist_all_model_state(config, model_folder: Path):
    """Persist all model artifacts to disk"""
    print("[INFO] Persisting full model state...", file=sys.stderr)

    def save(obj, key):
        path = model_folder / config[key]
        joblib.dump(obj, path)
        print(f"[SAVED] {key} -> {path.name}", file=sys.stderr)

    save(tfidf_sgd, "tfidf_sgd")
    save(tfidf_similarity, "tfidf_similarity")
    save(label_encoder, "label_encoder")
    save(sgd_model, "sgd_model")
    save(pipeline, "classifier_pipeline")
    save(nn, "nn")
    save(initial_map, "initial_map")
    save(customer_specific_map, "customer_specific_map")
    save(customer_initial_map, "customer_initial_map")
    save(customer_project_map, "customer_project_map")
    save(class_prefix_map, "class_prefix_map")
    save(master_df, "master_df")
    save(reference_df, "reference_df")
    save(ref_row_map, "ref_row_map")
    save(ref_id_set, "ref_id_set")
    save(unknown_df, "unknown_df")
    save(X_reference, "x_reference")
    save(reference_index_map, "reference_index_map")
    save(centroids, "centroids")
    save(composite_config, "composite_config")

    print("[INFO] Model state persisted successfully.", file=sys.stderr)

# =============================================================================
# USER ACTIONS
# =============================================================================

def user_manual_assign(assignments, customer, project_code):
    """Batch manual assignment of equipment types"""
    applied = []

    for item in assignments:
        data_id = item.get("data_id")
        equipment = item.get("equipment")

        if not data_id or not equipment:
            continue

        prefix = extract_initial(data_id)
        if prefix:
            update_initial_map(prefix, equipment)

        incremental_learning(
            id_str=data_id,
            true_label=equipment,
            customer=customer,
            sgd_model=sgd_model,
            label_encoder=label_encoder,
            tfidf_sgd=tfidf_sgd,
            reference_df=reference_df,
            X_reference=X_reference,
            ref_id_set=ref_id_set,
            nn=nn,
            persist_folder=JSON_MODEL_FOLDER
        )

        applied.append({
            "data_id": data_id,
            "equipment": equipment,
            "prefix": prefix
        })

    return applied

def import_equipment_helper(equipment_list, customer, project_code):
    """Import authoritative equipment list"""
    applied = []

    for item in equipment_list:
        data_id = item.get("data_id")
        equipment = item.get("equipment")

        if not data_id or not equipment:
            continue

        prefix = extract_initial(data_id)
        if prefix:
            update_initial_map(prefix, equipment)

        incremental_learning(
            id_str=data_id,
            true_label=equipment,
            customer=customer,
            sgd_model=sgd_model,
            label_encoder=label_encoder,
            tfidf_sgd=tfidf_sgd,
            reference_df=reference_df,
            X_reference=X_reference,
            ref_id_set=ref_id_set,
            nn=nn,
            persist_folder=None
        )

        applied.append({
            "data_id": data_id,
            "equipment": equipment,
            "prefix": prefix,
            "source": "list_equipment"
        })

    return applied

# =============================================================================
# MAIN PREDICTION FUNCTION
# =============================================================================

def predict_from_list(data_ids, project_code, customer_code=None):
    """
    Core prediction function using hybrid model.
    
    Parameters:
        data_ids: list of equipment IDs
        project_code: project identifier
        customer_code: optional customer identifier
    
    Returns:
        pandas.DataFrame with predictions
    """
    global CUSTOMER_CODE

    # Customer resolution
    if customer_code is None:
        customer_project_map[project_code] = customer_project_map.get(project_code, "UNKNOWN")
        CUSTOMER_CODE = customer_project_map[project_code]
    else:
        CUSTOMER_CODE = customer_code

    test_ids = pd.DataFrame({"data_id": data_ids})
    predictions_bulk = []

    # Thresholds
    ALPHA_PREFIX_WEIGHT = 0.65
    INITIAL_DICT_CONF = 0.75
    COSINE_THRESHOLD = 0.60
    SGD_STRONG_THRESHOLD = 0.60

    print("..............Model is loading..............", file=sys.stderr)

    # Preprocess inputs
    user_inputs = preprocess_input_raw_series_vectorized(test_ids['data_id'])
    N_inputs = len(user_inputs)
    display_ids = make_display_id_series(test_ids['data_id'])

    # Exact match check
    is_exact = [u in ref_id_set for u in user_inputs]

    # Vectorization with separate TF-IDF vectorizers
    X_test_sgd = tfidf_sgd.transform(user_inputs)
    X_test_sim = tfidf_similarity.transform(user_inputs)

    # Validate feature dimensions
    assert X_test_sgd.shape[1] == sgd_model.coef_.shape[1], "SGD feature mismatch"
    assert X_test_sim.shape[1] == X_reference.shape[1], "Similarity feature mismatch"

    # SGD predictions
    sgd_confs = np.full(N_inputs, np.nan, dtype=float)
    sgd_labels_list = [None] * N_inputs

    if sgd_model is not None:
        try:
            labels_vec, class_vals, probs_mat, top_conf = predict_with_sgd_model_batch(X_test_sgd, model=sgd_model)
            sgd_labels_list = list(labels_vec)
            sgd_confs = np.asarray(top_conf, dtype=float)
            
            print(f"[DEBUG] SGD predictions: {len(labels_vec)} samples", file=sys.stderr)
            print(f"[DEBUG] Confidence range: [{np.nanmin(top_conf):.4f}, {np.nanmax(top_conf):.4f}]", file=sys.stderr)
            
        except Exception as e_batch:
            print(f"[WARN] Batch SGD failed: {e_batch}. Falling back to per-row.", file=sys.stderr)
            for i in range(N_inputs):
                try:
                    lbl, _, _, topc = predict_with_sgd_model_single(user_inputs[i], model=sgd_model)
                    sgd_labels_list[i] = lbl
                    sgd_confs[i] = float(topc) if topc is not None else np.nan
                except Exception:
                    sgd_labels_list[i] = None
                    sgd_confs[i] = np.nan
    else:
        print("[WARN] sgd_model not loaded; predictions will be limited.", file=sys.stderr)

    # NN candidates for similarity
    distances, indices = nn.kneighbors(
        X_test_sim,
        n_neighbors=min(top_k_default, X_reference.shape[0]),
        return_distance=True
    )
    cosine_sims = 1.0 - distances
    input_prefixes = [extract_initial(u) for u in user_inputs]

    # Main prediction loop
    for i, user_input in enumerate(user_inputs):
        pred_label = ""
        confidence = 0.0
        source = ""
        dictionary_match = ""
        tfidf_sim = np.nan
        prefix_sim = np.nan
        composite_sim = np.nan
        reason = ""
        input_prefix = input_prefixes[i]
        best_ref_prefix = ""
        display_id = display_ids[i]

        # Skip all-letters inputs
        if re.fullmatch(r"[A-Z]+", user_input.replace(" ", "")):
            predictions_bulk.append([
                display_id, "Unknown", 0.0, "all_letters", "",
                input_prefix, "", np.nan, np.nan, np.nan, np.nan, "all_letters"
            ])
            continue

        # Get SGD outputs
        sgd_label = sgd_labels_list[i]
        sgd_conf = safe_float(sgd_confs[i], default=np.nan)

        # Exact match check
        exact_flag = False
        exact_label = None
        if is_exact[i]:
            matched = get_reference_row(user_input)
            if matched is not None:
                exact_flag = True
                exact_label = matched.get("data_type")
                best_ref_prefix = extract_initial(matched.get("data_id", ""))
                dictionary_match = f"EXACT:{matched.get('data_id')} — {exact_label}"

        # Dictionary lookup
        probe_for_dict = normalize_probe(user_input)
        candidates = [probe_for_dict]
        if probe_for_dict.startswith("SP") and len(probe_for_dict) > 2:
            candidates.append(probe_for_dict[2:])

        chosen_initial = None
        for candidate in candidates:
            valid_initials = []
            for ini in initial_map.keys():
                if not candidate.startswith(ini):
                    continue                             
                
                remainder = candidate[len(ini):]      # STRICT CHECK: After prefix, must be ONLY digits (or end of string)
                
                # Valid cases:
                # 1. Exact match: "CR" matches "CR"
                # 2. Prefix + digits only: "CR1234" matches "CR"
                # Invalid cases:
                # 1. Prefix + letters: "CR1234SPD" should NOT match "CR"
                # 2. Prefix + mixed: "CR123ABC" should NOT match "CR"
                
                if len(remainder) == 0:                # Exact match (e.g., just "CR")
                    valid_initials.append(ini)
                elif remainder.isdigit():               # Prefix followed by ONLY digits (e.g., "CR1234")
                    valid_initials.append(ini)          # else: has letters after prefix → REJECT              

            if valid_initials:
                valid_initials.sort(key=len, reverse=True)
                chosen_initial = valid_initials[0]
                break

        dict_matched = False
        dict_label_selected = None
        
        if chosen_initial:
            dict_label = initial_map[chosen_initial]
            dictionary_match = f"{chosen_initial} — {dict_label}"
            best_ref_prefix = chosen_initial
            prefix_sim = 1.0
            reason = "initial_dict_match_found"
            dict_matched = True
            dict_label_selected = dict_label

        # Similarity candidates
        idxs = indices[i]
        sims = cosine_sims[i]

        cand_ids = reference_df['data_id'].iloc[idxs].astype(str).values
        cand_types = reference_df['data_type'].iloc[idxs].values
        ref_prefixes_raw = [extract_initial(cid) for cid in cand_ids]
        ref_prefixes = [(p or "").strip().upper() for p in ref_prefixes_raw]

        input_prefix_norm = (input_prefix or "").strip().upper()
        chosen_initial_norm = (chosen_initial or "").strip().upper() if chosen_initial else None

        # Prefix matching
        if chosen_initial_norm:
            prefix_scores = np.array([1.0 if (p == chosen_initial_norm and p != "") else 0.0 for p in ref_prefixes])
        else:
            prefix_scores = np.array([1.0 if (p == input_prefix_norm and p != "") else 0.0 for p in ref_prefixes])

        # Composite similarity
        alpha = ALPHA_PREFIX_WEIGHT
        composite_scores = (alpha * prefix_scores) + ((1 - alpha) * sims)

        best_idx_local = int(np.argmax(composite_scores))
        best_row_id = cand_ids[best_idx_local]
        cosine_label = cand_types[best_idx_local]
        composite_sim = float(composite_scores[best_idx_local])
        tfidf_sim = float(sims[best_idx_local])
        prefix_sim = float(prefix_scores[best_idx_local])
        best_ref_prefix = ref_prefixes_raw[best_idx_local]

        # Fusion decision logic
        if (not np.isnan(sgd_conf)) and sgd_conf >= SGD_STRONG_THRESHOLD:
            pred_label = sgd_label
            confidence = sgd_conf
            source = "sgd"
            reason = "sgd_strong"

        elif (not np.isnan(composite_sim)) and composite_sim >= COSINE_THRESHOLD:
            pred_label = cosine_label
            confidence = composite_sim
            source = "composite similarity"
            reason = "cosine_prefix_accepted"

        elif dict_matched:
            pred_label = dict_label_selected
            confidence = INITIAL_DICT_CONF
            source = "initial_dict"
            reason = "initial_dict_only"

        else:
            if not pred_label:
                pred_label = "Unknown"
            confidence = max(
                0.0 if np.isnan(composite_sim) else composite_sim,
                0.0 if np.isnan(sgd_conf) else sgd_conf
            )
            source = "unknown"
            reason = "no_confident_source"

        predictions_bulk.append([
            display_id, pred_label, confidence, source, dictionary_match,
            input_prefix, best_ref_prefix, tfidf_sim, prefix_sim,
            composite_sim, sgd_conf, reason
        ])

    # Build results DataFrame
    results_df = pd.DataFrame(
        predictions_bulk,
        columns=[
            "data_id", "data_type", "confidence", "source", "dictionary_match",
            "input_prefix", "best_ref_prefix", "tfidf_sim", "prefix_sim",
            "composite_sim", "sgd_conf", "reason"
        ]
    )

    results_df["customer"] = CUSTOMER_CODE
    results_df.insert(0, "customer", results_df.pop("customer"))

    if "manual_check" not in results_df.columns:
        results_df.insert(loc=2, column="manual_check", value="")

    return results_df

# =============================================================================
# CLI INTERFACE
# =============================================================================

def run_cli():
    """
    Command line / C# entry point.
    Expects JSON from stdin with format:
    {
        "action": "predict" | "user_manual_assign" | "import_equipment",
        "project_code": "A1825",
        "customer_code": "Lipico",
        "data_ids": ["1HM887", "CR1234", ...]
    }
    """
    try:
        payload = json.load(sys.stdin)
        action = payload.get("action", "predict")

        # (1) User Manual Assignment
        if action == "user_manual_assign":
            assignments = payload.get("assignments", [])
            customer = payload.get("customer")
            project_code = payload.get("project_code")

            if not project_code:
                raise ValueError("project_code is required for user_manual_assign.")
            if not customer:
                raise ValueError("customer is required for user_manual_assign.")
            if not isinstance(assignments, list) or not assignments:
                raise ValueError("assignments list is empty or invalid.")

            applied = user_manual_assign(assignments, customer, project_code)
            persist_all_model_state(config, JSON_MODEL_FOLDER)

            print(json.dumps({
                "status": "ok",
                "applied_count": len(applied),
                "applied": applied
            }, ensure_ascii=False))
            return

        # (2) Import Equipment
        elif action == "import_equipment":
            equipment_list = payload.get("equipment_list", [])
            customer = payload.get("customer")
            project_code = payload.get("project_code")

            if not project_code:
                raise ValueError("project_code is required for import_equipment.")
            if not customer:
                raise ValueError("customer is required for import_equipment.")
            if not isinstance(equipment_list, list) or not equipment_list:
                raise ValueError("equipment_list is empty or invalid.")

            applied = import_equipment_helper(equipment_list, customer, project_code)
            persist_all_model_state(config, JSON_MODEL_FOLDER)

            print(json.dumps({
                "status": "ok",
                "source": "list_equipment",
                "applied_count": len(applied),
                "applied": applied
            }, ensure_ascii=False))
            return

        # (3) Prediction
        else:
            project_code = payload.get("project_code")
            customer_code = payload.get("customer_code")
            data_ids = payload.get("data_ids", [])

            if not project_code:
                raise ValueError("project_code is required in JSON input.")
            if not isinstance(data_ids, list) or not data_ids:
                raise ValueError("data_ids must be a non-empty list.")

            print(f"[INFO] Project: {project_code}, Customer: {customer_code}", file=sys.stderr)

            results_df = predict_from_list(data_ids, project_code, customer_code=customer_code)

            # Filter columns for output
            columns_to_send = ["customer", "data_id", "manual_check", "data_type", "confidence", "sgd_conf", "reason"]
            filtered_df = results_df[columns_to_send].copy()

            # Handle numeric columns
            numeric_columns = ['confidence', 'sgd_conf']
            for col in numeric_columns:
                if col in filtered_df.columns:
                    filtered_df[col] = filtered_df[col].replace([np.inf, -np.inf], None)
                    filtered_df[col] = filtered_df[col].where(pd.notnull(filtered_df[col]), None)
                    filtered_df[col] = filtered_df[col].apply(lambda x: float(x) if x is not None else None)

            # Handle string columns
            for col in ['customer', 'data_id', 'manual_check', 'data_type', 'reason']:
                if col in filtered_df.columns:
                    filtered_df[col] = filtered_df[col].where(pd.notnull(filtered_df[col]), None)
                    filtered_df[col] = filtered_df[col].astype(str).replace('nan', None)

            # Convert to dict and clean
            out = filtered_df.to_dict(orient="records")
            out = replace_nan(out)
            
            print(json.dumps(out, ensure_ascii=False))

    except Exception as e:
        traceback.print_exc(file=sys.stderr)
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    run_cli()