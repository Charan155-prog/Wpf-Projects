"""Persistent, cancellable training jobs. One model process at a time per API host."""
import os
import re
import shutil
import subprocess
import threading
import uuid
from pathlib import Path

from fastapi import HTTPException
from core import APP_ROOT, CONDA_PYTHON, SETTINGS_FILE, WORK_ROOT, read_json, safe_name, source_output_directory, dataset_project_directory
from services.dataset_store import atomic_json, file_hash, get_snapshot, now
from services.model_runtime import runtime
from services.workflow_guard import guarded_start
from services.training_split import split_dataset

LOCK = threading.RLock()
THREADS, CANCEL = {}, {}
ACTIVE = {"queued", "splitting", "waiting", "training", "testing"}


def configured_root(key, default):
    value = str(read_json(SETTINGS_FILE, {}).get(key, "")).strip()
    return Path(os.path.expandvars(os.path.expanduser(value))).resolve() if value else WORK_ROOT / default


def get_run(run_id):
    if not re.fullmatch(r"[a-f0-9]{32}", run_id):
        raise HTTPException(404, "Training run not found.")
    pointer = read_json(WORK_ROOT / ".training" / f"{run_id}.json", {})
    if not pointer:
        raise HTTPException(404, "Training run not found.")
    data = read_json(Path(pointer["directory"]) / "run.json", {})
    if not data:
        raise HTTPException(404, "Training storage unavailable.")
    if data["state"] in ACTIVE and not THREADS.get(run_id, threading.Thread()).is_alive():
        data.update(state="interrupted", error="API restarted; this run is not considered a completed model.")
    return data


def list_runs():
    # Recover pointers from durable run manifests, including after a source-code copy.
    for root in {configured_root("modelsDir", "models"), WORK_ROOT / "models", dataset_project_directory("storage", "detection").parent.parent}:
        for path in list(root.glob("*/*/*/run.json")) + list(root.glob("*/*/*/Trained Models/*/run.json")):
            data = read_json(path, {})
            if re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", ""))):
                actual = path.parent.resolve()
                changed = Path(data.get("directory", "")).resolve() != actual
                data["directory"] = str(actual)
                model = data.get("model")
                if isinstance(model, dict) and model.get("filename"):
                    checkpoint = actual / model["filename"]
                    # Rebase only an owned model checkpoint and only when its
                    # recorded checksum still matches. Foreign .pt files are
                    # never automatically registered.
                    if checkpoint.is_file() and model.get("sha256") and file_hash(checkpoint) == model["sha256"] and model.get("path") != str(checkpoint):
                        model = {**model, "path": str(checkpoint)}
                        data["model"] = model
                        changed = True
                if changed:
                    atomic_json(path, data)
                pointer = WORK_ROOT / ".training" / f"{data['id']}.json"
                if read_json(pointer, {}).get("directory") != str(actual):
                    atomic_json(pointer, {"directory": str(actual)})
    runs = []
    for p in (WORK_ROOT / ".training").glob("*.json"):
        try: runs.append(get_run(p.stem))
        except (HTTPException, KeyError, OSError): continue
    return sorted(runs, key=lambda r: r["createdAt"], reverse=True)


def list_models():
    return [r["model"] for r in list_runs() if r["state"] == "complete" and r.get("model") and Path(r["model"]["path"]).is_file()]


def get_model(model_id):
    model = next((m for m in list_models() if m["id"] == model_id), None)
    if not model:
        raise HTTPException(404, "Completed model not found.")
    if file_hash(Path(model["path"])) != model["sha256"]:
        raise HTTPException(409, "Checkpoint changed since registration; refusing to load it.")
    return model


def delete_model(model_id):
    """Permanently remove the completed training run that owns a model."""
    with LOCK:
        for run in list_runs():
            if run.get("model", {}).get("id") != model_id:
                continue
            if run["state"] in ACTIVE:
                raise HTTPException(409, "A model belonging to an active training run cannot be deleted.")
            directory = Path(run["directory"]).resolve()
            if read_json(directory / "run.json", {}).get("id") != run["id"]:
                raise HTTPException(409, "Model ownership could not be confirmed; it was not deleted.")
            shutil.rmtree(directory)
            (WORK_ROOT / ".training" / f"{run['id']}.json").unlink(missing_ok=True)
            return
    raise HTTPException(404, "Completed model not found.")


@guarded_start("training")
def start(config):
    snapshot = get_snapshot(config["datasetId"])
    excluded = {Path(name).name for name in config.get("excludedImages", []) if str(name).strip()}
    if excluded:
        snapshot = {**snapshot, "pairs": [pair for pair in snapshot.get("pairs", [])
                                             if Path(pair["image"]).name not in excluded]}
        snapshot["imageCount"] = len(snapshot["pairs"])
    if len(snapshot.get("pairs", [])) < 3:
        raise HTTPException(422, "At least three validated images are required.")
    source = config["architecture"].lower() + ("-seg" if snapshot["mode"] == "segmentation" else "")
    source += ".pt" if config["pretrained"] else ".yaml"
    local_pretrained = configured_root("modelsDir", "models") / "pretrained" / source
    if config["pretrained"] and local_pretrained.is_file():
        source = str(local_pretrained)
    if config["mode"] == "incremental":
        base = get_model(config["baseModelId"])
        if base["mode"] != snapshot["mode"] or base["classes"] != snapshot["classes"] or base["projectName"] != snapshot["projectName"]:
            raise HTTPException(422, "Base model must match project, task and exact ordered class mapping.")
        source = base["path"]
        config = {**config, "architecture": base["config"]["architecture"], "pretrained": True}
    if not CONDA_PYTHON.is_file():
        raise HTTPException(503, "Training Python is unavailable. Configure SAIL_CONDA_PYTHON.")
    with LOCK:
        run_id = uuid.uuid4().hex
        source_directory = Path(snapshot.get("sourceDirectory") or source_output_directory(snapshot["projectName"], snapshot["mode"], snapshot["name"]))
        directory = source_directory / "Trained Models" / run_id
        directory.mkdir(parents=True, exist_ok=False)
        data = {"id": run_id, "datasetId": snapshot["id"], "name": snapshot["name"],
                "projectName": snapshot["projectName"], "mode": snapshot["mode"], "directory": str(directory),
                "createdAt": now(), "state": "queued", "config": config, "sourceDirectory": str(source_directory)}
        atomic_json(directory / "run.json", data)
        atomic_json(directory / "request.json", {"config": config, "snapshot": snapshot, "modelSource": source})
        atomic_json(WORK_ROOT / ".training" / f"{run_id}.json", {"directory": str(directory)})
        CANCEL[run_id] = threading.Event()
        thread = threading.Thread(target=run, args=(data, snapshot), daemon=True, name=f"training-{run_id}")
        THREADS[run_id] = thread
        thread.start()
    return data


