from __future__ import annotations

import shutil
import threading
import traceback
import uuid
import time
from pathlib import Path

import cv2
import numpy as np

from fastapi import HTTPException, UploadFile

from core import LOCAL_SAM3_ENABLED, WORK_ROOT, dataset_project_directory, load_prompt_library, logger, read_json, safe_name, save_prompt_library, write_json
from services.model_runtime import runtime
from services.source_catalog import direct_video_for, files_for, source_info_for, output_names_for
from services.dataset_store import create_dataset, finish_dataset
from services.video_encoding import browser_video_size, normalize_video_frame, open_browser_video_writer
from services.dataset_store import atomic_json
from services.workflow_guard import guarded_start

THREADS: dict[str, threading.Thread] = {}
CANCEL_EVENTS: dict[str, threading.Event] = {}
LIVE_DISPLAYS = {}


def display_connection(job_id, connected):
    state = LIVE_DISPLAYS.get(job_id)
    if state:
        with state["condition"]:
            state["clients"] = max(0, state["clients"] + (1 if connected else -1))
            state["connected_once"] = state["connected_once"] or connected
            state["condition"].notify_all()


def acknowledge_display(job_id, completed):
    state = LIVE_DISPLAYS.get(job_id)
    if state:
        with state["condition"]:
            state["ack"] = max(state["ack"], min(int(completed), state["produced"]))
            state["condition"].notify_all()


def wait_for_display(job_id, completed, cancelled):
    state = LIVE_DISPLAYS.get(job_id)
    if not state:
        return
    # A disconnected/closed browser must not strand a durable backend job.
    deadline = time.monotonic() + 30
    with state["condition"]:
        state["produced"] = completed
        while state["ack"] < completed and not cancelled.is_set():
            if state["connected_once"] and not state["clients"]:
                return
            if time.monotonic() >= deadline:
                # Do not repeat the initial connection grace period for every
                # image when a client never opened the stream.
                if not state["clients"]:
                    state["connected_once"] = True
                return
            state["condition"].wait(.1)


def _job_dir(job_id: str) -> Path:
    return WORK_ROOT / ".jobs" / safe_name(job_id)


def _write_job_manifest(path: Path, value) -> None:
    """Progress telemetry must not be able to terminate a long annotation job."""
    try:
        atomic_json(path, value)
    except OSError as error:
        logger.warning("could not update annotation progress file %s: %s", path.name, error)


def _write_progress(job_dir: Path, value) -> None:
    _write_job_manifest(job_dir / "progress.json", value)


def active_job() -> str | None:
    jobs_dir = WORK_ROOT / ".jobs"
    if not jobs_dir.exists():
        return None
    for job_dir in jobs_dir.iterdir():
        progress = read_json(job_dir / "progress.json", {}) if job_dir.is_dir() else {}
        thread = THREADS.get(job_dir.name)
        if progress.get("state") in {"queued", "running"} and thread and thread.is_alive():
            return job_dir.name
    return None


