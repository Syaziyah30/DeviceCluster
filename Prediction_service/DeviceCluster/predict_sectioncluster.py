import os
import re
import sys
import json
import pickle
import logging
import warnings
from datetime import datetime

import numpy as np
import pandas as pd
from sklearn.preprocessing import LabelEncoder

warnings.filterwarnings("ignore")


# ============================================================
# LOGGING
# ============================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger(__name__)


# ============================================================
# LOAD CONFIG FROM config_sectioncluster.json
# ============================================================

def load_config(config_path: str = None) -> dict:
    if config_path is None:
        _script_dir = os.path.dirname(os.path.abspath(__file__))
        config_path = os.path.join(_script_dir, "predict_sectioncluster_folder", "config_sectioncluster.json")

    if not os.path.exists(config_path):
        raise FileNotFoundError(
            f"config_sectioncluster.json not found at: {config_path}\n"
            f"Please ensure config_sectioncluster.json exists in DeviceCluster_Prediction/"
        )

    with open(config_path, "r", encoding="utf-8") as f:
        return json.load(f)


# Read config once at module load
_cfg = load_config()

_BASE_DIR         = os.path.join(os.path.dirname(os.path.abspath(__file__)), "predict_sectioncluster_folder")
MODEL_DIR         = os.path.join(_BASE_DIR, _cfg["model_folder"])
UNKNOWN_THRESHOLD = float(_cfg["unknown_threshold"])

# ◄── ADDED: corrections file path — sits beside config in predict_sectioncluster_folder/
MANUAL_ASSIGN_SECTION_CLUSTER = os.path.join(_BASE_DIR, "manual_assign_sectioncluster.json")

# ============================================================
# PIPELINE CACHE
# ============================================================

_pipeline: dict | None = None

def get_pipeline() -> dict:
    global _pipeline
    if _pipeline is None:
        _pipeline = load_pipeline(MODEL_DIR)
    return _pipeline


# ============================================================
# SafeLabelEncoder
# ============================================================

class SafeLabelEncoder:
    UNKNOWN_LABEL = "__UNKNOWN__"

    def __init__(self):
        self._le      = LabelEncoder()
        self.classes_ = None

    def fit(self, y):
        labels = list(pd.Series(y).astype(str).unique())
        if self.UNKNOWN_LABEL not in labels:
            labels = [self.UNKNOWN_LABEL] + labels
        self._le.fit(labels)
        self.classes_ = self._le.classes_
        return self

    def transform(self, y):
        y_str  = pd.Series(y).astype(str)
        known  = set(self.classes_)
        y_safe = y_str.where(y_str.isin(known), other=self.UNKNOWN_LABEL)
        return self._le.transform(y_safe)

    def fit_transform(self, y):
        return self.fit(y).transform(y)

    def inverse_transform(self, y):
        return self._le.inverse_transform(y)

    def is_known(self, values):
        known = set(self.classes_) - {self.UNKNOWN_LABEL}
        return pd.Series(values).astype(str).isin(known)

    def real_classes(self):
        return [c for c in self.classes_ if c != self.UNKNOWN_LABEL]


# ============================================================
# FEATURE ENGINEERING HELPERS
# ============================================================

def extract_numeric_block(device_id: str) -> int:
    match = re.search(r"\d+", str(device_id))
    return int(match.group()) if match else -1

def extract_numeric_string(device_id: str) -> str:
    match = re.search(r"\d+", str(device_id))
    return match.group() if match else ""

def extract_suffix_letters(device_id: str) -> str:
    match = re.search(r"\d+([A-Za-z]*)$", str(device_id))
    return match.group(1).upper() if match else ""

def extract_suffix_full(device_id: str) -> str:
    match = re.search(r"\d+(.*)$", str(device_id))
    return match.group(1) if match else ""

def extract_numeric_suffix_shape(device_id: str) -> str:
    match = re.search(r"\d.*", str(device_id))
    if not match:
        return "NODIGIT"
    return "".join("L" if c.isalpha() else "D" for c in match.group())


