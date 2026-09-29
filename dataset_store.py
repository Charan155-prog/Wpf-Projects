"""Persistent annotation runs and immutable, human-validated training snapshots.

Only registry-owned paths are served. Source images are never modified. Deletion
is a reversible review exclusion; validation copies only included Pass pairs.
"""
from __future__ import annotations

import copy
import hashlib
import math
import os
import re
import shutil
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

from fastapi import HTTPException

from core import WORK_ROOT, SETTINGS_FILE, dataset_project_directory, read_json, safe_name, write_json

LOCK = threading.RLock()


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def registry() -> Path:
    return WORK_ROOT / ".datasets"


def atomic_json(path: Path, value) -> None:
    """Keep the old manifest intact while retrying transient Windows file locks."""
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        write_json(temporary, value)
        # Readers, antivirus and indexing can briefly deny replacement on Windows.
        # Never unlink/truncate the destination as a workaround: it may be the
        # only durable dataset/model registry. Permanent errors must still surface.
        for attempt in range(9):
            try:
                temporary.replace(path)
                break
            except OSError as error:
                transient = isinstance(error, PermissionError) or getattr(error, "winerror", None) in {5, 32, 33}
                if not transient or attempt == 8:
                    raise
                time.sleep(min(0.05 * (2 ** attempt), 0.5))
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass  # Cleanup must not mask the original write/replace error.


def contained(root: Path, relative: str) -> Path:
    root = root.resolve()
    target = (root / relative).resolve()
    if target == root or not target.is_relative_to(root):
        raise HTTPException(400, "Invalid dataset file path.")
    return target


def get_dataset(dataset_id: str) -> dict:
    if not re.fullmatch(r"[a-f0-9]{32}", dataset_id):
        raise HTTPException(404, "Dataset was not found.")
    pointer = read_json(registry() / f"{dataset_id}.json", {})
    if not pointer.get("directory"):
        raise HTTPException(404, "Dataset was not found.")
    data = read_json(Path(pointer["directory"]) / "dataset.json", {})
    if data.get("id") != dataset_id:
        raise HTTPException(404, "Dataset storage is unavailable. Check its saved location.")
    return data


def delete_dataset(dataset_id: str) -> None:
    """Permanently remove an owned annotation output and its validation snapshots."""
    with LOCK:
        data = get_dataset(dataset_id)
        root = Path(data["directory"]).resolve()
        manifest = root / "dataset.json"
        if read_json(manifest, {}).get("id") != dataset_id:
            raise HTTPException(409, "Dataset ownership could not be confirmed; it was not deleted.")
        snapshots = list(data.get("snapshots", []))
        shutil.rmtree(root)
        (registry() / f"{dataset_id}.json").unlink(missing_ok=True)
        for snapshot in snapshots:
            snapshot_id, directory = snapshot.get("id"), Path(snapshot.get("directory", ""))
            if re.fullmatch(r"[a-f0-9]{32}", str(snapshot_id)):
                if directory.is_dir() and read_json(directory / "dataset.json", {}).get("id") == snapshot_id:
                    shutil.rmtree(directory)
                (WORK_ROOT / ".snapshots" / f"{snapshot_id}.json").unlink(missing_ok=True)
                excluded_images_path(snapshot_id).unlink(missing_ok=True)


def save_dataset(data: dict) -> None:
    atomic_json(Path(data["directory"]) / "dataset.json", data)


def summary(data: dict) -> dict:
    items = data.get("items", [])
    included = [item for item in items if item["status"] == "Pass" and not item.get("deleted")]
    return {**{key: value for key, value in data.items() if key != "items"},
            "passCount": len(included), "failCount": sum(item["status"] == "Fail" for item in items),
            "deletedCount": sum(bool(item.get("deleted")) for item in items), "totalImages": len(items)}


def list_datasets(include_archived: bool = False) -> list[dict]:
    with LOCK:
        values = []
        for pointer in registry().glob("*.json"):
            try:
                data = get_dataset(pointer.stem)
                if include_archived or not data.get("archived"):
                    values.append(summary(data))
            except HTTPException:
                continue
        return sorted(values, key=lambda value: value["createdAt"], reverse=True)