def _run_job(job_dir: Path, cancel_event: threading.Event) -> None:
    """Runs inside the API process, retaining the already-loaded SAM3 model."""
    metadata = read_json(job_dir / "job.json", {})
    dataset_id = metadata.get("datasetId")
    capture = writer = None
    try:
        from ml.run_annotation import annotate_image, publish_results

        request = read_json(job_dir / "request.json", {})
        output_dir = job_dir / "output"
        output_dir.mkdir(exist_ok=True)
        if request.get("source_video"):
            processor = runtime.annotation_model(request.get("annotation_model", "sam3"))
            video_path = Path(request["source_video"])
            capture = cv2.VideoCapture(str(video_path), cv2.CAP_FFMPEG)
            if not capture.isOpened():
                capture.release()
                capture = cv2.VideoCapture(str(video_path))
            if not capture.isOpened():
                raise RuntimeError("The backend video decoder could not reopen the selected video.")
            fps = float(capture.get(cv2.CAP_PROP_FPS) or 25.0)
            total = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
            width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
            height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
            video_target = output_dir / f"{video_path.stem}_annotated.webm"
            output_size = browser_video_size(width, height)
            try:
                writer = open_browser_video_writer(video_target, fps, output_size)
            except RuntimeError:
                capture.release()
                raise
            frame_dir = job_dir / "video-frames"; frame_dir.mkdir(exist_ok=True)
            completed = detections = 0
            failures, empty_frames = [], []
            _write_job_manifest(job_dir / "coverage.json", {"expected": total, "attempted": 0, "failed": [], "noObjects": []})
            _write_progress(job_dir, {"state": "running", "completed": 0, "total": total, "items": []})
            while True:
                if cancel_event.is_set():
                    capture.release(); writer.release()
                    finish_dataset(dataset_id, "cancelled")
                    _write_progress(job_dir, {"state": "cancelled", "completed": completed, "total": total, "items": []})
                    return
                ok, frame = capture.read()
                if not ok:
                    break
                frame_path = frame_dir / f"frame_{completed:06d}.jpg"
                if not cv2.imwrite(str(frame_path), frame): raise OSError("Could not save decoded frame.")
                # Hand the already-decoded frame straight to the model instead of
                # re-reading the JPEG we just wrote (that copy exists only so the
                # frame is kept in the dataset's Raw folder like any other input).
                item = process_image(processor, frame_path, request, output_dir, image=frame, keep_frame=True)
                raw_frames = Path(metadata["projectDirectory"]) / "Raw"
                raw_frames.mkdir(parents=True, exist_ok=True)
                shutil.copy2(frame_path, raw_frames / item["filename"])
                publish_results(job_dir, [item])
                # Use the annotated frame handed back in memory; only fall back
                # to reading it off disk if something upstream didn't provide it
                # (e.g. the retry/failure path in process_image).
                annotated_frame = item.pop("__frame__", None)
                if annotated_frame is None:
                    annotated_frame = cv2.imread(str(job_dir / item["annotated"]))
                writer.write(normalize_video_frame(annotated_frame, output_size))
                detections += item.get("detections", 0)
                completed += 1
                if item.get("error"): failures.append({"filename": item["filename"], "error": item["error"]})
                elif not item.get("detections"): empty_frames.append(item["filename"])
                _write_job_manifest(job_dir / "coverage.json", {"expected": total or completed, "attempted": completed,
                    "failed": failures, "noObjects": empty_frames})
                _write_progress(job_dir, {"state": "running", "completed": completed, "total": total, "items": []})
            capture.release(); writer.release()
            if completed == 0 or (total > 0 and completed < total):
                raise RuntimeError(f"Video decoding stopped early at {completed}/{total} frames. Retry the source; it has not been marked complete.")
            video_item = {"filename": video_path.name, "video": str(video_target.relative_to(job_dir)), "detections": detections}
            metadata = read_json(job_dir / "job.json", {})
            raw_dir = Path(metadata.get("projectDirectory", "")) / "Raw"; raw_dir.mkdir(parents=True, exist_ok=True)
            shutil.copy2(video_path, raw_dir / video_path.name)
            annotated_dir = Path(metadata.get("projectDirectory", "")) / "Annotated" / ("Pass" if detections else "Fail")
            annotated_dir.mkdir(parents=True, exist_ok=True)
            shutil.copy2(video_target, annotated_dir / video_target.name)
            write_json(job_dir / "result.json", {"items": [video_item]})
            _write_progress(job_dir, {"state": "complete", "completed": completed, "total": total, "items": [video_item]})
            finish_dataset(dataset_id, "complete")
            return

        valid_extensions = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}
        candidates = request.get("source_files") or [str(path) for path in (job_dir / "input").iterdir()]
        image_paths = [path for path in map(Path, candidates) if path.suffix.lower() in valid_extensions]
        _write_job_manifest(job_dir / "coverage.json", {"expected": [str(p) for p in image_paths], "attempted": 0, "failed": []})
        _write_progress(job_dir, {"state": "running", "completed": 0, "total": len(image_paths), "items": []})

        # The first call loads SAM3. Every later job reuses this GPU-resident processor.
        processor = runtime.annotation_model(request.get("annotation_model", "sam3"))
        items: list[dict] = []
        metadata = read_json(job_dir / "job.json", {})
        raw_dir = Path(metadata.get("projectDirectory", "")) / "Raw"
        raw_dir.mkdir(parents=True, exist_ok=True)
        for path in image_paths:
            if cancel_event.is_set():
                finish_dataset(dataset_id, "cancelled")
                _write_progress(job_dir, {"state": "cancelled", "completed": len(items), "total": len(image_paths), "items": items})
                return
            threshold = request.get("image_thresholds", {}).get(request.get("output_names", {}).get(str(path), path.name), request.get("confidence_threshold", .5))
            item = process_image(processor, path, {**request, "confidence_threshold": threshold}, output_dir)
            items.append(item)
            # Source folders remain in place; this is the deliberate output
            # dataset copy, not a second browser upload/staging copy.
            if path.is_file(): shutil.copy2(path, raw_dir / item["filename"])
            display = LIVE_DISPLAYS.get(job_dir.name)
            if display:
                with display["condition"]:
                    display["produced"] = len(items)
            publish_results(job_dir, [item])
            _write_job_manifest(job_dir / "coverage.json", {"expected": len(image_paths), "attempted": len(items),
                "failed": [{"filename": i["filename"], "error": i["error"]} for i in items if i.get("error")],
                "noObjects": [i["filename"] for i in items if not i.get("detections") and not i.get("error")]})
            _write_progress(job_dir, {"state": "running", "completed": len(items), "total": len(image_paths), "items": items})
            # UI paint acknowledgements must never gate GPU processing:
            # browsers suspend painting when hidden or minimized.
        write_json(job_dir / "result.json", {"items": items})
        final_state = "cancelled" if cancel_event.is_set() else "complete"
        finish_dataset(dataset_id, final_state)
        _write_progress(job_dir, {"state": final_state, "completed": len(items), "total": len(image_paths), "items": items})
        (job_dir / "runner.log").write_text(f"Completed {len(items)} image(s) using the persistent SAM3 runtime.\n", encoding="utf-8")
        logger.info("annotation job complete id=%s images=%s", job_dir.name, len(items))
    except Exception as error:
        finish_dataset(dataset_id, "failed")
        (job_dir / "runner.log").write_text(traceback.format_exc(), encoding="utf-8")
        progress = read_json(job_dir / "progress.json", {"completed": 0, "total": 0, "items": []})
        progress.update(state="failed", error=str(error))
        _write_progress(job_dir, progress)
        logger.exception("annotation job failed id=%s", job_dir.name)
    finally:
        LIVE_DISPLAYS.pop(job_dir.name, None)
        if capture is not None: capture.release()
        if writer is not None: writer.release()