# ============================================================
# LOAD PIPELINE
# ============================================================

def load_pipeline(model_dir: str) -> dict:
    required_files = ["model_section.pkl", "model_cluster.pkl", "pipeline_config.pkl"]
    for fname in required_files:
        fpath = os.path.join(model_dir, fname)
        if not os.path.exists(fpath):
            raise FileNotFoundError(f"Required model file not found: {fpath}")

    try:
        with open(os.path.join(model_dir, "model_section.pkl"),   "rb") as f:
            model_section = pickle.load(f)
        with open(os.path.join(model_dir, "model_cluster.pkl"),   "rb") as f:
            model_cluster = pickle.load(f)
        with open(os.path.join(model_dir, "pipeline_config.pkl"), "rb") as f:
            pipeline_config = pickle.load(f)
    except Exception as e:
        raise RuntimeError(f"Failed to load model files: {e}")

    logger.info("Pipeline loaded successfully from: %s", model_dir)

    return {
        "model_section"   : model_section,
        "model_cluster"   : model_cluster,
        "config"          : pipeline_config,
        "known_customers" : pipeline_config["known_customers"],
        "known_num_widths": pipeline_config.get("known_num_widths", None),
        "reliable_widths" : pipeline_config.get("reliable_widths",  None),
        "max_num_width"   : pipeline_config.get("max_num_width",    None),
        "ood_scaler"      : pipeline_config.get("ood_scaler",             None),
        "ood_knn"         : pipeline_config.get("ood_knn",                None),
        "ood_features"    : pipeline_config.get("ood_features",           None),
        "ood_threshold"   : pipeline_config.get("ood_distance_threshold", None),
        "le_customer"     : pipeline_config.get("le_customer",            None),
    }


# ============================================================
# INPUT VALIDATION
# ============================================================

def validate_records(records: list[dict]) -> None:
    if not records:
        raise ValueError("Input records list is empty.")

    for i, rec in enumerate(records):
        if not isinstance(rec, dict):
            raise ValueError(f"Record at index {i} is not a dict: {rec}")
        if "device_id" not in rec and "DEVICE_ID" not in rec:
            raise ValueError(f"Record at index {i} missing 'device_id' key.")
        if "customer" not in rec and "CUSTOMER" not in rec:
            raise ValueError(f"Record at index {i} missing 'customer' key.")


# ============================================================
# GUARD — CUSTOMER GATE + NUMERIC WIDTH WARNING
# ============================================================

def check_entities(df: pd.DataFrame, pipeline: dict) -> tuple[pd.Series, pd.Series]:
    known_customers = pipeline["known_customers"]
    reliable_widths = pipeline.get("reliable_widths", None)
    max_num_width   = pipeline.get("max_num_width",   None)

    rejections = []
    warnings_  = []

    for _, row in df.iterrows():
        row_reject = []
        row_warn   = []

        dev = str(row.get("DEVICE_ID", "")).strip()
        if not dev or dev.upper() in ("", "NAN", "NONE"):
            row_reject.append("missing DEVICE_ID")

        cust = str(row.get("CUSTOMER", "")).strip()
        if not cust or cust.upper() in ("", "NAN", "NONE"):
            row_reject.append("missing CUSTOMER")
        elif cust not in known_customers:
            row_reject.append(f"unseen CUSTOMER '{cust}' — please assign manually")

        if reliable_widths is not None and max_num_width is not None:
            numeric_str = extract_numeric_string(str(row.get("DEVICE_ID", "")))
            if numeric_str:
                width = len(numeric_str)
                if width > max_num_width:
                    row_warn.append(
                        f"numeric field width {width} digits exceeds training maximum "
                        f"of {max_num_width} digits (confidence penalised by KNN scorer)"
                    )
                elif width not in reliable_widths:
                    row_warn.append(
                        f"numeric field width {width} digits is rare in training data; "
                        f"reliable widths: {sorted(reliable_widths)} digits "
                        f"(confidence penalised by KNN scorer)"
                    )

        rejections.append("; ".join(row_reject))
        warnings_.append("; ".join(row_warn))

    return (
        pd.Series(rejections, index=df.index),
        pd.Series(warnings_,  index=df.index),
    )


