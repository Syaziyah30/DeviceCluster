"""
predict_pipeline.py
Two-stage device classification inference pipeline (Section -> Cluster).

Usage (called from C#):
    python predict_pipeline.py --device_id LL001 --customer OILTEK
    python predict_pipeline.py --device_id LL001 --customer OILTEK --project A1706

Output:
    JSON string printed to stdout — C# reads and parses this.
"""

import os
import re
import sys
import json
import pickle
import logging
import argparse
import warnings
import configparser
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
# LOAD CONFIG FROM config.ini
# ============================================================

def load_config(config_path: str = None) -> configparser.ConfigParser:
    """
    Load settings from config.ini.
    Looks for config.ini in the same folder as this script by default.
    """
    if config_path is None:
        config_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.ini")

    if not os.path.exists(config_path):
        raise FileNotFoundError(
            f"config.ini not found at: {config_path}\n"
            f"Please create config.ini next to predict_pipeline.py."
        )

    cfg = configparser.ConfigParser()
    cfg.read(config_path)
    return cfg


# Read config once at module load
_cfg              = load_config()
MODEL_DIR         = _cfg["PATHS"]["MODEL_DIR"].strip()
OUTPUT_DIR        = _cfg["PATHS"]["OUTPUT_DIR"].strip()
UNKNOWN_THRESHOLD = float(_cfg["SETTINGS"]["UNKNOWN_THRESHOLD"].strip())


# ============================================================
# SafeLabelEncoder
# ============================================================

class SafeLabelEncoder:
    """
    LabelEncoder extended with an '__UNKNOWN__' sentinel class.
    Unseen labels at transform time are mapped to '__UNKNOWN__'
    instead of raising an error.
    """

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
    """Load all saved model artefacts and configuration from model_dir."""
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
    """
    Validate that each record has the required keys and non-empty values.
    Raises ValueError with a descriptive message on failure.
    """
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
    """
    Returns:
        rejection_reasons : hard-block Series (non-empty -> row excluded from inference)
        format_warnings   : soft-warning Series (non-empty -> prediction runs, KNN penalises)

    Hard block  : missing or unseen CUSTOMER, missing DEVICE_ID.
    Soft warning: numeric field width outside training distribution.
    """
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
    """Replicate training feature engineering exactly."""
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
    """
    Penalise XGBoost confidence scores based on distance from training distribution.

    Penalty formula:
        ratio    = max(0, distance - threshold) / threshold
        adjusted = conf_raw / (1 + ratio)

        distance = 0          -> adjusted = conf_raw        (no change)
        distance = threshold  -> adjusted = conf_raw / 2    (50% reduction)
        distance >> threshold -> adjusted approaches 0
    """
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
    """
    Run two-stage (Section -> Cluster) inference on a list of device records.

    Parameters
    ----------
    records   : list of dicts with keys 'device_id' and 'customer'
    pipeline  : loaded pipeline dict from load_pipeline()
    threshold : minimum adjusted confidence to emit a label (else 'UNKNOWN')

    Returns
    -------
    pd.DataFrame with columns:
        DEVICE_ID, CUSTOMER, PROJECT,
        PREDICTED_SECTION, SECTION_CONFIDENCE,
        PREDICTED_CLUSTER, CLUSTER_CONFIDENCE,
        REJECTION_REASON, FORMAT_WARNING
    """
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

        # Stage 1 — Section
        sec_proba_raw = model_section.predict_proba(X_sec)
        sec_pred_enc  = np.argmax(sec_proba_raw, axis=1)
        sec_conf_raw  = sec_proba_raw.max(axis=1)

        sec_conf_adj, _ = apply_ood_penalty(sec_conf_raw, df_feat, pipeline)

        sec_decoded = le_section.inverse_transform(sec_pred_enc)
        sec_decoded = np.where(
            (sec_decoded == SafeLabelEncoder.UNKNOWN_LABEL) |
            (sec_decoded == "__OOD__"),
            "UNKNOWN", sec_decoded,
        )

        # Stage 2 — Cluster (chained: inject predicted section)
        X_clu = (
            df_feat[[f for f in cluster_features if f != "predicted_section"]]
            .apply(pd.to_numeric, errors="coerce")
            .fillna(0)
            .copy()
        )
        X_clu["predicted_section"] = sec_pred_enc
        X_clu = X_clu[cluster_features]

        clu_proba_raw = model_cluster.predict_proba(X_clu)
        clu_pred_enc  = np.argmax(clu_proba_raw, axis=1)
        clu_conf_raw  = clu_proba_raw.max(axis=1)

        clu_conf_adj, _ = apply_ood_penalty(clu_conf_raw, df_feat, pipeline)

        clu_decoded = le_cluster.inverse_transform(clu_pred_enc)
        clu_decoded = np.where(
            (clu_decoded == SafeLabelEncoder.UNKNOWN_LABEL) |
            (clu_decoded == "__OOD__"),
            "UNKNOWN", clu_decoded,
        )

        sec_final = np.where(sec_conf_adj >= threshold, sec_decoded, "UNKNOWN")
        clu_final = np.where(clu_conf_adj >= threshold, clu_decoded, "UNKNOWN")

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
# SAVE RESULTS
# ============================================================