def process_image(processor, path, request, output, image=None, keep_frame: bool = False):
    """Account for every input; retry errors once and retain failures in the review queue."""
    from ml.run_annotation import annotate_image
    for attempt in range(2):
        try:
            return annotate_image(processor, path, request, output, image=image, keep_frame=keep_frame)
        except Exception as error:
            logger.exception("annotation image failed file=%s attempt=%s", path.name, attempt + 1)
            failure = str(error)
    name = request.get("output_names", {}).get(str(path), path.name)
    preview = output / f"{Path(name).stem}_annotated.jpg"
    label = output / f"{Path(name).stem}.txt"
    image = cv2.imread(str(path)) if path.is_file() else None
    if image is None:
        image = np.zeros((480, 640, 3), np.uint8)
        cv2.putText(image, "Input unavailable / cannot decode", (12, 240), cv2.FONT_HERSHEY_SIMPLEX, .7, (80, 80, 255), 2)
    if not cv2.imwrite(str(preview), image): raise OSError("Could not persist failed input preview.")
    label.write_text("", encoding="utf-8")
    return {"filename": name, "annotated": str(preview.relative_to(output.parent)),
            "label": str(label.relative_to(output.parent)), "detections": 0, "overlays": [], "error": failure}


def preview_source_image(project_name: str, mode: str, roi: list, aoi: list,
                         prompts: list[str], source_token: str, source_index: int,
                         roi_source_index: int, annotation_model: str,
                         confidence_threshold: float) -> dict:
    """Evaluate one selected source image for Semi-Annotation calibration.

    This deliberately does not create an annotation job, dataset, or output
    record. Slider changes therefore affect only the currently selected image
    and cannot alter a main Smart Annotation run until the user explicitly
    chooses Run sample annotation / Start Smart Annotation.
    """
    if not prompts:
        raise HTTPException(422, "Add at least one prompt before previewing.")
    import math
    if not math.isfinite(confidence_threshold) or not 0 <= confidence_threshold <= 1:
        raise HTTPException(422, "Confidence must be between 0 and 1.")
    if not isinstance(prompts, list) or any(not isinstance(p, str) or not p.strip() for p in prompts):
        raise HTTPException(422, "Prompts must be a list of nonempty strings.")
    if annotation_model not in {"sam2", "sam3"}:
        raise HTTPException(422, "Choose SAM2 or SAM3.")
    if annotation_model == "sam3" and not LOCAL_SAM3_ENABLED:
        raise HTTPException(503, "Local SAM3 is disabled. Configure SAIL_ENABLE_LOCAL_SAM3=1 on supported hardware.")
    # The persistent processor is stateful. Do not run a calibration pass in
    # parallel with a full annotation job and risk mutating its current image.
    files = files_for(source_token)
    if not 0 <= source_index < len(files):
        raise HTTPException(422, "The selected source image was not found.")
    image_path = files[source_index]
    roi_source_file = files[roi_source_index] if roi and 0 <= roi_source_index < len(files) else None
    preview_id = uuid.uuid4().hex
    root = WORK_ROOT / ".semi-previews" / preview_id
    output = root / "output"
    output.mkdir(parents=True, exist_ok=True)
    request = {
        "mode": mode,
        "annotation_model": annotation_model,
        "roi": roi,
        "roi_source_file": str(roi_source_file) if roi_source_file else None,
        "aoi": aoi,
        "prompts": prompts,
        "confidence_threshold": confidence_threshold,
        "source_files": [str(image_path)],
        "output_names": {str(image_path): image_path.name},
    }
    with runtime.inference_lock:
        processor = runtime.annotation_model(annotation_model)
        item = process_image(processor, image_path, request, output)
    write_json(root / "preview.json", {"id": preview_id, "projectName": project_name, "filename": image_path.name, "item": item})
    return {**item, "previewId": preview_id,
            "annotatedUrl": f"/annotate/previews/{preview_id}/image"}