# ============================================================
# BUILD FEATURES
# ============================================================

def build_features(df: pd.DataFrame, config: dict) -> pd.DataFrame:
    le_suffix_lt   = config["le_suffix_letter"]
    le_suffix_last = config["le_suffix_last"]
    le_customer    = config["le_customer"]
    le_shape       = config["le_shape"]

    df = df.copy().reset_index(drop=True)

    df["numeric_block"]        = df["DEVICE_ID"].apply(extract_numeric_block)
    df["device_suffix_letter"] = df["DEVICE_ID"].apply(extract_suffix_letters)
    df["suffix_full"]          = df["DEVICE_ID"].apply(extract_suffix_full)
    df["device_id_length"]     = df["DEVICE_ID"].astype(str).str.len()
    df["has_suffix_letter"]    = (df["device_suffix_letter"] != "").astype(int)
    df["has_numeric"]          = (df["numeric_block"] != -1).astype(int)

    _numeric_raw_str            = df["DEVICE_ID"].apply(extract_numeric_string)
    df["count_num_digit"]       = _numeric_raw_str.str.len()
    df["numeric_remove_zero"]   = df["numeric_block"]
    df["count_num_remove_zero"] = df["numeric_remove_zero"].apply(
        lambda x: len(str(x)) if x != -1 else 0
    )
    df["leading_zero_count"] = df["count_num_digit"] - df["count_num_remove_zero"]

    df["suffix_length"]       = df["suffix_full"].astype(str).str.len()
    df["suffix_has_digit"]    = df["suffix_full"].astype(str).str.contains(r"\d",       regex=True).astype(int)
    df["suffix_has_letter"]   = df["suffix_full"].astype(str).str.contains(r"[A-Za-z]", regex=True).astype(int)
    df["suffix_has_decimal"]  = df["suffix_full"].astype(str).str.contains(r"\.",        regex=True).astype(int)
    df["suffix_digit_count"]  = df["suffix_full"].astype(str).str.count(r"\d")
    df["suffix_letter_count"] = df["suffix_full"].astype(str).str.count(r"[A-Za-z]")

    df["numeric_suffix_shape"] = df["DEVICE_ID"].apply(extract_numeric_suffix_shape)
    df["shape_enc"]            = le_shape.transform(df["numeric_suffix_shape"])

    df["suffix_starts_with_digit"] = df["suffix_full"].apply(
        lambda s: 1 if len(str(s)) > 0 and str(s)[0].isdigit() else 0
    )
    df["suffix_last_char"] = df["suffix_full"].apply(
        lambda s: str(s)[-1] if len(str(s)) > 0 else ""
    )
    df["suffix_last_char_is_letter"] = df["suffix_last_char"].apply(
        lambda c: 1 if isinstance(c, str) and c.isalpha() else 0
    )
    df["suffix_last_char_is_digit"] = df["suffix_last_char"].apply(
        lambda c: 1 if isinstance(c, str) and c.isdigit() else 0
    )

    df["equip_id_length"]      = df["DEVICE_ID"].astype(str).str.len()
    df["equip_id_digit_count"] = df["DEVICE_ID"].astype(str).str.count(r"\d")

    df["suffix_letter_enc"]    = le_suffix_lt.transform(df["device_suffix_letter"])
    df["suffix_last_char_enc"] = le_suffix_last.transform(df["suffix_last_char"])
    df["customer_enc"]         = le_customer.transform(df["CUSTOMER"])

    return df


# ============================================================
# KNN OOD CONFIDENCE PENALTY
# ============================================================