def create_dataset(project_name: str, mode: str, source: dict, prompts: list[str]) -> dict:
    if mode not in {"detection", "segmentation"}:
        raise HTTPException(422, "Choose detection or segmentation.")
    with LOCK:
        identity = os.path.normcase(str(Path(source["path"]).resolve()))
        source_key = hashlib.sha256((source["kind"] + ":" + identity).encode()).hexdigest()[:12]
        dataset_id = uuid.uuid4().hex
        siblings = [value for value in list_datasets(True) if value["projectName"] == project_name
                    and value["mode"] == mode and value["sourceKey"] == source_key]
        revision = max((value["runNumber"] for value in siblings), default=0) + 1
        name = source["name"]
        parent = dataset_project_directory(project_name, mode).resolve()
        source_directory = contained(parent, safe_name(name))
        directory = source_directory / "Annotation"
        previous = read_json(directory / "dataset.json", {})
        # Equal display names from different sources must not collide. Unknown
        # existing folders are also never overwritten.
        if directory.exists() and previous.get("sourceKey") != source_key:
            source_directory = contained(parent, f"{safe_name(name)}--{source_key}")
            directory = source_directory / "Annotation"
            previous = read_json(directory / "dataset.json", {})
        if directory.exists():
            from services.automatic_validation import THREADS
            latest = read_json(directory / ".automatic/latest.json", {})
            if latest.get("id") in THREADS and THREADS[latest["id"]].is_alive():
                raise HTTPException(409, "Wait for automatic validation to finish or cancel it before rerunning this source.")
            if previous.get("sourceKey") != source_key or not previous.get("id"):
                raise HTTPException(409, "The source output folder already contains unregistered files. Choose another output location.")
            if previous.get("state") == "running":
                raise HTTPException(409, "This source is already being annotated.")
            archive = contained(source_directory, f"Annotation History/{previous['id']}")
            archive.parent.mkdir(parents=True, exist_ok=True)
            directory.rename(archive)
            previous.update(directory=str(archive), archived=True)
            save_dataset(previous)
            atomic_json(registry() / f"{previous['id']}.json", {"directory": str(archive)})
        directory.mkdir(parents=True, exist_ok=False)
        for child in ("Raw", "Labels", "Annotated/Pass", "Annotated/Fail"):
            (directory / child).mkdir(parents=True, exist_ok=True)
        data = {"id": dataset_id, "name": name, "projectName": project_name, "mode": mode,
                "sourceKind": source["kind"], "sourcePath": identity, "sourceKey": source_key,
                "runNumber": revision, "directory": str(directory), "classes": prompts,
                "state": "running", "createdAt": now(), "updatedAt": now(), "revision": 0,
                "items": [], "snapshots": [], "outputLayout": "unified-source-v3", "sourceDirectory": str(source_directory)}
        (directory / "classes.txt").write_text("\n".join(prompts) + "\n", encoding="utf-8")
        save_dataset(data)
        atomic_json(registry() / f"{dataset_id}.json", {"directory": str(directory)})
        return data


def record_item(dataset_id: str, item: dict) -> None:
    with LOCK:
        data = get_dataset(dataset_id)
        status = "Pass" if item.get("detections", 0) > 0 else "Fail"
        item_id = hashlib.sha256(item["filename"].encode()).hexdigest()[:24]
        entry = {"id": item_id, "filename": item["filename"], "status": status,
                 "raw": "Raw/" + item["filename"],
                 "annotated": f"Annotated/{status}/" + Path(item["annotated"]).name,
                 "label": "Labels/" + Path(item["label"]).name,
                 "detections": item.get("detections", 0), "error": item.get("error"), "deleted": False, "edited": False}
        data["items"] = [value for value in data["items"] if value["id"] != item_id] + [entry]
        data["revision"] += 1
        data["updatedAt"] = now()
        save_dataset(data)


def finish_dataset(dataset_id: str | None, state: str) -> None:
    if not dataset_id:
        return
    with LOCK:
        data = get_dataset(dataset_id)
        data["state"] = state
        data["updatedAt"] = now()
        save_dataset(data)


def require_editable(data: dict, revision: int) -> None:
    if data.get("archived"):
        raise HTTPException(409, "This run has been archived. Open the current source folder to review it.")
    if data["state"] == "running":
        raise HTTPException(409, "Wait for annotation to finish before reviewing this dataset.")
    if revision != data["revision"]:
        raise HTTPException(409, "The dataset changed. Refresh it before saving your review.")