def preview_image_path(preview_id: str) -> Path:
    root = WORK_ROOT / ".semi-previews" / safe_name(preview_id)
    data = read_json(root / "preview.json", {})
    item = data.get("item", {})
    path = root / item.get("annotated", "")
    if data.get("id") != preview_id or not path.is_file():
        raise HTTPException(404, "Semi-Annotation preview not found.")
    return path


def _start_job(job_dir: Path, prompts: list[str]) -> dict:
    cancel_event = threading.Event()
    if read_json(job_dir / "request.json", {}).get("live_sync"):
        LIVE_DISPLAYS[job_dir.name] = {"condition": threading.Condition(), "clients": 0,
            "connected_once": False, "ack": 0, "produced": 0}
    thread = threading.Thread(target=_run_job, args=(job_dir, cancel_event), name=f"sail-annotation-{job_dir.name[:8]}", daemon=True)
    THREADS[job_dir.name] = thread
    CANCEL_EVENTS[job_dir.name] = cancel_event
    thread.start()
    # Starting a job must extend the reusable library, not replace prompts
    # saved by earlier manual or VLM sessions.
    save_prompt_library([*load_prompt_library(), *prompts])
    progress = read_json(job_dir / "progress.json", {})
    logger.info("annotation job started id=%s images=%s", job_dir.name, progress.get("total"))
    return {"jobId": job_dir.name, "state": "queued", "total": progress.get("total", 0)}


def _prepare_project(project_name: str, mode: str) -> Path:
    # Compatibility upload jobs have no source folder identity. Keep each run.
    return dataset_project_directory(project_name, mode) / f"uploads-{uuid.uuid4().hex}" / "Annotation"