def apply_ood_penalty(
    conf_raw: np.ndarray,
    df_feat: pd.DataFrame,
    pipeline: dict,
) -> tuple[np.ndarray, np.ndarray]:
    ood_scaler    = pipeline.get("ood_scaler",    None)
    ood_knn       = pipeline.get("ood_knn",       None)
    ood_features  = pipeline.get("ood_features",  None)
    ood_threshold = pipeline.get("ood_threshold", None)

    if any(v is None for v in [ood_scaler, ood_knn, ood_features, ood_threshold]):
        return conf_raw, np.zeros(len(conf_raw))

    if ood_threshold <= 0:
        return conf_raw, np.zeros(len(conf_raw))

    X_ood = (
        df_feat[ood_features]
        .apply(pd.to_numeric, errors="coerce")
        .fillna(0)
    )
    X_ood_scaled = ood_scaler.transform(X_ood)
    distances, _ = ood_knn.kneighbors(X_ood_scaled)
    avg_dist      = distances.mean(axis=1)

    ratio    = np.maximum(0, avg_dist - ood_threshold) / ood_threshold
    adjusted = conf_raw / (1 + ratio)

    return adjusted, avg_dist


# ============================================================
# PREDICT
# ============================================================

def predict(
    records: list[dict],
    pipeline: dict,
    threshold: float = UNKNOWN_THRESHOLD,
) -> pd.DataFrame:
    validate_records(records)

    config        = pipeline["config"]
    model_section = pipeline["model_section"]
    model_cluster = pipeline["model_cluster"]
    le_section    = config["le_section"]
    le_cluster    = config["le_cluster"]

    section_features = config["section_features"]
    cluster_features = config["cluster_features"]

    base_df         = pd.DataFrame(records)
    base_df.columns = base_df.columns.str.upper()
    if "PROJECT" not in base_df.columns:
        base_df["PROJECT"] = ""

    rejection_reasons, format_warnings = check_entities(base_df, pipeline)
    eligible_mask = rejection_reasons == ""

    pred_section = ["UNKNOWN"] * len(base_df)
    sec_conf     = [None]      * len(base_df)
    pred_cluster = ["UNKNOWN"] * len(base_df)
    clu_conf     = [None]      * len(base_df)

    if eligible_mask.any():
        elig_idx = base_df.index[eligible_mask].tolist()
        df_elig  = base_df.loc[elig_idx].copy()
        df_feat  = build_features(df_elig, config)

        X_sec = (
            df_feat[section_features]
            .apply(pd.to_numeric, errors="coerce")
            .fillna(0)
        )

        sec_proba_raw           = model_section.predict_proba(X_sec)
        sec_pred_enc            = np.argmax(sec_proba_raw, axis=1)
        sec_conf_raw            = sec_proba_raw.max(axis=1)
        sec_conf_adj, _         = apply_ood_penalty(sec_conf_raw, df_feat, pipeline)

        sec_decoded = le_section.inverse_transform(sec_pred_enc)
        sec_decoded = np.where(
            (sec_decoded == SafeLabelEncoder.UNKNOWN_LABEL) | (sec_decoded == "__OOD__"),
            "UNKNOWN", sec_decoded,
        )

        X_clu = (
            df_feat[[f for f in cluster_features if f != "predicted_section"]]
            .apply(pd.to_numeric, errors="coerce")
            .fillna(0)
            .copy()
        )
        X_clu["predicted_section"] = sec_pred_enc
        X_clu = X_clu[cluster_features]

        clu_proba_raw           = model_cluster.predict_proba(X_clu)
        clu_pred_enc            = np.argmax(clu_proba_raw, axis=1)
        clu_conf_raw            = clu_proba_raw.max(axis=1)
        clu_conf_adj, _         = apply_ood_penalty(clu_conf_raw, df_feat, pipeline)

        clu_conf_adj = np.where(
            sec_conf_adj < threshold,
            clu_conf_adj * sec_conf_adj,
            clu_conf_adj,
        )

        clu_decoded = le_cluster.inverse_transform(clu_pred_enc)
        clu_decoded = np.where(
            (clu_decoded == SafeLabelEncoder.UNKNOWN_LABEL) | (clu_decoded == "__OOD__"),
            "UNKNOWN", clu_decoded,
        )

        sec_final = np.where(sec_conf_adj >= threshold, sec_decoded, "UNKNOWN")
        clu_final = np.where(clu_conf_adj >= threshold, clu_decoded, "UNKNOWN")
        clu_final = np.where(sec_final == "UNKNOWN", "UNKNOWN", clu_final)

        for i, orig_idx in enumerate(elig_idx):
            pred_section[orig_idx] = sec_final[i]
            sec_conf[orig_idx]     = round(float(sec_conf_adj[i]) * 100, 2)
            pred_cluster[orig_idx] = clu_final[i]
            clu_conf[orig_idx]     = round(float(clu_conf_adj[i]) * 100, 2)

    result = base_df[["DEVICE_ID", "CUSTOMER", "PROJECT"]].copy()
    result["PREDICTED_SECTION"]  = pred_section
    result["SECTION_CONFIDENCE"] = sec_conf
    result["PREDICTED_CLUSTER"]  = pred_cluster
    result["CLUSTER_CONFIDENCE"] = clu_conf
    result["REJECTION_REASON"]   = rejection_reasons.values
    result["FORMAT_WARNING"]     = format_warnings.values

    return result


