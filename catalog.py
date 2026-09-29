from __future__ import annotations

import os
import tempfile
import shutil
import re
from pathlib import Path

from fastapi import APIRouter, Body, Form, HTTPException

from core import ANNOTATION_RUNNER, CONDA_ENV, CONDA_PYTHON, LOCAL_SAM3_ENABLED, PROJECTS_FILE, SAM3_CHECKPOINT, SETTINGS_FILE, VLM_MODEL, VLM_RUNNER, WORK_ROOT, load_prompt_library, logger, read_json, save_prompt_library, write_json
from services.model_runtime import runtime

router = APIRouter(tags=["workspace"])

@router.get("/health")
def health():
    return {"status": "ok", "datasetApi": True, "outputLayout": "source-folder-v2", "models": runtime.state(), "nativeCondaRunnerConfigured": CONDA_PYTHON.is_file() and ANNOTATION_RUNNER.is_file(), "condaEnvironment": str(CONDA_ENV), "condaPython": str(CONDA_PYTHON), "sam3CheckpointReady": Path(SAM3_CHECKPOINT).is_file(), "localSam3Enabled": LOCAL_SAM3_ENABLED, "nativeVlmRunnerConfigured": CONDA_PYTHON.is_file() and VLM_RUNNER.is_file() and bool(VLM_MODEL)}

@router.get("/prompts")
def prompts(): return {"prompts": load_prompt_library()}

@router.post("/prompts")
def add_prompt(prompt: str = Form(...)):
    values = save_prompt_library([*load_prompt_library(), prompt])
    logger.info("prompt saved value=%s", prompt[:80])
    return {"prompts": values}

@router.delete("/prompts")
def delete_prompt(prompt: str = Form(...)):
    values = save_prompt_library([value for value in load_prompt_library() if value != prompt.strip()])
    logger.info("prompt removed value=%s", prompt[:80])
    return {"prompts": values}

@router.get("/projects")
def projects(): return {"projects": read_json(PROJECTS_FILE, [])}

@router.put("/projects")
def save_projects(projects: list[dict] = Body(...)):
    write_json(PROJECTS_FILE, projects)
    logger.info("projects saved count=%s", len(projects))
    return {"projects": projects}


@router.delete("/projects/{project_id}")
def delete_project(project_id: str):
    from services import dataset_store as store, training_jobs as training, inference_jobs as inference
    from services import annotation_jobs as annotation, automatic_validation as validation
    from services.workflow_guard import LOCK as workflow_lock
    # Starts use this same lock: no new worker can start between preflight and deletion.
    with workflow_lock, store.LOCK, training.LOCK, inference.LOCK:
        projects = read_json(PROJECTS_FILE, [])
        project = next((p for p in projects if str(p.get("id")) == project_id), None)
        if project is None:
            raise HTTPException(404, "Project not found.")
        name = project["name"]
        if sum(p.get("name") == name for p in projects) != 1:
            raise HTTPException(409, "Projects share this name; output ownership is ambiguous. Rename the duplicate first.")
        # Also covers validation/export workers not registered by guarded_start.
        for service in (annotation, validation, training, inference, store):
            if any(t.is_alive() for t in getattr(service, "THREADS", {}).values()):
                raise HTTPException(409, "Stop active processing before deleting a project and its output files.")
        if any(t.is_alive() for t in store.MANUAL_THREADS.values()):
            raise HTTPException(409, "Wait for dataset validation to finish before deleting this project.")
        store.discover_outputs()
        datasets = [d for d in store.list_datasets(True) if d["projectName"] == name]
        snapshots = [s for s in store.training_datasets() if s["projectName"] == name]
        training_runs = [r for r in training.list_runs() if r["projectName"] == name]
        inference_runs = [r for r in inference.list_runs() if r["projectName"] == name]
        targets, pointers = {}, []
        groups = ((datasets, "dataset.json", store.registry()),
                  (snapshots, "dataset.json", WORK_ROOT / ".snapshots"),
                  (training_runs, "run.json", WORK_ROOT / ".training"),
                  (inference_runs, "run.json", WORK_ROOT / ".inference"))
        forbidden = {WORK_ROOT.resolve(), Path.home().resolve(), Path(PROJECTS_FILE).parent.resolve()}
        for values, manifest_name, registry in groups:
            for data in values:
                root = Path(data["directory"]).resolve()
                manifest = read_json(root / manifest_name, {})
                if (not re.fullmatch(r"[a-f0-9]{32}", str(data.get("id", "")))
                    or root in forbidden or root == Path(root.anchor)
                    or manifest.get("id") != data["id"] or manifest.get("projectName") != name
                    or Path(data["directory"]).is_symlink()):
                    raise HTTPException(409, "Output ownership could not be verified. No project files were deleted.")
                targets[root] = True
                pointers.append(registry / f"{data['id']}.json")
        # Cached annotation copies belong to a dataset, never to its input path.
        dataset_ids = {d["id"] for d in datasets}
        for manifest in (WORK_ROOT / ".jobs").glob("*/job.json"):
            job = read_json(manifest, {})
            if job.get("datasetId") in dataset_ids or job.get("projectName") == name:
                root = manifest.parent.resolve()
                if root.parent == (WORK_ROOT / ".jobs").resolve() and re.fullmatch(r"[a-f0-9]{32}", root.name):
                    targets[root] = True
                    if not job.get("datasetId") and job.get("projectDirectory"):
                        from core import dataset_project_directory
                        output = Path(job["projectDirectory"]).resolve()
                        boundary = dataset_project_directory(name, job.get("mode", "detection")).resolve()
                        if output.is_relative_to(boundary) and len(output.relative_to(boundary).parts) >= 2:
                            targets[output] = True
        for manifest in (WORK_ROOT / ".semi-previews").glob("*/preview.json"):
            preview = read_json(manifest, {})
            root = manifest.parent.resolve()
            if (preview.get("projectName") == name and preview.get("id") == root.name
                and re.fullmatch(r"[a-f0-9]{32}", root.name)
                and root.parent == (WORK_ROOT / ".semi-previews").resolve()):
                targets[root] = True
        parents = set()
        try:
            for root in sorted(targets, key=lambda p: len(p.parts), reverse=True):
                parents.add(root.parent)
                if root.exists():
                    shutil.rmtree(root)
            for pointer in pointers:
                pointer.unlink(missing_ok=True)
            for snapshot in snapshots:
                store.excluded_images_path(snapshot["id"]).unlink(missing_ok=True)
            # Only remove empty grouping directories; never recurse over a project
            # name guessed from disk, which could contain unrelated user files.
            from core import safe_name
            for parent in parents:
                if not any(p.name == safe_name(name) for p in (parent, *parent.parents)):
                    continue
                for _ in range(5):
                    if parent in forbidden or parent == Path(parent.anchor): break
                    try: parent.rmdir()
                    except OSError: break
                    if parent.name == safe_name(name): break
                    parent = parent.parent
        except OSError as error:
            raise HTTPException(409, f"Some output files could not be removed. Project retained; close files and retry. {error}") from error
        remaining = [p for p in projects if str(p.get("id")) != project_id]
        write_json(PROJECTS_FILE, remaining)
        return {"projects": remaining, "deletedOutputs": len(targets)}