async def create_job(project_name: str, mode: str, roi: list, aoi: list, prompts: list[str], images: list[UploadFile]) -> dict:
    if not prompts:
        raise HTTPException(422, "Add at least one prompt before starting annotation.")
    if not LOCAL_SAM3_ENABLED:
        raise HTTPException(503, "Local SAM3 is disabled. Configure SAIL_ENABLE_LOCAL_SAM3=1 on supported hardware.")
    if active_job():
        raise HTTPException(409, "Another annotation job is already running. Wait or cancel it before starting another job.")
    project_dir = _prepare_project(project_name, mode)
    raw_dir = project_dir / "raw"; raw_dir.mkdir(parents=True, exist_ok=True)
    job_dir = WORK_ROOT / ".jobs" / uuid.uuid4().hex
    input_dir = job_dir / "input"; input_dir.mkdir(parents=True)
    saved = []
    for image in images:
        filename = safe_name(image.filename or "image.jpg")
        target = input_dir / filename
        with target.open("wb") as output:
            shutil.copyfileobj(image.file, output)
        shutil.copy2(target, raw_dir / filename)
        saved.append(filename)
    write_json(job_dir / "request.json", {"mode": mode, "roi": roi, "aoi": aoi, "prompts": prompts})
    write_json(job_dir / "job.json", {"projectName": project_name, "projectDirectory": str(project_dir), "mode": mode, "prompts": prompts})
    write_json(job_dir / "progress.json", {"state": "queued", "completed": 0, "total": len(saved), "items": []})
    return _start_job(job_dir, prompts)


@guarded_start("annotation")
def create_job_from_source(
    project_name: str, mode: str, roi: list, aoi: list, prompts: list[str],
    source_token: str, confidence_threshold: float = 0.0,
    sample_limit: int = 0, pre_annotation: bool = False,
    sample_indices: list[int] | None = None,
    preview_index: int = -1,
    roi_source_index: int = -1,
    annotation_model: str = "sam3",
    image_thresholds: dict | None = None,
    live_sync: bool = False,
) -> dict:
    if not prompts:
        raise HTTPException(422, "Add at least one prompt before starting annotation.")
    if annotation_model not in {"sam2", "sam3"}: raise HTTPException(422, "Choose SAM2 or SAM3.")
    if annotation_model == "sam3" and not LOCAL_SAM3_ENABLED:
        raise HTTPException(503, "Local SAM3 is disabled. Configure SAIL_ENABLE_LOCAL_SAM3=1 on supported hardware.")
    source_files = files_for(source_token)
    output_names = output_names_for(source_files)
    source_info = source_info_for(source_token)
    dataset = None
    roi_source_file = (
        source_files[roi_source_index]
        if roi and 0 <= roi_source_index < len(source_files)
        else None
    )
    if preview_index >= 0:
        if preview_index >= len(source_files):
            raise HTTPException(422, "The requested calibration preview image was not found.")
        source_files = [source_files[preview_index]]
    elif pre_annotation:
        if not sample_indices:
            raise HTTPException(422, "Select samples before running sample annotation.")
        chosen = list(dict.fromkeys(index for index in sample_indices if 0 <= index < len(source_files)))[:10]
        if not chosen:
            raise HTTPException(422, "Select at least one valid pre-annotation image.")
        source_files = [source_files[index] for index in chosen]
    elif sample_limit > 0:
        source_files = source_files[:max(1, min(sample_limit, 100))]
    root_project_dir = dataset_project_directory(project_name, mode) / safe_name(source_info["name"])
    if preview_index >= 0:
        project_dir = root_project_dir / ".calibration-preview" / uuid.uuid4().hex
        project_dir.mkdir(parents=True, exist_ok=True)
    elif pre_annotation:
        project_dir = root_project_dir / ".samples" / uuid.uuid4().hex
        project_dir.mkdir(parents=True, exist_ok=True)
        write_json(project_dir / "calibration.json", {
            "prompts": prompts, "confidenceThreshold": confidence_threshold,
            "roi": roi, "aoi": aoi, "sampleCount": len(source_files),
            "sampleIndices": sample_indices or [],
        })
    else:
        dataset = create_dataset(project_name, mode, source_info, prompts)
        dataset["annotationModel"] = annotation_model
        from services.dataset_store import save_dataset
        save_dataset(dataset)
        project_dir = Path(dataset["directory"])
    job_dir = WORK_ROOT / ".jobs" / uuid.uuid4().hex; job_dir.mkdir(parents=True)
    write_json(job_dir / "request.json", {
        "mode": mode,
        "annotation_model": annotation_model,
        "roi": roi,
        "roi_source_file": str(roi_source_file) if roi_source_file else None,
        "aoi": aoi,
        "prompts": prompts,
        "confidence_threshold": confidence_threshold,
        "source_files": [str(path) for path in source_files],
        "output_names": output_names,
        "image_thresholds": image_thresholds or {},
        "live_sync": live_sync,
    })
    write_json(job_dir / "job.json", {"projectName": project_name, "projectDirectory": str(project_dir), "datasetId": dataset["id"] if dataset else None, "mode": mode, "prompts": prompts, "sourceToken": source_token, "preAnnotation": pre_annotation, "previewOnly": preview_index >= 0, "previewIndex": preview_index})
    write_json(job_dir / "progress.json", {"state": "queued", "completed": 0, "total": len(source_files), "items": []})
    return _start_job(job_dir, prompts)