# ============================================================
# TOP-N CLUSTER SUGGESTIONS (single device)              ◄── ADDED
# ============================================================

def get_top_clusters(device_id: str, customer: str, project: str,
                      pipeline: dict, top_n: int = 3) -> list[dict]:
    config        = pipeline["config"]
    model_section = pipeline["model_section"]
    model_cluster = pipeline["model_cluster"]
    le_section    = config["le_section"]
    le_cluster    = config["le_cluster"]

    section_features = config["section_features"]
    cluster_features = config["cluster_features"]

    base_df = pd.DataFrame([{"DEVICE_ID": device_id, "CUSTOMER": customer, "PROJECT": project}])
    base_df.columns = base_df.columns.str.upper()

    rejection_reasons, _ = check_entities(base_df, pipeline)
    if rejection_reasons.iloc[0]:
        raise ValueError(f"Cannot compute top clusters: {rejection_reasons.iloc[0]}")

    df_feat = build_features(base_df, config)

    # ── Section prediction (single best, same as predict()) ────────────
    X_sec = df_feat[section_features].apply(pd.to_numeric, errors="coerce").fillna(0)
    sec_proba_raw = model_section.predict_proba(X_sec)
    sec_pred_enc  = np.argmax(sec_proba_raw, axis=1)

    sec_decoded = le_section.inverse_transform(sec_pred_enc)
    predicted_section = str(sec_decoded[0])
    if predicted_section in (SafeLabelEncoder.UNKNOWN_LABEL, "__OOD__"):
        predicted_section = "UNKNOWN"

    # ── Cluster probabilities, conditioned on predicted section ────────
    X_clu = (
        df_feat[[f for f in cluster_features if f != "predicted_section"]]
        .apply(pd.to_numeric, errors="coerce")
        .fillna(0)
        .copy()
    )
    X_clu["predicted_section"] = sec_pred_enc
    X_clu = X_clu[cluster_features]

    clu_proba_raw = model_cluster.predict_proba(X_clu)[0]   # single row → 1D array

    # ── Take top-N by probability (this is the part predict() throws away) ──
    top_idx = np.argsort(clu_proba_raw)[::-1][:top_n]

    results = []
    for idx in top_idx:
        cluster_label = le_cluster.inverse_transform([idx])[0]
        if cluster_label in (SafeLabelEncoder.UNKNOWN_LABEL, "__OOD__"):
            continue
        results.append({
            "section"    : predicted_section,
            "cluster"    : str(cluster_label),
            "probability": round(float(clu_proba_raw[idx]) * 100, 2),
        })

    return results


# ============================================================
# SAVE CORRECTION                                          ◄── ADDED
# ============================================================

