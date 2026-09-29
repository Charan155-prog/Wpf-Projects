from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np
import torch
from PIL import Image

from services.model_runtime import runtime
from ml.run_annotation import attach_roi_box, draw_annotation, label_lines, normalized_polygon


def correct_image(source: Path, regions: list, classes: list[str], mode: str, output: Path, model_name: str = "sam3", method: str = "model") -> None:
    image = Image.open(source).convert("RGB")
    width, height = image.size
    detections, scores = [], []
    with runtime.inference_lock, torch.inference_mode():
        processor = runtime.annotation_model(model_name) if method == "model" else None
        if method == "model" and model_name == "sam3": processor.set_confidence_threshold(0.0)
        if method == "model" and model_name == "sam2": processor.set_image(cv2.cvtColor(np.asarray(image), cv2.COLOR_RGB2BGR), str(source.resolve()) + str(source.stat().st_mtime_ns))
        from contextlib import nullcontext
        context = torch.autocast(device_type="cuda", dtype=torch.bfloat16 if torch.cuda.get_device_properties(0).major >= 8 else torch.float16) if method == "model" and model_name == "sam3" else nullcontext()
        with context:
            for region in regions:
                roi = normalized_polygon(region["points"], width, height)
                gate = np.zeros((height, width), dtype=np.uint8)
                cv2.fillPoly(gate, [np.rint(roi).astype(np.int32)], 1)
                if gate.sum() < 4:
                    raise ValueError("The drawn region is too small. Draw around the whole object.")
                clicks = [[p[0] * (width - 1), p[1] * (height - 1)] for p in region.get("clicks", [])]
                labels = region.get("clickLabels", [])
                if method == "manual":
                    masks, confidences = [gate], [1.0]
                elif model_name == "sam2":
                    masks = processor.predict(np.r_[roi.min(axis=0), roi.max(axis=0)], clicks, labels)
                    confidences = [1.0] * len(masks)  # no predicted IoU from Ultralytics SAM2
                else:
                    state = attach_roi_box(processor, processor.set_image(image), roi, width, height)
                    result = processor.set_text_prompt(prompt=classes[region["classId"]], state=state)
                    masks = result["masks"].detach().cpu().numpy()
                    confidences = result["scores"].detach().float().cpu().numpy()
                proposals = []
                for mask, score in zip(masks, confidences):
                    binary = np.asarray(mask).squeeze() > 0
                    inside = binary & (gate > 0)
                    intersection = int(inside.sum())
                    if intersection < 4:
                        continue
                    if any(bool(inside[int(y), int(x)]) != bool(label) for (x, y), label in zip(clicks, labels)):
                        continue
                    # Prefer the mask which fits the selected object, not a
                    # larger object behind it. Never retain pixels outside ROI.
                    iou = intersection / max(1, int((binary | (gate > 0)).sum()))
                    proposals.append((iou, float(score), inside))
                if not proposals:
                    raise ValueError("No mask matches this region and its clicks. Adjust prompts or draw an exact manual polygon.")
                _, confidence, binary = max(proposals, key=lambda proposal: (proposal[0], proposal[1]))
                contours, _ = cv2.findContours(binary.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                positives = [p for p, label in zip(clicks, labels) if label]
                if positives:
                    contours = [c for c in contours if all(cv2.pointPolygonTest(c, (float(x), float(y)), False) >= 0 for x, y in positives)]
                    if not contours:
                        raise ValueError("Foreground clicks span disconnected regions. Draw separate object ROIs.")
                contour = max(contours, key=cv2.contourArea)
                if len(contour) < 3 or cv2.contourArea(contour) < 1:
                    raise ValueError("The predicted object is too small to form a valid annotation.")
                # One ROI is one object. Remove disconnected noise islands.
                mask = np.zeros((height, width), dtype=np.uint8)
                cv2.drawContours(mask, [contour], -1, 1, cv2.FILLED)
                if any(bool(mask[int(y), int(x)]) != bool(label) for (x, y), label in zip(clicks, labels)):
                    raise ValueError("The polygon cannot preserve all clicks (for example, a hole). Refine the object outline.")
                ys, xs = np.where(mask > 0)
                detections.append((region["classId"], mask, np.array([xs.min(), ys.min(), xs.max(), ys.max()])))
                scores.append(confidence)
    # SAM2/manual refinement has no calibrated mask confidence. Never display
    # the internal ranking placeholder as a perfect 1.00 confidence.
    rendered = draw_annotation(cv2.cvtColor(np.asarray(image), cv2.COLOR_RGB2BGR), detections, classes,
                               scores if method == "model" and model_name == "sam3" else None)
    if not cv2.imwrite(str(output / "annotated.jpg"), rendered):
        raise OSError("Could not write the correction preview.")
    (output / "label.txt").write_text("\n".join(label_lines(mode, detections, width, height)), encoding="utf-8")
