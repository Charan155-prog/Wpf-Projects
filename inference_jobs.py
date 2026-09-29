"""Durable model inference runs, serialized with the shared GPU runtime."""
import os
import re
import subprocess
import shutil
import threading
import uuid
from datetime import datetime
from pathlib import Path
from fastapi import HTTPException
from core import APP_ROOT, CONDA_PYTHON, WORK_ROOT, read_json, safe_name, source_output_directory, dataset_project_directory
from services.dataset_store import atomic_json, now, contained
from services.training_jobs import configured_root, get_model
from services.source_catalog import IMAGE_EXTENSIONS, VIDEO_EXTENSIONS
from services.model_runtime import runtime
from services.workflow_guard import guarded_start

THREADS, CANCEL = {}, {}
LOCK = threading.RLock()
ACTIVE = {"queued", "waiting", "running"}


def get_run(run_id):
    if not re.fullmatch(r"[a-f0-9]{32}", run_id): raise HTTPException(404, "Inference run not found.")
    pointer = read_json(WORK_ROOT / ".inference" / f"{run_id}.json", {})
    data = read_json(Path(pointer.get("directory", "")) / "run.json", {}) if pointer else {}
    if data.get("id") != run_id: raise HTTPException(404, "Inference run unavailable.")
    if data["state"] in ACTIVE and not THREADS.get(run_id, threading.Thread()).is_alive():
        data.update(state="interrupted", error="Backend restarted before inference completed.")
    return data


def list_runs():
    for root in {configured_root("inferenceDir", "inference"), WORK_ROOT / "inference", dataset_project_directory("storage", "detection").parent.parent}:
        for path in list(root.glob("*/*/run.json")) + list(root.glob("*/*/*/Inference Outputs/*/run.json")):
            data = read_json(path, {})
            if re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", ""))) and Path(data.get("directory", "")).resolve() == path.parent.resolve():
                pointer = WORK_ROOT / ".inference" / f"{data['id']}.json"
                if not pointer.is_file(): atomic_json(pointer, {"directory": str(path.parent)})
    values = []
    for path in (WORK_ROOT / ".inference").glob("*.json"):
        try: values.append(get_run(path.stem))
        except HTTPException: continue
    return sorted(values, key=lambda r: r["createdAt"], reverse=True)


@guarded_start("inference")
def start(config):
    model = get_model(config["modelId"])
    path = Path(config["sourcePath"]).expanduser().resolve()
    if not path.exists(): raise HTTPException(422, "Source path does not exist on the backend.")
    if path.is_dir():
        files = sorted(str(p) for p in path.rglob("*") if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS)
        source_type = "folder"
    elif path.suffix.lower() in IMAGE_EXTENSIONS:
        files, source_type = [str(path)], "image"
    elif path.suffix.lower() in VIDEO_EXTENSIONS:
        files, source_type = [], "video"
    else: raise HTTPException(422, "Choose an image folder, image, or video.")
    if source_type != "video" and not files:
        raise HTTPException(
            422,
            "No supported images were found in the selected input folder. Choose the folder containing source images, not the Inference Results Directory configured in Settings.",
        )
    if not CONDA_PYTHON.is_file(): raise HTTPException(503, "Backend Python is unavailable.")
    with LOCK:
        run_id = uuid.uuid4().hex
        # Human-friendly output label AND on-disk directory name:
        # "DD-MM-YYYY_HH-MM-SS_<image-folder/video name>_Output". Colons
        # aren't valid in Windows folder names, so the time portion uses
        # hyphens instead of colons — everything else matches what's asked
        # for, and it makes the physical folder on the backend PC (Explorer,
        # not just the in-app Run History list) readable at a glance instead
        # of a bare hex run id.
        #
        # The run id is still the durable identity used everywhere else
        # (URLs, the .inference/<run_id>.json pointer file, run.json's
        # "id" field) — only the *folder name* changes. Nothing that looks
        # runs up by id needs to know the folder name at all.
        source_label = path.stem if path.is_file() else path.name
        timestamp = datetime.now().strftime("%d-%m-%Y_%H-%M-%S")
        # Cap the source-name portion so a very long input folder/file name
        # can't push the full path past Windows' path-length limits once
        # combined with the project/output root above it.
        output_name = safe_name(f"{timestamp}_{source_label[:80]}_Output")
        source_directory = Path(model.get("sourceDirectory") or source_output_directory(model["projectName"], model["mode"], model["name"]))
        directory_root = source_directory / "Inference Outputs"
        directory = directory_root / output_name
        try:
            directory.mkdir(parents=True, exist_ok=False)
        except FileExistsError:
            # Two runs starting in the same second on the same source name —
            # keep the readable name but disambiguate with a short id suffix
            # rather than silently overwriting/reusing the first run's folder.
            output_name = f"{output_name}_{run_id[:6]}"
            directory = directory_root / output_name
            directory.mkdir(parents=True, exist_ok=False)
        data = {"id": run_id, "projectName": model["projectName"], "modelId": model["id"],
            "modelName": model.get("filename", model["name"]), "mode": model["mode"], "name": output_name,
            "directory": str(directory), "sourceDirectory": str(source_directory), "sourceType": source_type, "createdAt": now(), "state": "queued"}
        atomic_json(directory / "request.json", {"model": model, "config": config, "source": str(path), "sourceType": source_type, "files": files})
        atomic_json(directory / "run.json", data)
        atomic_json(WORK_ROOT / ".inference" / f"{run_id}.json", {"directory": str(directory)})
        event = CANCEL[run_id] = threading.Event()
        thread = THREADS[run_id] = threading.Thread(target=run, args=(data, event), daemon=True)
        thread.start()
    return data