def save_manual_assign_sectioncluster(device_id: str, customer: str, project: str,
                    					correct_section: str = None, correct_cluster: str = None) -> dict:
    """
    Append a manual correction to manual_corrections.json.
    Used as training data during next XGBoost retrain.
    """
    # Load existing corrections
    if os.path.exists(MANUAL_ASSIGN_SECTION_CLUSTER):
        with open(MANUAL_ASSIGN_SECTION_CLUSTER, "r", encoding="utf-8") as f:
            corrections = json.load(f)
    else:
        corrections = []

    entry = {
        "device_id"       : device_id,
        "customer"        : customer,
        "project"         : project,
        "correct_section" : correct_section,
        "correct_cluster" : correct_cluster,
        "corrected_at"    : datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }

    corrections.append(entry)

    # Atomic write — prevent file corruption on crash
    tmp_path = MANUAL_ASSIGN_SECTION_CLUSTER + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(corrections, f, ensure_ascii=False, indent=2)
    os.replace(tmp_path, MANUAL_ASSIGN_SECTION_CLUSTER)

    logger.info("Correction saved: %s → section=%s, cluster=%s",
                device_id, correct_section, correct_cluster)

    return {"status": "ok", "saved": entry}


# ============================================================
# CLEAN NAN HELPER
# ============================================================

def _clean_nan(obj):
    if isinstance(obj, float) and (obj != obj or obj == float("inf") or obj == float("-inf")):
        return None
    if isinstance(obj, dict):
        return {k: _clean_nan(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_clean_nan(v) for v in obj]
    return obj


# ============================================================
# RUN CLI — replaces main(), mirrors predict_equipment.py  ◄── MODIFIED
# ============================================================

def run_cli():
    """
    Command line / C# entry point.
    Expects JSON from stdin:

    Predict:
    {
        "action"  : "predict",
        "records" : [{"device_id": "V160", "customer": "UGS", "project": "A1825"}, ...]
    }

    Save Correction:
    {
        "action"          : "save_manual_assign_sectioncluster",
        "device_id"       : "V160",
        "customer"        : "UGS",
        "project"         : "A1825",
        "correct_section" : "SECTION 3",
        "correct_cluster" : "CLUSTER A"
    }
    """
    try:
        payload = json.load(sys.stdin)
        action  = payload.get("action", "predict")

        # (1) Save Correction
        if action == "save_manual_assign_sectioncluster":
            device_id = payload.get("device_id")
            customer  = payload.get("customer")
            project   = payload.get("project")

            if not device_id:
                raise ValueError("device_id is required for save_manual_assign_sectioncluster.")
            if not customer:
                raise ValueError("customer is required for save_manual_assign_sectioncluster.")
            if not project:
                raise ValueError("project is required for save_manual_assign_sectioncluster.")

            result = save_manual_assign_sectioncluster(
                device_id       = device_id,
                customer        = customer,
                project         = project,
                correct_section = payload.get("correct_section", None),
                correct_cluster = payload.get("correct_cluster", None),
            )
            print(json.dumps(result, ensure_ascii=False))
            return

        # (2) Top-3 cluster suggestions for a single device      ◄── ADDED
        elif action == "top_clusters":
            device_id = payload.get("device_id")
            customer  = payload.get("customer_code") or payload.get("customer")
            project   = payload.get("project_code")  or payload.get("project")
            top_n     = payload.get("top_n", 3)

            if not device_id:
                raise ValueError("device_id is required for top_clusters.")

            pipeline = get_pipeline()
            result   = get_top_clusters(device_id, customer, project, pipeline, top_n=top_n)

            print(json.dumps(_clean_nan(result), ensure_ascii=False))
            return

        # (3) Predict (default)
        else:
            records = payload.get("records", [])

            if not isinstance(records, list) or not records:
                raise ValueError("records must be a non-empty list.")

            pipeline  = get_pipeline()
            result_df = predict(records, pipeline, threshold=UNKNOWN_THRESHOLD)

            out = _clean_nan(result_df.to_dict(orient="records"))
            print(json.dumps(out, ensure_ascii=False))

    except Exception as e:
        logger.error("run_cli failed: %s", str(e))
        print(json.dumps({"status": "error", "message": str(e)}), file=sys.stdout)
        sys.exit(1)


if __name__ == "__main__":
    run_cli()   # ◄── MODIFIED: was main()