def get_item(data: dict, item_id: str) -> dict:
    item = next((value for value in data["items"] if value["id"] == item_id), None)
    if not item:
        raise HTTPException(404, "Dataset image was not found.")
    return item


def public_item(data: dict, item: dict) -> dict:
    base = f"/datasets/{data['id']}/images/{item['id']}"
    return {**item, "annotatedUrl": f"{base}/file/annotated?v={data['revision']}",
            "rawUrl": f"{base}/file/raw", "labelUrl": f"{base}/file/label?v={data['revision']}"}


def set_deleted(dataset_id: str, item_id: str, deleted: bool, revision: int) -> dict:
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        item = get_item(data, item_id)
        item["deleted"] = deleted
        data["revision"] += 1
        data["updatedAt"] = now()
        save_dataset(data)
        return summary(data)


def validate_labels(path: Path, mode: str, class_count: int) -> None:
    lines = path.read_text(encoding="utf-8-sig").strip().splitlines()
    if not lines:
        raise ValueError("empty label file")
    for line in lines:
        parts = line.split()
        class_id = int(parts[0])
        coordinates = [float(value) for value in parts[1:]]
        if not 0 <= class_id < class_count or any(not math.isfinite(value) or not 0 <= value <= 1 for value in coordinates):
            raise ValueError("invalid class ID or normalized coordinates")
        if mode == "detection":
            if len(coordinates) != 4 or min(coordinates[2:]) <= 0:
                raise ValueError("detection labels need class cx cy width height")
        elif len(coordinates) < 6 or len(coordinates) % 2:
            raise ValueError("segmentation labels need class and at least three polygon points")


def validate_dataset(dataset_id: str, revision: int, accepted_ids: set[str] | None = None, report_id: str | None = None) -> dict:
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        if data["state"] != "complete":
            raise HTTPException(409, "Only a completed annotation run can be validated.")
        if not data["classes"]:
            raise HTTPException(422, "Class mapping is missing. Restore the original classes.txt and refresh Datasets, or rerun annotation before validating.")
        existing = next((value for value in data["snapshots"] if value["revision"] == revision and value.get("reportId") == report_id), None)
        if existing:
            return existing
        items = [item for item in data["items"] if item["status"] == "Pass" and not item.get("deleted")]
        if accepted_ids is not None:
            items = [item for item in items if item["id"] in accepted_ids]
        if not items:
            raise HTTPException(422, "No included Pass images remain to validate.")
        source = Path(data["directory"])
        for item in items:
            if not contained(source, item["raw"]).is_file() or not contained(source, item["label"]).is_file():
                raise HTTPException(422, f"Missing raw image or label: {item['filename']}")
            try:
                validate_labels(contained(source, item["label"]), data["mode"], len(data["classes"]))
            except (OSError, ValueError, IndexError) as error:
                raise HTTPException(422, f"Invalid {data['mode']} label for {item['filename']}: {error}") from error
        from core import source_output_directory
        source_directory = Path(data.get("sourceDirectory") or source_output_directory(data["projectName"], data["mode"], data["name"]))
        snapshot_id = uuid.uuid4().hex
        target = (source_directory / "Validated Datasets" / snapshot_id).resolve()
        staging = target.with_name(".building-" + snapshot_id)
        # Incomplete copies are deliberately not registered or offered to Training.
        (staging / "images").mkdir(parents=True, exist_ok=False)
        (staging / "labels").mkdir()
        pairs = []
        for item in items:
            raw = contained(source, item["raw"])
            label = contained(source, item["label"])
            shutil.copy2(raw, staging / "images" / raw.name)
            label_name = raw.stem + ".txt"
            shutil.copy2(label, staging / "labels" / label_name)
            pairs.append({"image": f"images/{raw.name}", "label": f"labels/{label_name}",
                          "imageSha256": file_hash(staging / "images" / raw.name),
                          "labelSha256": file_hash(staging / "labels" / label_name)})
        snapshot = {"id": snapshot_id, "datasetId": dataset_id, "name": data["name"],
                    "projectName": data["projectName"], "mode": data["mode"], "classes": data["classes"],
                    "runNumber": data["runNumber"], "revision": revision, "createdAt": now(),
                    "imageCount": len(pairs), "directory": str(target), "sourceDirectory": str(source_directory),
                    "validationMethod": "automatic" if report_id else "manual", "reportId": report_id,
                    "imagesDirectory": str(target / "images"), "labelsDirectory": str(target / "labels"),
                    "labelFormat": "YOLO detection" if data["mode"] == "detection" else "YOLO segmentation"}
        write_json(staging / "dataset.json", {**snapshot, "pairs": pairs})
        (staging / "classes.txt").write_text("\n".join(data["classes"]) + "\n", encoding="utf-8")
        staging.rename(target)
        data["snapshots"].append(snapshot)
        atomic_json(WORK_ROOT / ".snapshots" / f"{snapshot_id}.json", {"directory": str(target)})
        data["updatedAt"] = now()
        save_dataset(data)
        return snapshot


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