def run(data, event):
    directory = Path(data["directory"])
    process = None
    acquired = False
    def update(**kwargs):
        data.update(**kwargs, updatedAt=now())
        atomic_json(directory / "run.json", data)
    try:
        update(state="waiting")
        if event.is_set(): update(state="cancelled"); return
        # This worker owns its CUDA process. Never evict another workflow's models.
        get_model(data["modelId"])  # recheck integrity after waiting
        update(state="running")
        with (directory / "inference.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen([str(CONDA_PYTHON), "-u", str(APP_ROOT / "ml/predict.py"), str(directory)],
                cwd=directory, stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                env={**os.environ, "SAIL_PARENT_PID": str(os.getpid()), "YOLO_AUTOINSTALL": "false"})
            while process.poll() is None:
                if event.wait(.25):
                    # Let the worker finish its current frame and finalize the
                    # video container/result manifest before stopping.
                    (directory / "cancel.request").touch(exist_ok=True)
                    try: process.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        process.terminate()
                        try: process.wait(timeout=10)
                        except subprocess.TimeoutExpired: process.kill(); process.wait()
                    break
        if event.is_set(): update(state="cancelled"); return
        result = read_json(directory / "result.json", {})
        if process.returncode or not result: raise RuntimeError("Inference failed. See the run log.")
        request = read_json(directory / "request.json", {})
        assets = result.get("assets", [])
        if not assets or result.get("processed", 0) < 1:
            raise RuntimeError("Inference produced no completed outputs.")
        if request.get("sourceType") != "video" and (len(assets) != len(request["files"]) or result["processed"] != len(request["files"])):
            raise RuntimeError("Inference did not account for every input image.")
        if any(not contained(directory, a["path"]).is_file() for a in assets):
            raise RuntimeError("An inference output is missing from disk.")
        update(state="complete", processed=result["processed"], objects=result["objects"])
    except Exception as error: update(state="failed", error=str(error))
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
        if acquired: runtime.inference_lock.release()


def detail(run_id, page=1):
    data = get_run(run_id)
    root = Path(data["directory"])
    result = read_json(root / "result.json", {})
    assets = result_assets(root)
    log = root / "inference.log"
    tail = ""
    if log.is_file():
        with log.open("rb") as f:
            f.seek(max(0, log.stat().st_size - 12000)); tail = f.read().decode("utf-8", errors="replace")
    return {**data, "progress": read_json(root / "progress.json", {}), "log": tail,
        "totalAssets": len(assets), "assets": [{**a, "url": f"/inference/runs/{run_id}/asset/{i}"}
            for i, a in enumerate(assets) if (max(1, page)-1)*24 <= i < max(1, page)*24],
        "previewUrl": f"/inference/runs/{run_id}/preview" if (root / "preview.jpg").is_file() else None}


def asset(run_id, index, playback=False):
    root = Path(get_run(run_id)["directory"])
    assets = result_assets(root)
    if not 0 <= index < len(assets): raise HTTPException(404, "Result not found.")
    return contained(root, assets[index].get("playbackPath", assets[index]["path"]) if playback else assets[index]["path"])


def result_assets(root):
    """Also recover completed JPEGs from older stopped/interrupted workers."""
    assets = read_json(root / "result.json", {}).get("assets", [])
    if assets:
        return assets
    return [{"name": path.name, "path": f"output/{path.name}", "type": "image"}
            for path in sorted((root / "output").glob("*.jpg")) if path.is_file()]


def delete_asset(run_id, index):
    with LOCK:
        data = get_run(run_id)
        if data["state"] in ACTIVE:
            raise HTTPException(409, "Stop inference before deleting an output.")
        root = Path(data["directory"])
        assets = result_assets(root)
        if not 0 <= index < len(assets): raise HTTPException(404, "Result not found.")
        item = assets.pop(index)
        for key in ("path", "playbackPath"):
            if item.get(key):
                path = contained(root, item[key])
                if path.is_file(): path.unlink()
        result = read_json(root / "result.json", {})
        result.update(assets=assets, processed=len(assets))
        atomic_json(root / "result.json", result)


def delete_run(run_id):
    with LOCK:
        data = get_run(run_id)
        if data["state"] in ACTIVE:
            raise HTTPException(409, "Stop inference before deleting this output run.")
        root = Path(data["directory"]).resolve()
        if read_json(root / "run.json", {}).get("id") != run_id:
            raise HTTPException(409, "Inference ownership could not be confirmed; it was not deleted.")
        shutil.rmtree(root)
        (WORK_ROOT / ".inference" / f"{run_id}.json").unlink(missing_ok=True)