@router.get("/settings")
def settings():
    return {"settings": read_json(SETTINGS_FILE, {}), "defaults": {
        "sam2Checkpoint": str(Path(SAM3_CHECKPOINT).parent.parent / "sam2" / "sam2.1_b.pt"),
        "groundingDinoModel": "IDEA-Research/grounding-dino-base",
        "datasetRoot": str(WORK_ROOT / "datasets"),
        "validatedRoot": str(WORK_ROOT / "validated-datasets"),
        "modelsDir": str(WORK_ROOT / "models"), "exportDir": str(WORK_ROOT / "exports"),
        "inferenceDir": str(WORK_ROOT / "inference"), "annotationCache": str(WORK_ROOT / ".jobs"),
    }}

@router.put("/settings")
def save_settings(settings: dict = Body(...)):
    merged = {**read_json(SETTINGS_FILE, {}), **settings}
    checkpoint = str(merged.get("sam2Checkpoint", "")).strip()
    if checkpoint and not Path(checkpoint).is_file():
        raise HTTPException(422, "SAM2 checkpoint must point to an existing .pt file on the backend.")
    for key in ("datasetRoot", "validatedRoot", "modelsDir", "exportDir", "inferenceDir", "annotationCache"):
        value = str(merged.get(key, "")).strip()
        if not value:
            continue
        path = Path(os.path.expandvars(os.path.expanduser(value)))
        if not path.is_absolute():
            raise HTTPException(422, f"{key} must be an absolute path on the backend machine.")
        try:
            path.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryFile(dir=path):
                pass
        except OSError as error:
            raise HTTPException(422, f"Cannot write to {key}: {error}") from error
        merged[key] = str(path.resolve())
    write_json(SETTINGS_FILE, merged)
    return {"settings": merged}

@router.post("/settings/select-folder")
def select_folder(kind: str = "folder"):
    if os.name != "nt": raise HTTPException(501, "Native folder selection is available on Windows only.")
    try:
        import tkinter as tk
        from tkinter import filedialog
        window = tk.Tk(); window.withdraw(); window.attributes("-topmost", True)
        if kind == "file":
            selected = filedialog.askopenfilename(title="Select model checkpoint", filetypes=[("Checkpoint", "*.pt")])
        elif kind == "media":
            selected = filedialog.askopenfilename(title="Select input image or video", filetypes=[("Images and videos", "*.jpg *.jpeg *.png *.bmp *.webp *.tif *.tiff *.mp4 *.avi *.mov *.mkv *.webm"), ("All files", "*.*")])
        else:
            selected = filedialog.askdirectory(title="Select folder")
        window.destroy()
        return {"path": selected}
    except Exception as error:
        raise HTTPException(500, f"Could not open folder picker: {error}") from error
