from pathlib import Path
import os
from fastapi import APIRouter, HTTPException, UploadFile, File
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field, model_validator
from services import inference_jobs as jobs

router = APIRouter(prefix="/inference", tags=["inference"])


@router.get("/browse")
def browse(path: str = "", kind: str = "folder", page: int = 1):
    """Browse input media in the web UI, never open a backend desktop dialog."""
    from core import WORK_ROOT
    from services.source_catalog import IMAGE_EXTENSIONS, VIDEO_EXTENSIONS
    if kind not in {"folder", "media"}:
        raise HTTPException(422, "Choose folder or media browsing.")
    roots = [f"{letter}:/" for letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ" if Path(f"{letter}:/").exists()] if os.name == "nt" else ["/"]
    directory = Path(os.path.expandvars(os.path.expanduser(path.strip()))) if path.strip() else WORK_ROOT
    if directory.is_file(): directory = directory.parent
    try:
        directory = directory.resolve(strict=True)
        entries = sorted((p for p in directory.iterdir() if p.is_dir() or
                          (kind == "media" and p.suffix.lower() in IMAGE_EXTENSIONS | VIDEO_EXTENSIONS)),
                         key=lambda p: (not p.is_dir(), p.name.casefold()))
    except (OSError, ValueError) as error:
        raise HTTPException(422, f"Cannot browse input directory: {error}") from error
    offset = (max(1, page) - 1) * 200
    return {"path": str(directory), "parent": str(directory.parent), "roots": roots,
            "total": len(entries), "page": max(1, page),
            "items": [{"name": p.name, "path": str(p), "directory": p.is_dir()} for p in entries[offset:offset + 200]]}


@router.post("/inputs")
async def upload_input(files: list[UploadFile] = File(...)):
    import uuid
    from core import WORK_ROOT
    from services.source_catalog import IMAGE_EXTENSIONS, VIDEO_EXTENSIONS
    if not files or any(Path(f.filename or "").suffix.lower() not in IMAGE_EXTENSIONS | VIDEO_EXTENSIONS for f in files):
        raise HTTPException(422, "Choose supported images or one video.")
    if len(files) > 1 and any(Path(f.filename).suffix.lower() in VIDEO_EXTENSIONS for f in files):
        raise HTTPException(422, "Upload one video at a time, or multiple images.")
    root = WORK_ROOT / "inference-inputs" / uuid.uuid4().hex
    root.mkdir(parents=True, exist_ok=False)
    paths = []
    for index, upload in enumerate(files):
        path = root / f"{index:06d}_{jobs.safe_name(Path(upload.filename).stem)}{Path(upload.filename).suffix.lower()}"
        try:
            with path.open("xb") as stream:
                while chunk := await upload.read(1024 * 1024):
                    stream.write(chunk)
            paths.append(path)
        finally:
            await upload.close()
    return {"path": str(paths[0] if len(paths) == 1 else root), "count": len(paths)}


class InferenceRequest(BaseModel):
    modelId: str
    sourcePath: str = Field(min_length=1)
    confidence: float = Field(.9, ge=.01, le=1)
    iou: float = Field(.45, ge=.01, le=1)
    imageSize: int = Field(640, ge=320, le=1280)
    drawBoxes: bool = True
    maskAlpha: float = Field(.35, ge=0, le=1)

    @model_validator(mode="after")
    def valid(self):
        if self.imageSize % 32: raise ValueError("Image size must be a multiple of 32.")
        return self


@router.get("/runs")
def runs(): return {"items": jobs.list_runs()}

@router.post("/runs", status_code=202)
def start(body: InferenceRequest): return jobs.start(body.model_dump())

@router.get("/runs/{run_id}")
def detail(run_id: str, page: int = 1): return jobs.detail(run_id, page)

@router.post("/runs/{run_id}/cancel")
def cancel(run_id: str):
    jobs.get_run(run_id)
    if run_id in jobs.CANCEL: jobs.CANCEL[run_id].set()
    return {"requested": run_id in jobs.CANCEL}


@router.delete("/runs/{run_id}", status_code=204)
def delete_run(run_id: str):
    jobs.delete_run(run_id)

@router.get("/runs/{run_id}/asset/{index}")
def asset(run_id: str, index: int, download: bool = False):
    path = jobs.asset(run_id, index, playback=not download)
    return FileResponse(path, filename=path.name if download else None)


@router.delete("/runs/{run_id}/asset/{index}", status_code=204)
def delete_asset(run_id: str, index: int):
    jobs.delete_asset(run_id, index)

@router.get("/runs/{run_id}/preview")
def preview(run_id: str):
    path = Path(jobs.get_run(run_id)["directory"]) / "preview.jpg"
    if not path.is_file(): raise HTTPException(404, "Preview not ready.")
    return FileResponse(path, headers={"Cache-Control": "no-store"})

@router.get("/runs/{run_id}/report")
def report(run_id: str):
    data = jobs.get_run(run_id)
    path = Path(data["directory"]) / "predictions.jsonl"
    if data["state"] != "complete" or not path.is_file(): raise HTTPException(409, "Report is not complete yet.")
    return FileResponse(path, filename=f"{jobs.safe_name(data['name'])}_{run_id[:8]}_predictions.jsonl")