def run(data, snapshot):
    directory = Path(data["directory"])
    event = CANCEL[data["id"]]
    process = None
    acquired = False
    def update(**values):
        data.update(**values, updatedAt=now())
        atomic_json(directory / "run.json", data)
    try:
        update(state="splitting")
        split = split_dataset(snapshot, directory / "split", data["config"]["ratios"], data["config"]["seed"], event)
        exposed = set()
        if data["config"]["mode"] == "incremental":
            base = get_model(data["config"]["baseModelId"])
            exposed.update(base.get("seenImageHashes", []))
            if not base.get("seenImageHashes"):
                raise ValueError("Base model has no training-exposure record. Use a model created by this training pipeline.")
            if any(p["imageSha256"] in exposed for p in split["pairs"] if p["split"] == "test"):
                raise ValueError("Test split overlaps images used to train/select the base model. Use an independent holdout dataset or the original split seed.")
        exposed.update(p["imageSha256"] for p in split["pairs"] if p["split"] != "test")
        counts = {name: [0] * len(snapshot["classes"]) for name in ("train", "val", "test")}
        for pair in split["pairs"]:
            label = directory / "split/labels" / pair["split"] / (Path(pair["image"]).stem + ".txt")
            for row in label.read_text(encoding="utf-8-sig").splitlines():
                if row.strip(): counts[pair["split"]][int(row.split()[0])] += 1
        warnings = [f"No training instances for class {i}: {name}. Add examples or change the split seed." for i, name in enumerate(snapshot["classes"]) if not counts["train"][i]]
        if min(split["counts"].values()) < 10: warnings.append("One or more splits contain fewer than 10 images; evaluation metrics may be unreliable.")
        update(state="waiting", splitCounts=split["counts"], classCounts=counts, warnings=warnings)
        if event.is_set():
            update(state="cancelled")
            return
        # Training owns a separate process; retain models used by other workflows.
        update(state="training")
        with (directory / "train.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen([str(CONDA_PYTHON), "-u", str(APP_ROOT / "ml" / "train.py"), str(directory)],
                cwd=directory, stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                env={**os.environ, "PYTHONUNBUFFERED": "1", "YOLO_AUTOINSTALL": "false", "SAIL_PARENT_PID": str(os.getpid())})
            while process.poll() is None:
                if event.wait(0.3):
                    process.terminate()
                    try: process.wait(timeout=10)
                    except subprocess.TimeoutExpired: process.kill(); process.wait()
                    break
        if event.is_set():
            update(state="cancelled")
            return
        result = read_json(directory / "result.json", {})
        if process.returncode != 0 or not result:
            raise RuntimeError("Training failed. See the training log (dependencies, device, memory and dataset details).")
        best = Path(result["best"]).resolve()
        if not best.is_relative_to(directory.resolve()) or best.name != "best.pt" or not best.is_file():
            raise RuntimeError("Valid best.pt was not produced.")
        filename = f"{safe_name(data['name'])}_{snapshot['mode']}_{data['id'][:8]}_best.pt"
        named_best = directory / filename
        shutil.copy2(best, named_best)
        model = {"id": data["id"], "name": data["name"], "projectName": data["projectName"],
                 "mode": snapshot["mode"], "classes": snapshot["classes"], "datasetId": snapshot["id"], "sourceDirectory": data.get("sourceDirectory"),
                 "path": str(named_best), "filename": filename, "sha256": file_hash(named_best), "createdAt": now(),
                 "config": data["config"], "splitCounts": split["counts"], "seenImageHashes": sorted(exposed), **result}
        atomic_json(directory / "model.json", model)
        update(state="complete", model=model)
    except Exception as error:
        update(state="cancelled" if event.is_set() else "failed", error=str(error))
    finally:
        if process is not None and process.poll() is None:
            process.terminate(); process.wait()
        if acquired: runtime.inference_lock.release()


def detail(run_id):
    data = get_run(run_id)
    directory = Path(data["directory"])
    log = directory / "train.log"
    tail = ""
    if log.is_file():
        with log.open("rb") as stream:
            stream.seek(max(0, log.stat().st_size - 24000))
            tail = stream.read().decode("utf-8", errors="replace")
    return {**public_run(data), "progress": read_json(directory / "progress.json", {}), "log": tail}


def public_model(model):
    return {key: value for key, value in model.items() if key != "seenImageHashes"}


def public_run(data):
    return {**data, **({"model": public_model(data["model"])} if data.get("model") else {})}


def cancel(run_id):
    data = get_run(run_id)
    if data["state"] in ACTIVE and run_id in CANCEL: CANCEL[run_id].set()
    return {"requested": data["state"] in ACTIVE}