# ─── Manual validation job (cancellable background check) ──────────────────
# Images are checked sequentially (existence + label sanity) so progress can
# be reported and a run can be stopped early. Whatever was already checked
# successfully is still exported via validate_dataset above, which stays
# synchronous and is also used directly by Automatic's own export step.

MANUAL_THREADS: dict = {}
MANUAL_CANCEL: dict = {}


def manual_job_location(data: dict, job_id: str) -> Path:
    if not re.fullmatch(r"[a-f0-9]{32}", job_id):
        raise HTTPException(404, "Manual validation job not found.")
    return contained(Path(data["directory"]), f".manual/{job_id}")


def manual_validation_status(dataset_id: str) -> dict:
    with LOCK:
        data = get_dataset(dataset_id)
        pointer = read_json(Path(data["directory"]) / ".manual/latest.json", {})
        if not pointer:
            return {"job": None}
        root = manual_job_location(data, pointer["id"])
        job = read_json(root / "status.json", {})
        if not job:
            return {"job": None}
        if job.get("state") == "running" and not MANUAL_THREADS.get(job["id"], threading.Thread()).is_alive():
            job["state"] = "interrupted"
            atomic_json(root / "status.json", job)
        return {"job": job}


def start_manual_validation(dataset_id: str, revision: int) -> dict:
    from services.annotation_jobs import active_job
    from services import automatic_validation
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        if data["state"] != "complete" or not data["classes"]:
            raise HTTPException(409, "A completed dataset with its class mapping is required.")
        if any(thread.is_alive() for thread in MANUAL_THREADS.values()):
            raise HTTPException(409, "A validation run is already in progress.")
        if active_job() or any(thread.is_alive() for thread in automatic_validation.THREADS.values()):
            raise HTTPException(409, "Wait for the active annotation or automatic review job to finish.")
        items = [item for item in data["items"] if item["status"] == "Pass" and not item.get("deleted")]
        if not items:
            raise HTTPException(422, "No included Pass images remain to validate.")
        job_id = uuid.uuid4().hex
        root = manual_job_location(data, job_id)
        root.mkdir(parents=True)
        job = {"id": job_id, "revision": revision, "state": "running", "total": len(items),
               "completed": 0, "createdAt": now(), "invalid": [], "snapshot": None, "error": None}
        atomic_json(root / "status.json", job)
        atomic_json(root.parent / "latest.json", {"id": job_id})
        event = threading.Event(); MANUAL_CANCEL[job_id] = event
        thread = threading.Thread(target=_run_manual_validation, args=(dataset_id, copy.deepcopy(data), items, root, job, event, revision), daemon=True)
        MANUAL_THREADS[job_id] = thread; thread.start()
        return {"job": job}


