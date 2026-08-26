"""
============================================================
DeviceCluster prediction service (FastAPI)
============================================================

Wraps predict_equipment.py and predict_sectioncluster.py in a long-running
web service instead of launching them as a subprocess per request.

Why this exists
---------------
Today PythonClient.RunAsync starts python.exe for every prediction, which
reloads ~14 MB of .pkl models each time. Here the models load ONCE when the
service starts and stay in memory, so each request is only the prediction.

Retraining then means: copy new .pkl files in, restart this service. Nothing
is redistributed to whoever is calling it.

The request/response shapes are IDENTICAL to what the scripts already accept
on stdin and return on stdout. That is deliberate — swapping the C# side from
subprocess to HTTP becomes a transport change only, with no payload changes.

Run it
------
    pip install -r requirements-service.txt
    python -m uvicorn service:app --host 127.0.0.1 --port 8000

Then open http://127.0.0.1:8000/docs to try it in a browser, no C# needed.
============================================================
"""

import os
import sys
import threading
import logging
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Dict, List, Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

# The prediction scripts resolve their config and model folders relative to
# their own __file__, so this service must sit in the same directory as them.
_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

# Importing predict_equipment loads its models at module level; importing
# predict_sectioncluster is cheap until get_pipeline() is first called.
# Both have __main__ guards, so importing does not trigger their CLI.
import predict_equipment as eq              # noqa: E402
import predict_sectioncluster as sc         # noqa: E402

logging.basicConfig(level=logging.INFO, format="%(asctime)s  %(levelname)s  %(message)s")
log = logging.getLogger("devicecluster.service")

# ------------------------------------------------------------------
# Pickle compatibility shim — REQUIRED, do not remove.
#
# pipeline_config.pkl was saved while SafeLabelEncoder was defined in the
# __main__ module, so the pickle records the class as "__main__.SafeLabelEncoder".
# That resolves fine when predict_sectioncluster.py is run directly (it IS
# __main__ then), but under uvicorn __main__ is uvicorn's own module and the
# load fails with:
#     AttributeError: Can't get attribute 'SafeLabelEncoder' on
#     <module 'uvicorn.__main__' ...>
#
# Registering the class on __main__ makes the existing .pkl files load unchanged.
# The permanent fix is to re-save the models with the class defined in an
# importable module rather than __main__ — worth doing at the next retrain.
# ------------------------------------------------------------------
import __main__  # noqa: E402

if not hasattr(__main__, "SafeLabelEncoder"):
    __main__.SafeLabelEncoder = sc.SafeLabelEncoder
    log.info("Registered SafeLabelEncoder on __main__ for pickle compatibility.")

# The scripts hold model state in module-level globals, and the manual-assign
# paths write files. Serialising access keeps concurrent requests from racing
# each other. Prediction batches here are large and infrequent, so the cost of
# the lock is irrelevant next to the safety it buys.
_lock = threading.Lock()

@asynccontextmanager
async def lifespan(_app: FastAPI):
    """
    Force the section/cluster pipeline to load now rather than on the first
    request. Without this the first caller pays the whole load cost and may
    time out, which looks like a broken service rather than a cold one.
    (predict_equipment loads its models at import, so it is already warm.)
    """
    log.info("Loading section/cluster pipeline from %s ...", sc.MODEL_DIR)
    with _lock:
        sc.get_pipeline()
    log.info("Models loaded. Service ready.")
    yield


app = FastAPI(
    title="DeviceCluster Prediction Service",
    description="Device type, section and cluster prediction. Models load once at startup.",
    version="1.0.0",
    lifespan=lifespan,
)


# ============================================================
# REQUEST SHAPES  (mirror the existing stdin payloads exactly)
# ============================================================

class DeviceTypeRequest(BaseModel):
    project_code: Optional[str] = None
    customer_code: Optional[str] = None
    data_ids: List[str] = Field(..., min_length=1)


class PipelineRecord(BaseModel):
    device_id: str
    customer: Optional[str] = None
    project: Optional[str] = None


class SectionClusterRequest(BaseModel):
    records: List[PipelineRecord] = Field(..., min_length=1)
    export_raw_csv_path: Optional[str] = None


class TopClustersRequest(BaseModel):
    device_id: str
    customer_code: Optional[str] = None
    project_code: Optional[str] = None
    top_n: int = 3


class EquipmentAssignment(BaseModel):
    data_id: str
    equipment: str


class AssignDeviceTypeRequest(BaseModel):
    project_code: Optional[str] = None
    customer: Optional[str] = None
    assignments: List[EquipmentAssignment] = Field(..., min_length=1)
    batch_results: Optional[List[Dict[str, Any]]] = None


