"""Prevent overlapping source/mode jobs without globally blocking other work."""
import inspect
import threading
from functools import wraps
from pathlib import Path
from fastapi import HTTPException

LOCK = threading.RLock()
RUNNING = []


def source_identity(kind, args, namespace):
    from services import dataset_store as store
    if kind == "annotation":
        from services.source_catalog import source_info_for
        return namespace.get("source_info_for", source_info_for)(args["source_token"])["path"], args["mode"]
    if kind == "inference":
        from services.training_jobs import get_model
        config = args["config"]
        return config["sourcePath"], namespace.get("get_model", get_model)(config["modelId"])["mode"]
    if kind == "training":
        data = namespace.get("get_snapshot", store.get_snapshot)(args["config"]["datasetId"])
        try:
            original = store.get_dataset(data["datasetId"])
        except (HTTPException, KeyError):
            original = data
    else:
        original = store.get_dataset(args["dataset_id"])
    return original.get("sourcePath") or original["directory"], original["mode"]


def guarded_start(kind):
    def decorate(function):
        signature = inspect.signature(function)
        @wraps(function)
        def wrapped(*args, **kwargs):
            bound = signature.bind(*args, **kwargs)
            bound.apply_defaults()
            source, mode = source_identity(kind, bound.arguments, function.__globals__)
            source = Path(source).expanduser().resolve()
            mode = mode.lower()
            with LOCK:
                RUNNING[:] = [entry for entry in RUNNING if entry[3].is_alive()]
                for other, other_mode, owner, thread in RUNNING:
                    if mode == other_mode and (source == other or source in other.parents or other in source.parents):
                        raise HTTPException(409, f"{owner.capitalize()} is using this source in {mode} mode. Wait or cancel that run first.")
                result = function(*args, **kwargs)
                if not isinstance(result, dict):
                    return result
                key = result.get("jobId") or result.get("id")
                thread = function.__globals__["THREADS"].get(key)
                if thread is not None:
                    RUNNING.append((source, mode, kind, thread))
                return result
        return wrapped
    return decorate