def _run_manual_validation(dataset_id: str, data: dict, items: list, root: Path, job: dict, event: threading.Event, revision: int) -> None:
    source = Path(data["directory"])
    accepted = []
    try:
        for item in items:
            if event.is_set():
                job["state"] = "cancelled"; break
            with LOCK:
                current = get_dataset(dataset_id)
                if current["revision"] != revision or current.get("archived"):
                    job["state"] = "stale"; break
            raw = contained(source, item["raw"])
            label = contained(source, item["label"])
            problem = None
            if not raw.is_file() or not label.is_file():
                problem = "Missing raw image or label"
            else:
                try:
                    validate_labels(label, data["mode"], len(data["classes"]))
                except (OSError, ValueError, IndexError) as error:
                    problem = str(error)
            if problem:
                job["invalid"].append({"filename": item["filename"], "reason": problem})
            else:
                accepted.append(item["id"])
            job["completed"] += 1
            atomic_json(root / "status.json", job)
        else:
            job["state"] = "complete"
    except Exception as error:
        job.update(state="failed", error=str(error)[:500])
    finally:
        if job["state"] in ("complete", "cancelled") and accepted:
            try:
                job["snapshot"] = validate_dataset(dataset_id, revision, accepted_ids=set(accepted))
            except HTTPException as error:
                job["state"] = "failed"; job["error"] = str(error.detail)
        job["finishedAt"] = now()
        atomic_json(root / "status.json", job)


def cancel_manual_validation(dataset_id: str) -> dict:
    result = manual_validation_status(dataset_id)
    job = result.get("job")
    if job and job.get("state") == "running" and job["id"] in MANUAL_CANCEL:
        MANUAL_CANCEL[job["id"]].set()
    return {"cancelRequested": True}


def training_datasets() -> list[dict]:
    # Snapshots outlive the annotation source/run that created them.
    values = {s["id"]: s for data in list_datasets(True) for s in data["snapshots"]}
    settings = read_json(SETTINGS_FILE, {})
    roots = {WORK_ROOT / "validated-datasets"}
    if settings.get("validatedRoot"):
        roots.add(Path(os.path.expandvars(os.path.expanduser(settings["validatedRoot"]))))
    paths = [p for root in roots for p in root.glob("*/*/*/*/dataset.json")]
    paths += list(dataset_project_directory("storage", "detection").parent.parent.glob("*/*/*/Validated Datasets/*/dataset.json"))
    paths += [Path(read_json(p, {}).get("directory", "")) / "dataset.json"
              for p in (WORK_ROOT / ".snapshots").glob("*.json")]
    for path in paths:
        data = read_json(path, {})
        if data.get("pairs") and re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", ""))):
            # Validated snapshots are self-contained. If their storage root was
            # moved (or the configured root changed), repair only the manifest's
            # location fields; do not alter the immutable image/label pairs.
            actual = path.parent.resolve()
            if Path(data.get("directory", "")).resolve() != actual:
                data.update(directory=str(actual), imagesDirectory=str(actual / "images"), labelsDirectory=str(actual / "labels"))
                atomic_json(path, data)
            values[data["id"]] = {k: v for k, v in data.items() if k != "pairs"}
            pointer = WORK_ROOT / ".snapshots" / f"{data['id']}.json"
            if read_json(pointer, {}).get("directory") != str(actual):
                atomic_json(pointer, {"directory": str(actual)})
    return sorted([s for s in values.values() if (Path(s["directory"]) / "dataset.json").is_file()],
                  key=lambda s: s["createdAt"], reverse=True)


def get_snapshot(snapshot_id: str) -> dict:
    snapshot = next((value for value in training_datasets() if value["id"] == snapshot_id), None)
    if not snapshot:
        raise HTTPException(404, "Validated training dataset was not found.")
    return read_json(Path(snapshot["directory"]) / "dataset.json", {})


def delete_snapshot(snapshot_id: str) -> None:
    """Permanently remove a validated raw-image/label snapshot only."""
    with LOCK:
        snapshot = get_snapshot(snapshot_id)
        directory = Path(snapshot["directory"]).resolve()
        if read_json(directory / "dataset.json", {}).get("id") != snapshot_id:
            raise HTTPException(409, "Training dataset ownership could not be confirmed; it was not deleted.")
        shutil.rmtree(directory)
        (WORK_ROOT / ".snapshots" / f"{snapshot_id}.json").unlink(missing_ok=True)
        excluded_images_path(snapshot_id).unlink(missing_ok=True)


def excluded_images_path(snapshot_id: str) -> Path:
    return WORK_ROOT / ".training" / "excluded" / f"{snapshot_id}.json"


def get_excluded_images(snapshot_id: str) -> list[str]:
    """Filenames the user has excluded from future training runs of this validated dataset."""
    get_snapshot(snapshot_id)  # 404s on an unknown/invalid dataset id
    return sorted(read_json(excluded_images_path(snapshot_id), {}).get("excludedImages", []))


