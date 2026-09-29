"""Explicit user-confirmed references, not generated or inferred ground truth."""
import hashlib
import json
import shutil
import uuid
from pathlib import Path

import numpy as np
from PIL import Image
from fastapi import HTTPException
from services import dataset_store as store


def reference_root(data):
    key = hashlib.sha256(json.dumps([data["projectName"], data["mode"], data["classes"]]).encode()).hexdigest()
    return store.WORK_ROOT / ".ground-truth" / key


def freeze(dataset_id, item_id, revision, confirmed):
    if confirmed is not True:
        raise HTTPException(422, "Explicit confirmation that every object and label is correct is required.")
    with store.LOCK:
        data = store.get_dataset(dataset_id)
        store.require_editable(data, revision)
        item = store.get_item(data, item_id)
        if data["state"] != "complete" or item.get("deleted") or item["status"] != "Pass":
            raise HTTPException(409, "Choose an included Pass image from a completed dataset.")
        raw = store.contained(Path(data["directory"]), item["raw"])
        label = store.contained(Path(data["directory"]), item["label"])
        store.validate_labels(label, data["mode"], len(data["classes"]))
        raw_hash, label_hash = store.file_hash(raw), store.file_hash(label)
        directory = reference_root(data) / raw_hash / uuid.uuid4().hex
        directory.mkdir(parents=True, exist_ok=False)
        shutil.copy2(raw, directory / ("raw" + raw.suffix))
        shutil.copy2(label, directory / "label.txt")
        if store.file_hash(directory / ("raw" + raw.suffix)) != raw_hash or store.file_hash(directory / "label.txt") != label_hash:
            raise HTTPException(409, "Files changed while saving reference; no reference was activated.")
        reference = {"id": directory.name, "datasetId": data["id"], "itemId": item_id, "revision": revision,
            "projectName": data["projectName"], "mode": data["mode"], "classes": data["classes"],
            "rawHash": raw_hash, "labelHash": label_hash, "filename": item["filename"], "createdAt": store.now(),
            "directory": str(directory), "confirmedByUser": True}
        store.atomic_json(directory / "reference.json", reference)
        store.atomic_json(directory.parent / "latest.json", {"id": directory.name})
        return reference


def lookup(data, raw_hash):
    directory = reference_root(data) / raw_hash
    pointer = store.read_json(directory / "latest.json", {})
    if not pointer: return None
    root = store.contained(directory, pointer["id"])
    reference = store.read_json(root / "reference.json", {})
    if not reference or reference.get("rawHash") != raw_hash:
        raise ValueError("Ground-truth reference is unavailable.")
    label = root / "label.txt"
    if not label.is_file() or store.file_hash(label) != reference["labelHash"]:
        raise ValueError("Ground-truth reference changed. Reconfirm it before automatic checks.")
    return reference, label


def rows(path):
    return [(int(parts[0]), np.asarray(list(map(float, parts[1:]))))
            for parts in (line.split() for line in path.read_text(encoding="utf-8-sig").splitlines()) if parts]


def compare(data, item, raw, label, hashes):
    found = lookup(data, hashes["rawHash"])
    if not found: return None
    reference, expected = found
    base = {**hashes, "rating": None, "referenceId": reference["id"], "referenceLabelHash": reference["labelHash"], "method": "user-ground-truth"}
    if reference["datasetId"] == data["id"] and reference["itemId"] == item["id"] and reference["labelHash"] == hashes["labelHash"]:
        return {**base, "status": "review", "reason": "This is the annotation used to create the reference. It is human-confirmed, not an independent automatic accuracy test; use Manual validation."}
    prediction, truth = rows(label), rows(expected)
    if len(prediction) != len(truth) or not truth or len(truth) > 30:
        return {**base, "status": "review", "reason": "Object count differs from ground truth, is empty, or exceeds the 30-instance comparison limit."}
    masks = None
    if data["mode"] == "segmentation":
        import cv2
        with Image.open(raw) as image: width, height = image.size
        scale = min(1, 1024 / max(width, height))
        width, height = max(1, round(width * scale)), max(1, round(height * scale))
        def raster(values):
            mask = np.zeros((height, width), dtype=np.uint8)
            points = np.rint(values.reshape(-1, 2) * [width-1, height-1]).astype(np.int32)
            cv2.fillPoly(mask, [points], 1)
            return mask.astype(bool)
        # Bound memory for dense scenes; mask IoU is rasterized at this recorded resolution.
        masks = ([raster(v) for _, v in prediction], [raster(v) for _, v in truth])
        base["maskResolution"] = [width, height]
        if any(np.count_nonzero(mask) < 16 for group in masks for mask in group):
            return {**base, "status": "review", "reason": "An object is too small for reliable rasterized mask comparison; review manually."}
    def iou(i, j):
        if prediction[i][0] != truth[j][0]: return 0.0
        if masks:
            a, b = masks[0][i], masks[1][j]
            union = np.count_nonzero(a | b)
            return float(np.count_nonzero(a & b) / union) if union else 0.0
        a, b = prediction[i][1], truth[j][1]
        lo = np.maximum(a[:2] - a[2:]/2, b[:2] - b[2:]/2)
        hi = np.minimum(a[:2] + a[2:]/2, b[:2] + b[2:]/2)
        intersection = float(np.prod(np.maximum(0, hi-lo)))
        union = float(np.prod(a[2:]) + np.prod(b[2:]) - intersection)
        return intersection / union if union > 0 else 0.0
    scores = [[iou(i, j) for j in range(len(truth))] for i in range(len(prediction))]
    # Maximum-cardinality matching ABOVE threshold, class-aware; do not filter a sum-optimal match afterwards.
    matches = {}
    def augment(i, seen):
        for j in sorted(range(len(truth)), key=lambda j: scores[i][j], reverse=True):
            if scores[i][j] < .9 or j in seen: continue
            seen.add(j)
            if j not in matches or augment(matches[j], seen):
                matches[j] = i
                return True
        return False
    accepted = all(augment(i, set()) for i in range(len(prediction)))
    return {**base, "status": "candidate" if accepted else "review", "threshold": .9,
            "reason": "Every instance matches an independently user-confirmed reference at class-aware IoU ≥ 0.90." if accepted else "Annotations differ from the user-confirmed reference (class-aware IoU threshold 0.90).",
            "matched": len(matches), "expected": len(truth)}


def verify_result_reference(data, result):
    """Export must use the same unchanged reference that produced the review result."""
    directory = reference_root(data) / result["rawHash"]
    root = store.contained(directory, result["referenceId"])
    label = root / "label.txt"
    pointer = store.read_json(directory / "latest.json", {})
    if pointer.get("id") != result["referenceId"] or not label.is_file() or store.file_hash(label) != result.get("referenceLabelHash"):
        raise HTTPException(409, "The ground-truth reference changed since review. Run automatic checks again.")