class AssignSectionClusterRequest(BaseModel):
    device_id: str
    customer: str
    project: str
    correct_section: Optional[str] = None
    correct_cluster: Optional[str] = None


# ============================================================
# ML DEVICE IDENTIFIER — status
# ============================================================

@app.get("/ml-device-identifier")
def ml_device_identifier() -> Dict[str, Any]:
    """
    Service status: confirms the service is alive and reports WHICH model
    folders are loaded.

    Check this after retraining and restarting — it is the quickest way to
    confirm the service picked up the new models rather than still holding
    the old ones in memory.
    """
    return {
        "service": "ML DeviceIdentifier",
        "status": "ok",
        "pipeline_loaded": sc._pipeline is not None,
        "device_type_model_folder": str(eq.MODEL_FOLDER),
        "section_cluster_model_folder": str(sc.MODEL_DIR),
        "unknown_threshold": sc.UNKNOWN_THRESHOLD,
    }


# ============================================================
# PREDICTION
# ============================================================

@app.post("/predict/device-type")
def predict_device_type(req: DeviceTypeRequest) -> List[Dict[str, Any]]:
    """
    Equivalent of piping {"project_code","customer_code","data_ids"} into
    predict_equipment.py. Returns one record per device.
    """
    if len(req.data_ids) > eq.MAX_BATCH_SIZE:
        raise HTTPException(
            status_code=400,
            detail=f"data_ids length {len(req.data_ids)} exceeds maximum batch size {eq.MAX_BATCH_SIZE}.",
        )
    try:
        with _lock:
            df = eq.predict_device_types(
                req.data_ids,
                project_code=req.project_code,
                customer_code=req.customer_code,
            )
        return df.to_dict(orient="records")
    except Exception as e:
        log.exception("device-type prediction failed")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/predict/section-cluster")
def predict_section_cluster(req: SectionClusterRequest) -> List[Dict[str, Any]]:
    """
    Equivalent of piping {"records":[...]} into predict_sectioncluster.py.
    Includes TOP_CLUSTERS on each record, which the quota allocator's stage 3
    depends on.
    """
    try:
        records = [r.model_dump() for r in req.records]
        with _lock:
            pipeline = sc.get_pipeline()
            df = sc.predict(
                records,
                pipeline,
                threshold=sc.UNKNOWN_THRESHOLD,
                export_csv_path=req.export_raw_csv_path,
            )
        return sc._clean_nan(df.to_dict(orient="records"))
    except Exception as e:
        log.exception("section/cluster prediction failed")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/predict/top-clusters")
def predict_top_clusters(req: TopClustersRequest) -> Any:
    """Ranked cluster candidates for one device — used during manual correction."""
    try:
        with _lock:
            pipeline = sc.get_pipeline()
            result = sc.get_top_clusters(
                req.device_id, req.customer_code, req.project_code,
                pipeline, top_n=req.top_n,
            )
        return sc._clean_nan(result)
    except Exception as e:
        log.exception("top-clusters lookup failed")
        raise HTTPException(status_code=500, detail=str(e))


# ============================================================
# MANUAL CORRECTIONS
# ============================================================

@app.post("/assign/device-type")
def assign_device_type(req: AssignDeviceTypeRequest) -> Dict[str, Any]:
    """
    Persist user corrections to device type. Writes to the model folder with
    .bak backups, exactly as the CLI path does.
    """
    try:
        applied = []
        with _lock:
            for item in req.assignments:
                applied.append(
                    eq.manual_assign_equipment(
                        item.data_id, item.equipment,
                        project_code=req.project_code,
                        customer=req.customer,
                        batch_results=req.batch_results,
                    )
                )
            eq.persist_light_state()
        return {"status": "ok", "applied_count": len(applied), "applied": applied}
    except Exception as e:
        log.exception("device-type assignment failed")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/assign/section-cluster")
def assign_section_cluster(req: AssignSectionClusterRequest) -> Any:
    """Queue a section/cluster correction for the next retraining cycle."""
    try:
        with _lock:
            result = sc.save_manual_assign_sectioncluster(
                device_id=req.device_id,
                customer=req.customer,
                project=req.project,
                correct_section=req.correct_section,
                correct_cluster=req.correct_cluster,
            )
        return result
    except Exception as e:
        log.exception("section/cluster assignment failed")
        raise HTTPException(status_code=500, detail=str(e))


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        app,
        host=os.environ.get("SERVICE_HOST", "127.0.0.1"),
        port=int(os.environ.get("SERVICE_PORT", "8000")),
    )