def set_excluded_images(snapshot_id: str, excluded_images: list) -> list[str]:
    get_snapshot(snapshot_id)
    valid = {Path(pair["image"]).name for pair in get_snapshot(snapshot_id)["pairs"]}
    cleaned = sorted({str(name).strip() for name in excluded_images if str(name).strip()} & valid)
    atomic_json(excluded_images_path(snapshot_id), {"excludedImages": cleaned, "updatedAt": now()})
    return cleaned


def check_snapshot(snapshot_id: str) -> dict:
    snapshot = get_snapshot(snapshot_id)
    root = Path(snapshot["directory"])
    for pair in snapshot["pairs"]:
        for key, hash_key in (("image", "imageSha256"), ("label", "labelSha256")):
            path = contained(root, pair[key])
            if not path.is_file() or file_hash(path) != pair[hash_key]:
                raise HTTPException(409, f"Training file is missing or changed: {pair[key]}. Revalidate the source dataset.")
    return {"verified": True, "imageCount": len(snapshot["pairs"]), "mode": snapshot["mode"]}


def register_existing_outputs() -> None:
    """Index the surviving pre-validation layout without moving/deleting files.

    Older versions overwrote project outputs, so only the most recent completed
    job for each directory can describe the files that still exist there.
    """
    with LOCK:
        for data in list_datasets():
            if data["state"] == "running":
                finish_dataset(data["id"], "interrupted")
        registered = {str(Path(value["directory"]).resolve()) for value in list_datasets()}
        jobs = WORK_ROOT / ".jobs"
        if not jobs.is_dir():
            discover_outputs()
            for data in list_datasets():
                if data["state"] == "running":
                    finish_dataset(data["id"], "interrupted")
            return
        candidates = sorted(jobs.glob("*/job.json"), key=lambda path: path.stat().st_mtime, reverse=True)
        for metadata_file in candidates:
            metadata = read_json(metadata_file, {})
            if metadata.get("datasetId") or metadata.get("preAnnotation") or metadata.get("previewOnly"):
                continue
            if not metadata.get("projectDirectory"):
                continue
            root = Path(metadata["projectDirectory"]).resolve()
            if str(root) in registered or (root / "dataset.json").exists() or not (root / "raw").is_dir():
                continue
            progress = read_json(metadata_file.parent / "progress.json", {})
            if progress.get("state") != "complete":
                continue
            request = read_json(metadata_file.parent / "request.json", {})
            source_files = request.get("source_files", [])
            source = Path(request["source_video"]) if request.get("source_video") else (Path(source_files[0]).parent if source_files else root)
            mode = metadata.get("mode", "detection")
            if mode not in {"detection", "segmentation"}:
                continue
            items = []
            for raw in sorted((root / "raw").iterdir()):
                if raw.suffix.lower() not in {".jpg", ".jpeg", ".png", ".webp", ".bmp"}:
                    continue
                label = root / "labels" / f"{raw.stem}.txt"
                for status in ("Pass", "Fail"):
                    annotated = root / "annotated" / status / f"{raw.stem}_annotated.jpg"
                    if label.is_file() and annotated.is_file():
                        items.append({"id": hashlib.sha256(raw.name.encode()).hexdigest()[:24],
                            "filename": raw.name, "raw": raw.relative_to(root).as_posix(),
                            "label": label.relative_to(root).as_posix(), "annotated": annotated.relative_to(root).as_posix(),
                            "status": status, "detections": len(label.read_text(encoding="utf-8-sig").strip().splitlines()),
                            "deleted": False, "edited": False})
                        break
            if not items:
                continue
            dataset_id = uuid.uuid4().hex
            project_name = next((parent.parent.name for parent in root.parents if parent.name.lower() == mode), root.parent.name)
            data = {"id": dataset_id, "name": source.name, "projectName": project_name,
                    "mode": mode, "sourceKind": "video" if request.get("source_video") else "folder",
                    "sourcePath": str(source), "sourceKey": hashlib.sha256(str(source).encode()).hexdigest()[:12],
                    "runNumber": 1, "directory": str(root), "classes": metadata.get("prompts", []),
                    "state": "complete", "createdAt": now(), "updatedAt": now(), "revision": 0,
                    "items": items, "snapshots": [], "legacy": True}
            save_dataset(data)
            atomic_json(registry() / f"{dataset_id}.json", {"directory": str(root)})
            registered.add(str(root))
        discover_outputs()
        for data in list_datasets():
            if data["state"] == "running":
                finish_dataset(data["id"], "interrupted")


