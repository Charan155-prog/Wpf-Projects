"""ROI correction: always infer on the clean raw image; preview before commit."""
from __future__ import annotations

import uuid
from pathlib import Path

from fastapi import HTTPException

from core import read_json
from services.dataset_store import (LOCK, atomic_json, contained, get_dataset, get_item,
                                    now, require_editable, save_dataset, summary)


def preview_correction(dataset_id: str, item_id: str, regions: list, revision: int, model_name: str = "sam3", method: str = "model") -> dict:
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        item = get_item(data, item_id)
        if item.get("deleted"):
            raise HTTPException(409, "Restore this image before editing it.")
        source = contained(Path(data["directory"]), item["raw"])
        if not source.is_file():
            raise HTTPException(404, "The raw image is unavailable.")
        classes = data["classes"]
        if data.get("annotationModel") == "sam2" and model_name != "sam2":
            raise HTTPException(422, "This SAM2 dataset uses SAM2 for semi-annotation.")
        if not regions or len(regions) > 50:
            raise HTTPException(422, "Draw between 1 and 50 object regions.")
        for region in regions:
            if not 0 <= region["classId"] < len(classes):
                raise HTTPException(422, "Choose a class from this dataset.")
        token = uuid.uuid4().hex
        directory = contained(Path(data["directory"]), f".edits/{token}")
        directory.mkdir(parents=True)
    # Do not hold the dataset metadata lock during GPU inference. Revision is
    # checked again at commit, including simultaneous edits from other tabs.
    from ml.run_validation import correct_image
    if model_name == "sam3" and method == "model" and not any(r.get("clicks") for r in regions):
        correct_image(source, regions, classes, data["mode"], directory)
    else:
        correct_image(source, regions, classes, data["mode"], directory, model_name, method)
    atomic_json(directory / "preview.json", {"datasetId": dataset_id, "itemId": item_id,
                "revision": revision, "regions": regions, "model": model_name, "method": method, "createdAt": now()})
    return {"previewId": token, "annotatedUrl": f"/datasets/{dataset_id}/previews/{token}/image",
            "objects": len(regions)}


def commit_correction(dataset_id: str, item_id: str, token: str, revision: int) -> dict:
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        item = get_item(data, item_id)
        root = Path(data["directory"])
        folder = contained(root, f".edits/{token}")
        preview = read_json(folder / "preview.json", {})
        if (preview.get("datasetId") != dataset_id or preview.get("itemId") != item_id
                or preview.get("revision") != revision or item.get("deleted")):
            raise HTTPException(409, "This correction preview is stale. Generate a new preview.")
        if not (folder / "annotated.jpg").is_file() or not (folder / "label.txt").is_file():
            raise HTTPException(404, "Correction files were not found.")
        # Versioned files + a single atomic manifest switch make the update
        # crash-safe; originals and prior corrections remain recoverable.
        annotated = f"Annotated/Pass/corrections/{item_id}/{token}.jpg"
        label = f"Labels/corrections/{item_id}/{token}.txt"
        import shutil
        for source_name, relative in (("annotated.jpg", annotated), ("label.txt", label)):
            target = contained(root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(folder / source_name, target)
        item.setdefault("history", []).append({"annotated": item["annotated"], "label": item["label"], "at": now()})
        item.update(annotated=annotated, label=label, status="Pass", edited=True,
                    detections=preview.get("objects", len(preview["regions"])), error=None,
                    correctionModel=preview.get("model", "sam3"))
        data["revision"] += 1
        data["updatedAt"] = now()
        save_dataset(data)
        return summary(data)


def preview_reprocess(dataset_id, item_id, revision, model_name, confidence):
    from services.model_runtime import runtime
    from ml.run_annotation import annotate_image
    import shutil
    with LOCK:
        data = get_dataset(dataset_id)
        require_editable(data, revision)
        item = get_item(data, item_id)
        if item.get("deleted"): raise HTTPException(409, "Restore the image first.")
        if data.get("annotationModel") == "sam2" and model_name != "sam2": raise HTTPException(422, "Choose SAM2 for this source.")
        token = uuid.uuid4().hex
        directory = contained(Path(data["directory"]), f".edits/{token}")
        directory.mkdir(parents=True)
        raw = contained(Path(data["directory"]), item["raw"])
    with runtime.inference_lock:
        result = annotate_image(runtime.annotation_model(model_name), raw,
            {"mode": data["mode"], "prompts": data["classes"], "annotation_model": model_name,
             "confidence_threshold": confidence}, directory)
    if not result["detections"]: raise ValueError("No objects found. Use the ROI/click editor to correct this image.")
    for source, target in ((directory.parent / result["annotated"], directory / "annotated.jpg"),
                           (directory.parent / result["label"], directory / "label.txt")):
        if source.resolve() != target.resolve(): shutil.copy2(source, target)
    atomic_json(directory / "preview.json", {"datasetId": dataset_id, "itemId": item_id,
        "revision": revision, "regions": [], "objects": result["detections"], "model": model_name, "createdAt": now()})
    return {"previewId": token, "objects": result["detections"], "annotatedUrl": f"/datasets/{dataset_id}/previews/{token}/image"}
