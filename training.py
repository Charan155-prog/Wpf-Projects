from typing import Literal
from pathlib import Path
from fastapi import APIRouter, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field, model_validator
from services import training_jobs as jobs

router = APIRouter(tags=["training"])


class TrainingRequest(BaseModel):
    datasetId: str
    architecture: Literal["YOLOv8n", "YOLOv8s", "YOLOv8m", "YOLOv8l", "YOLOv8x"] = "YOLOv8n"
    mode: Literal["normal", "incremental"] = "normal"
    baseModelId: str = ""
    epochs: int = Field(100, ge=1, le=1000)
    batchSize: int = Field(4, ge=1, le=128)
    imageSize: int = Field(720, ge=320, le=1280)
    learningRate: float = Field(0.01, ge=0.0001, le=0.1)
    optimizer: Literal["SGD", "Adam", "AdamW"] = "SGD"
    device: Literal["0"] = "0"
    pretrained: bool = True
    freezeLayers: int = Field(10, ge=0, le=50)
    patience: int = Field(0, ge=0, le=1000)
    seed: int = Field(42, ge=0, le=2147483647)
    ratios: tuple[int, int, int] = (70, 20, 10)
    excludedImages: list[str] = Field(default_factory=list, max_length=10000)

    @model_validator(mode="after")
    def valid(self):
        if self.imageSize != 720 and self.imageSize % 32:
            raise ValueError("Use 720 from the supplied training script, or a multiple of 32.")
        if any(r <= 0 for r in self.ratios) or sum(self.ratios) != 100:
            raise ValueError("Split percentages must total 100 and be positive.")
        if self.mode == "incremental" and not self.baseModelId:
            raise ValueError("Select a compatible base model.")
        if len(set(self.excludedImages)) != len(self.excludedImages):
            raise ValueError("The same image cannot be excluded twice.")
        return self


@router.get("/training/runs")
def runs(): return {"items": [jobs.public_run(r) for r in jobs.list_runs()]}

@router.post("/training/runs", status_code=202)
def start(request: TrainingRequest): return jobs.start(request.model_dump())

@router.get("/training/runs/{run_id}")
def detail(run_id: str): return jobs.detail(run_id)

@router.post("/training/runs/{run_id}/cancel")
def cancel(run_id: str): return jobs.cancel(run_id)

@router.get("/models")
def models(): return {"items": [jobs.public_model(m) for m in jobs.list_models()]}

@router.get("/models/{model_id}/download")
def download(model_id: str):
    model = jobs.get_model(model_id)
    filename = model.get("filename") or f"{jobs.safe_name(model['name'])}_{model['mode']}_{model['id'][:8]}_best.pt"
    return FileResponse(model["path"], filename=filename, media_type="application/octet-stream")


@router.delete("/models/{model_id}", status_code=204)
def delete_model(model_id: str):
    jobs.delete_model(model_id)


def split_images(run_id, split):
    if split not in {"train", "val", "test"}: raise HTTPException(422, "Invalid split.")
    run = jobs.get_run(run_id)
    root = Path(run["directory"]) / "split"
    manifest = jobs.read_json(root / "split.json", {})
    return root, [p for p in manifest.get("pairs", []) if p["split"] == split]


@router.get("/training/runs/{run_id}/images")
def images(run_id: str, split: str = "train", page: int = 1, page_size: int = 16):
    _, pairs = split_images(run_id, split)
    page = max(1, page)
    page_size = max(1, min(page_size, 200))
    start = (page - 1) * page_size
    return {"total": len(pairs), "page": page, "pageSize": page_size, "items": [
        {"name": Path(p["image"]).name, "url": f"/training/runs/{run_id}/images/{split}/{i}"}
        for i, p in enumerate(pairs) if start <= i < start + page_size]}


@router.get("/training/runs/{run_id}/images/{split}/{index}")
def image(run_id: str, split: str, index: int):
    from services.dataset_store import contained
    root, pairs = split_images(run_id, split)
    if not 0 <= index < len(pairs): raise HTTPException(404, "Image not found.")
    return FileResponse(contained(root, f"images/{split}/{Path(pairs[index]['image']).name}"))