def discover_outputs() -> None:
    """Recover the index from saved output folders, including copied datasets.

    Scan only the documented layout and the preceding run-folder layout; never
    recurse through raw images, model caches or training snapshots.
    """
    configured = str(read_json(SETTINGS_FILE, {}).get("datasetRoot", "")).strip()
    base = Path(os.path.expandvars(os.path.expanduser(configured))) if configured else WORK_ROOT / "datasets"
    with LOCK:
        candidates = set(base.glob("*/*/*/dataset.json"))
        candidates.update(base.glob("*/*/*/Annotation/dataset.json"))
        candidates.update(base.glob("*/*/*/Annotation History/*/dataset.json"))
        candidates.update(base.glob("*/*/*/run-*/dataset.json"))
        candidates.update(base.glob("*/*/.history/*/*/dataset.json"))
        for manifest in sorted(candidates):
            root = manifest.parent.resolve()
            data = read_json(manifest, {})
            if not re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", ""))):
                continue
            if not {"items", "classes", "mode", "sourceKey", "createdAt", "snapshots"}.issubset(data):
                continue
            # An existing live registry location wins over a duplicate copy.
            pointer = read_json(registry() / f"{data['id']}.json", {})
            existing = Path(pointer["directory"]) if pointer.get("directory") else None
            if existing and existing.resolve() != root and (existing / "dataset.json").is_file():
                continue
            if data.get("directory") != str(root):
                data["directory"] = str(root)
                save_dataset(data)
            if data.get("legacy") and not data["classes"] and (root / "classes.txt").is_file():
                data["classes"] = (root / "classes.txt").read_text(encoding="utf-8-sig").splitlines()
                data["revision"] += 1
                save_dataset(data)
            atomic_json(registry() / f"{data['id']}.json", {"directory": str(root)})
        # Plain source folders with outputs but no manifest can still be reviewed.
        for root in list(base.glob("*/*/*")) + list(base.glob("*/*/*/Annotation")):
            source_root = root.parent if root.name == "Annotation" else root
            if not root.is_dir() or root.name.startswith(".") or source_root.parent.name.lower() not in {"detection", "segmentation"}:
                continue
            if (root / "dataset.json").exists():
                continue
            children = {child.name.lower(): child for child in root.iterdir() if child.is_dir()}
            if not {"raw", "annotated", "labels"}.issubset(children):
                continue
            items = []
            for raw in sorted(children["raw"].iterdir()):
                if not raw.is_file() or raw.suffix.lower() not in {".jpg", ".jpeg", ".png", ".bmp", ".webp"}:
                    continue
                label = children["labels"] / f"{raw.stem}.txt"
                for status in ("Pass", "Fail"):
                    annotated = children["annotated"] / status / f"{raw.stem}_annotated.jpg"
                    if annotated.is_file():
                        items.append({"id": hashlib.sha256(raw.name.encode()).hexdigest()[:24], "filename": raw.name,
                            "raw": raw.relative_to(root).as_posix(), "label": label.relative_to(root).as_posix(),
                            "annotated": annotated.relative_to(root).as_posix(), "status": status,
                            "detections": 0, "deleted": False, "edited": False})
                        break
            classes_file = root / "classes.txt"
            classes = classes_file.read_text(encoding="utf-8-sig").splitlines() if classes_file.is_file() else []
            data = {"id": uuid.uuid4().hex, "name": source_root.name, "projectName": source_root.parent.parent.name,
                "mode": source_root.parent.name.lower(), "sourceKind": "folder", "sourcePath": str(root.resolve()),
                "sourceKey": hashlib.sha256(str(root.resolve()).encode()).hexdigest()[:12],
                "runNumber": 1, "directory": str(root.resolve()), "classes": classes, "state": "complete",
                "createdAt": now(), "updatedAt": now(), "revision": 0, "items": items, "snapshots": [], "legacy": True}
            save_dataset(data)
            atomic_json(registry() / f"{data['id']}.json", {"directory": data["directory"]})