@guarded_start("annotation")
def create_job_from_video(project_name: str, mode: str, roi: list, aoi: list, prompts: list[str], source_token: str, confidence_threshold: float = 0.0, annotation_model: str = "sam3") -> dict:
    if not prompts:
        raise HTTPException(422, "Add at least one prompt before starting annotation.")
    if annotation_model not in {"sam2", "sam3"}: raise HTTPException(422, "Choose SAM2 or SAM3.")
    if annotation_model == "sam3" and not LOCAL_SAM3_ENABLED:
        raise HTTPException(503, "Local SAM3 is disabled. Configure SAIL_ENABLE_LOCAL_SAM3=1 on supported hardware.")
    video_path = direct_video_for(source_token)
    dataset = create_dataset(project_name, mode, source_info_for(source_token), prompts)
    dataset["annotationModel"] = annotation_model
    from services.dataset_store import save_dataset
    save_dataset(dataset)
    project_dir = Path(dataset["directory"])
    job_dir = WORK_ROOT / ".jobs" / uuid.uuid4().hex; job_dir.mkdir(parents=True)
    write_json(job_dir / "request.json", {"mode": mode, "annotation_model": annotation_model, "roi": roi, "aoi": aoi, "prompts": prompts, "confidence_threshold": confidence_threshold, "source_video": str(video_path)})
    write_json(job_dir / "job.json", {"projectDirectory": str(project_dir), "datasetId": dataset["id"], "mode": mode, "prompts": prompts, "sourceToken": source_token})
    write_json(job_dir / "progress.json", {"state": "queued", "completed": 0, "total": 0, "items": []})
    return _start_job(job_dir, prompts)


def results(job_dir: Path) -> list[dict]:
    progress = read_json(job_dir / "progress.json", {"items": []})
    values = []
    for item in progress.get("items", []):
        if item.get("video") and (job_dir / item["video"]).is_file():
            values.append({"filename": item["filename"], "videoUrl": f"/files/{job_dir.relative_to(WORK_ROOT).as_posix()}/{item['video']}", "detections": item.get("detections", 0)})
        elif item.get("annotated") and item.get("label") and (job_dir / item["annotated"]).is_file() and (job_dir / item["label"]).is_file():
            values.append({"filename": item["filename"], "annotatedUrl": f"/files/{job_dir.relative_to(WORK_ROOT).as_posix()}/{item['annotated']}", "labelUrl": f"/files/{job_dir.relative_to(WORK_ROOT).as_posix()}/{item['label']}", "detections": item.get("detections", 0), "overlays": item.get("overlays", [])})
    return values


def status(job_id: str) -> dict:
    job_dir = _job_dir(job_id)
    if not job_dir.is_dir():
        raise HTTPException(404, "Annotation job was not found.")
    progress = read_json(job_dir / "progress.json", {"state": "unknown", "completed": 0, "total": 0, "items": []})
    return {"jobId": job_id, "datasetId": read_json(job_dir / "job.json", {}).get("datasetId"), "coverage": read_json(job_dir / "coverage.json", {}), "state": progress.get("state", "unknown"), "completed": progress.get("completed", 0), "total": progress.get("total", 0), "error": progress.get("error"), "results": results(job_dir)}


def cancel(job_id: str) -> dict:
    event = CANCEL_EVENTS.get(job_id)
    if not event:
        raise HTTPException(409, "Annotation job is not running.")
    event.set()
    logger.info("annotation job cancellation requested id=%s", job_id)
    return {"jobId": job_id, "state": "cancelling"}
