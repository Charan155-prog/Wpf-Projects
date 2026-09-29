from __future__ import annotations

import re
from typing import Literal
from pathlib import Path

from fastapi import APIRouter, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field, field_validator, model_validator

from services import dataset_store as store
from services.dataset_validation import preview_correction, commit_correction
from services import automatic_validation as automatic

router = APIRouter(tags=["datasets"])


class Revision(BaseModel):
    revision: int = Field(ge=0)


class Deletion(Revision):
    deleted: bool = True


class GroundTruth(Revision):
    confirmed: bool = False


@router.post("/datasets/{dataset_id}/images/{item_id}/ground-truth")
def save_ground_truth(dataset_id: str, item_id: str, body: GroundTruth):
    from services.ground_truth import freeze
    return {"reference": freeze(dataset_id, item_id, body.revision, body.confirmed)}


class Region(BaseModel):
    classId: int = Field(ge=0)
    points: list[list[float]] = Field(min_length=3, max_length=2000)
    clicks: list[list[float]] = Field(default_factory=list, max_length=200)
    clickLabels: list[Literal[0, 1]] = Field(default_factory=list, max_length=200)

    @field_validator("points", "clicks")
    @classmethod
    def valid_points(cls, points):
        if any(len(point) != 2 or any(not 0 <= coordinate <= 1 for coordinate in point) for point in points):
            raise ValueError("ROI points must be normalized x,y pairs in [0,1].")
        return points

    @model_validator(mode="after")
    def valid_clicks(self):
        if len(self.clicks) != len(self.clickLabels): raise ValueError("Each click needs a foreground/background label.")
        return self


class Correction(Revision):
    regions: list[Region] = Field(min_length=1, max_length=50)
    model: Literal["sam2", "sam3"] = "sam3"
    method: Literal["model", "manual"] = "model"


class Reprocess(Revision):
    model: Literal["sam2", "sam3"] = "sam3"
    confidence: float = Field(.25, ge=0, le=1)


@router.post("/datasets/{dataset_id}/images/{item_id}/reprocess")
def reprocess(dataset_id: str, item_id: str, body: Reprocess):
    from services.dataset_validation import preview_reprocess
    try: return preview_reprocess(dataset_id, item_id, body.revision, body.model, body.confidence)
    except ValueError as error: raise HTTPException(422, str(error)) from error


class Commit(Revision):
    previewId: str = Field(pattern=r"^[a-f0-9]{32}$")


class AutomaticExport(Revision):
    reportId: str = Field(pattern=r"^[a-f0-9]{32}$")


@router.get("/datasets/{dataset_id}/automatic")
def automatic_status(dataset_id: str, page: int = 1, status: str = "all"):
    if status not in {"all", "candidate", "review"}:
        raise HTTPException(422, "Unknown review filter.")
    return automatic.status(dataset_id, page, status)


@router.post("/datasets/{dataset_id}/automatic")
def automatic_start(dataset_id: str, request: Revision):
    return {"report": automatic.start(dataset_id, request.revision)}


@router.post("/datasets/{dataset_id}/automatic/cancel")
def automatic_cancel(dataset_id: str):
    return automatic.cancel(dataset_id)


@router.post("/datasets/{dataset_id}/automatic/export")
def automatic_export(dataset_id: str, request: AutomaticExport):
    return {"snapshot": automatic.export(dataset_id, request.revision, request.reportId)}


@router.get("/datasets")
def datasets():
    store.discover_outputs()
    return {"datasets": store.list_datasets()}


@router.get("/datasets/{dataset_id}")
def dataset(dataset_id: str, page: int = 1, page_size: int = 48, deleted: bool = False, status: Literal["Pass", "Fail", "all"] = "Pass"):
    with store.LOCK:
        data = store.get_dataset(dataset_id)
        items = [item for item in data["items"] if (status == "all" or item["status"] == status) and bool(item.get("deleted")) == deleted]
        page_size = max(1, min(page_size, 100))
        page = max(1, page)
        return {"dataset": store.summary(data), "total": len(items), "page": page,
                "items": [store.public_item(data, item) for item in items[(page - 1) * page_size:page * page_size]]}


@router.delete("/datasets/{dataset_id}", status_code=204)
def delete_dataset(dataset_id: str):
    store.delete_dataset(dataset_id)