def save_results(result: pd.DataFrame, output_dir: str) -> None:
    """Append prediction results to the running CSV log."""
    os.makedirs(output_dir, exist_ok=True)
    result = result.copy()
    result["PREDICTED_AT"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    results_path = os.path.join(output_dir, "application_results.csv")
    result.to_csv(
        results_path,
        mode="a",
        header=not os.path.exists(results_path),
        index=False,
    )
    logger.info("Results saved to: %s", results_path)


# ============================================================
# EXPORT UNKNOWNS FOR MANUAL REVIEW
# ============================================================

def export_unknown_for_review(result: pd.DataFrame, output_dir: str) -> str | None:
    """
    Export all UNKNOWN rows (blocked or low-confidence) to Excel
    for manual assignment.

    Returns the export file path, or None if no UNKNOWNs found.
    """
    os.makedirs(output_dir, exist_ok=True)

    mask = (
        (result["REJECTION_REASON"] != "") |
        (result["PREDICTED_SECTION"] == "UNKNOWN") |
        (result["PREDICTED_CLUSTER"]  == "UNKNOWN")
    )
    unknown_df = result[mask].copy()

    if unknown_df.empty:
        logger.info("No UNKNOWN rows — nothing exported for manual assignment.")
        return None

    unknown_df["UNKNOWN_TYPE"] = unknown_df["REJECTION_REASON"].apply(
        lambda r: "BLOCKED" if r else "LOW_CONFIDENCE"
    )
    unknown_df["ASSIGNED_SECTION"] = ""
    unknown_df["ASSIGNED_CLUSTER"] = ""

    cols = ["DEVICE_ID", "CUSTOMER"]
    if "PROJECT" in unknown_df.columns:
        cols.append("PROJECT")
    cols += [
        "UNKNOWN_TYPE",
        "REJECTION_REASON",
        "FORMAT_WARNING",
        "SECTION_CONFIDENCE",
        "CLUSTER_CONFIDENCE",
        "ASSIGNED_SECTION",
        "ASSIGNED_CLUSTER",
    ]
    unknown_df = unknown_df[cols]

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    out_path  = os.path.join(output_dir, f"pending_manual_{timestamp}.xlsx")
    unknown_df.to_excel(out_path, index=False)

    logger.info(
        "%d UNKNOWN row(s) exported — LOW_CONFIDENCE: %d | BLOCKED: %d | Path: %s",
        len(unknown_df),
        (unknown_df["UNKNOWN_TYPE"] == "LOW_CONFIDENCE").sum(),
        (unknown_df["UNKNOWN_TYPE"] == "BLOCKED").sum(),
        out_path,
    )
    return out_path


# ============================================================
# MAIN — Entry point called by C#
# ============================================================

def main():
    """
    CLI entry point for C# subprocess calls.

    C# passes device_id and customer as command-line arguments.
    Python prints a single-line JSON string to stdout.
    C# reads and parses that JSON.

    Example:
        python predict_pipeline.py --device_id LL001 --customer OILTEK
        python predict_pipeline.py --device_id LL001 --customer OILTEK --project A1706
    """
    parser = argparse.ArgumentParser(description="Device cluster prediction")
    parser.add_argument("--device_id", required=True, help="Device ID to predict")
    parser.add_argument("--customer",  required=True, help="Customer name")
    parser.add_argument("--project",   default="",    help="Project code (optional)")
    args = parser.parse_args()

    try:
        pipeline  = load_pipeline(MODEL_DIR)
        records   = [{"device_id": args.device_id, "customer": args.customer, "project": args.project}]
        result_df = predict(records, pipeline, threshold=UNKNOWN_THRESHOLD)

        save_results(result_df, OUTPUT_DIR)
        export_unknown_for_review(result_df, OUTPUT_DIR)

        row    = result_df.iloc[0]
        output = {
            "status"             : "ok",
            "device_id"          : row["DEVICE_ID"],
            "customer"           : row["CUSTOMER"],
            "project"            : row["PROJECT"],
            "predicted_section"  : row["PREDICTED_SECTION"],
            "section_confidence" : row["SECTION_CONFIDENCE"],
            "predicted_cluster"  : row["PREDICTED_CLUSTER"],
            "cluster_confidence" : row["CLUSTER_CONFIDENCE"],
            "rejection_reason"   : row["REJECTION_REASON"],
            "format_warning"     : row["FORMAT_WARNING"],
        }

    except Exception as e:
        logger.error("Prediction failed: %s", str(e))
        output = {
            "status" : "error",
            "message": str(e),
        }
        print(json.dumps(output))
        sys.exit(1)

    # Single-line JSON printed to stdout — C# reads this
    print(json.dumps(output))


if __name__ == "__main__":
    main()