@router.get("/datasets/{dataset_id}/images/{item_id}/file/{kind}")
def image_file(dataset_id: str, item_id: str, kind: str):
    if kind not in {"annotated", "raw", "label"}:
        raise HTTPException(404, "Unknown dataset asset.")
    data = store.get_dataset(dataset_id)
    item = store.get_item(data, item_id)
    path = store.contained(Path(data["directory"]), item[kind])
    if not path.is_file():
        raise HTTPException(404, "The image or label is missing from its storage location.")
    return FileResponse(path, headers={"Cache-Control": "no-cache"})


@router.patch("/datasets/{dataset_id}/images/{item_id}")
def delete_image(dataset_id: str, item_id: str, body: Deletion):
    return {"dataset": store.set_deleted(dataset_id, item_id, body.deleted, body.revision)}


@router.post("/datasets/{dataset_id}/images/{item_id}/preview")
def preview(dataset_id: str, item_id: str, body: Correction):
    try:
        return preview_correction(dataset_id, item_id, [region.model_dump() for region in body.regions], body.revision, body.model, body.method)
    except ValueError as error:
        raise HTTPException(422, str(error)) from error


@router.get("/datasets/{dataset_id}/previews/{token}/image")
def preview_image(dataset_id: str, token: str):
    if not re.fullmatch(r"[a-f0-9]{32}", token):
        raise HTTPException(404, "Preview was not found.")
    data = store.get_dataset(dataset_id)
    path = store.contained(Path(data["directory"]), f".edits/{token}/annotated.jpg")
    if not path.is_file():
        raise HTTPException(404, "Preview was not found.")
    return FileResponse(path, headers={"Cache-Control": "no-store"})


@router.post("/datasets/{dataset_id}/images/{item_id}/commit")
def commit(dataset_id: str, item_id: str, body: Commit):
    return {"dataset": commit_correction(dataset_id, item_id, body.previewId, body.revision)}


@router.post("/datasets/{dataset_id}/validate")
def validate(dataset_id: str, body: Revision):
    return {"snapshot": store.validate_dataset(dataset_id, body.revision)}


@router.get("/datasets/{dataset_id}/validate/status")
def validate_status(dataset_id: str):
    return store.manual_validation_status(dataset_id)


@router.post("/datasets/{dataset_id}/validate/start")
def validate_start(dataset_id: str, body: Revision):
    return store.start_manual_validation(dataset_id, body.revision)


@router.post("/datasets/{dataset_id}/validate/cancel")
def validate_cancel(dataset_id: str):
    return store.cancel_manual_validation(dataset_id)


@router.get("/training/datasets")
def training():
    return {"datasets": store.training_datasets()}


@router.delete("/training/datasets/{snapshot_id}", status_code=204)
def delete_training_dataset(snapshot_id: str):
    from services.training_jobs import list_runs, ACTIVE
    if any(run.get("datasetId") == snapshot_id and run.get("state") in ACTIVE for run in list_runs()):
        raise HTTPException(409, "This validated dataset is being used by active training.")
    store.delete_snapshot(snapshot_id)


@router.post("/training/datasets/{snapshot_id}/verify")
def verify_training(snapshot_id: str):
    return store.check_snapshot(snapshot_id)


class ExcludedImages(BaseModel):
    excludedImages: list[str] = Field(default_factory=list, max_length=10000)


@router.get("/training/datasets/{snapshot_id}/excluded")
def get_excluded_images(snapshot_id: str):
    return {"excludedImages": store.get_excluded_images(snapshot_id)}


@router.put("/training/datasets/{snapshot_id}/excluded")
def put_excluded_images(snapshot_id: str, body: ExcludedImages):
    return {"excludedImages": store.set_excluded_images(snapshot_id, body.excludedImages)}


@router.get("/training/datasets/{snapshot_id}/images")
def training_images(snapshot_id: str, page: int = 1, page_size: int = 120):
    data = store.get_snapshot(snapshot_id)
    page = max(1, page)
    page_size = max(1, min(page_size, 500))
    start = (page - 1) * page_size
    pairs = data["pairs"]
    return {"total": len(pairs), "page": page, "pageSize": page_size,
            "items": [{"name": Path(pair["image"]).name,
                       "url": f"/training/datasets/{snapshot_id}/images/{index}"}
                      for index, pair in enumerate(pairs[start:start + page_size], start)]}


@router.get("/training/datasets/{snapshot_id}/images/{index}")
def training_image(snapshot_id: str, index: int):
    data = store.get_snapshot(snapshot_id)
    if index < 0 or index >= len(data["pairs"]):
        raise HTTPException(404, "Training image was not found.")
    return FileResponse(store.contained(Path(data["directory"]), data["pairs"][index]["image"]))
