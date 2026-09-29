from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path
from contextlib import contextmanager

import cv2
import numpy as np
import torch
from PIL import Image


def load_sam3():
    from sam3.model_builder import build_sam3_image_model
    from sam3.model.sam3_image_processor import Sam3Processor

    checkpoint = os.environ.get("SAIL_SAM3_CHECKPOINT")
    if not checkpoint or not Path(checkpoint).is_file():
        raise RuntimeError("SAIL_SAM3_CHECKPOINT must point to the downloaded sam3.pt checkpoint.")
    if Path(checkpoint).stat().st_size == 0:
        raise RuntimeError(
            "SAM3 checkpoint is empty (0 bytes). Re-download sam3.pt from the approved Hugging Face facebook/sam3 repository."
        )
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is unavailable in the native Conda environment. Update the NVIDIA driver "
            "to support this PyTorch CUDA build, then restart the API."
        )
    device_properties = torch.cuda.get_device_properties(0)
    if device_properties.major < 8 and os.getenv("SAIL_ALLOW_LEGACY_GPU") != "1":
        raise RuntimeError(
            "SAM3 interactive inference is disabled on this pre-Ampere GPU to prevent system-wide stalls. "
            "Use an Ampere-or-newer GPU or remote inference. Set SAIL_ALLOW_LEGACY_GPU=1 only for slow experimental runs."
        )
    model = build_sam3_image_model(checkpoint_path=checkpoint, device="cuda")
    return Sam3Processor(model, resolution=1008)


def normalized_polygon(points: list, width: int, height: int) -> np.ndarray:
    values = np.asarray(
        [[point["x"], point["y"]] if isinstance(point, dict) else point for point in points],
        dtype=np.float32,
    )
    if values.size == 0:
        return np.empty((0, 2), dtype=np.float32)
    values = values.reshape(-1, 2)
    if values.max() <= 1.0:
        values[:, 0] *= width
        values[:, 1] *= height
    return values


def attach_roi_box(processor, state: dict, roi: np.ndarray, width: int, height: int) -> dict:
    """Attach one positive SAM3 box without executing a redundant visual-only pass.

    SAM3 expects normalized ``[center_x, center_y, box_width, box_height]``.
    The UI sends a normalized polygon, which ``normalized_polygon`` converts
    to image pixels for drawing and AOI operations.  The previous code passed
    pixel-space ``[x1, y1, x2, y2]`` directly to SAM3, producing malformed
    geometric prompts and large unrelated masks.
    """
    x1, y1 = roi.min(axis=0)
    x2, y2 = roi.max(axis=0)
    box = [
        float(np.clip(((x1 + x2) * 0.5) / width, 0.0, 1.0)),
        float(np.clip(((y1 + y2) * 0.5) / height, 0.0, 1.0)),
        float(np.clip((x2 - x1) / width, 1.0 / width, 1.0)),
        float(np.clip((y2 - y1) / height, 1.0 / height, 1.0)),
    ]
    if "geometric_prompt" not in state:
        state["geometric_prompt"] = processor.model._get_dummy_prompt()
    boxes = torch.tensor(box, device=processor.device, dtype=torch.float32).view(1, 1, 4)
    labels = torch.ones((1, 1), device=processor.device, dtype=torch.bool)
    state["geometric_prompt"].append_boxes(boxes, labels)
    return state


@torch.inference_mode()
def reference_features(processor, request):
    """Encode the drawn object's appearance on its original image, once per setup."""
    source = request.get("roi_source_file")
    if not source or len(request.get("roi", [])) < 3:
        return None
    path = Path(source)
    key = (str(path.resolve()), path.stat().st_mtime_ns, json.dumps(request["roi"], sort_keys=True))
    cached = getattr(processor, "_sail_reference", None)
    if cached and cached[0] == key:
        return cached[1]
    with Image.open(path) as opened:
        reference = opened.convert("RGB")
    width, height = reference.size
    state = processor.set_image(reference)
    state = attach_roi_box(processor, state, normalized_polygon(request["roi"], width, height), width, height)
    _, features, positions, sizes = processor.model._get_img_feats(state["backbone_out"], processor.find_stage.img_ids)
    embedding, mask = processor.model.geometry_encoder(
        geo_prompt=state["geometric_prompt"], img_feats=features,
        img_sizes=sizes, img_pos_embeds=positions,
    )
    value = (embedding.detach(), mask.detach())
    processor._sail_reference = (key, value)
    return value


@contextmanager
def visual_reference_prompt(model, reference):
    """Supply source appearance through SAM3's visual prompt input, under the runtime lock."""
    if reference is None:
        yield
        return
    original = model._encode_prompt
    owned = model.__dict__.get("_encode_prompt")
    def encode(*args, **kwargs):
        kwargs["visual_prompt_embed"], kwargs["visual_prompt_mask"] = reference
        return original(*args, **kwargs)
    model._encode_prompt = encode
    try:
        yield
    finally:
        if owned is None:
            del model._encode_prompt
        else:
            model._encode_prompt = owned


ANNOTATION_COLORS = [
    (83, 92, 255),    # coral red (BGR)
    (67, 170, 104),   # green
    (225, 105, 65),   # blue
    (75, 180, 230),   # orange
    (187, 85, 168),   # purple
    (85, 205, 230),   # yellow
    (188, 125, 70),   # teal
    (128, 80, 235),   # pink
]

def draw_label(
    result: np.ndarray,
    text: str,
    center_x: int,
    object_top: int,
    color: tuple[int, int, int],
) -> None:
    """
    Draw a compact, high-contrast prompt label
    immediately above the visible object.
    """

    font = cv2.FONT_HERSHEY_SIMPLEX

    scale = 0.58
    text_thickness = 2

    padding_x = 7
    padding_y = 5

    (
        text_width,
        text_height,
    ), baseline = cv2.getTextSize(
        text,
        font,
        scale,
        text_thickness,
    )

    height, width = result.shape[:2]

    box_width = (
        text_width + padding_x * 2
    )

    box_height = (
        text_height
        + baseline
        + padding_y * 2
    )

    # Center the label horizontally and place it outside, immediately above
    # the visible object. Only fall back inside when the object touches the top.
    x = max(
        2,
        min(
            int(center_x - box_width / 2),
            width - box_width - 2,
        ),
    )
    y_above = int(object_top) - box_height - 6
    y = y_above if y_above >= 2 else min(height - box_height - 2, int(object_top) + 6)
    y = max(2, y)

    # Dark background for readability.
    cv2.rectangle(
        result,
        (x, y),
        (
            x + box_width,
            y + box_height,
        ),
        (18, 24, 32),
        -1,
    )

    # Colored border matching annotation.
    cv2.rectangle(
        result,
        (x, y),
        (
            x + box_width,
            y + box_height,
        ),
        color,
        2,
    )

    # White text.
    cv2.putText(
        result,
        text,
        (
            x + padding_x,
            y + padding_y + text_height,
        ),
        font,
        scale,
        (255, 255, 255),
        text_thickness,
        cv2.LINE_AA,
    )


def draw_annotation(
    image: np.ndarray,
    detections: list[
        tuple[
            int,
            np.ndarray,
            np.ndarray,
        ]
    ],
    prompts: list[str],
    scores: list[float] | None = None,
) -> np.ndarray:

    result = image.copy()

    for detection_index, (class_id, mask, box) in enumerate(detections):

        binary = (
            np.asarray(mask).squeeze()
            > 0
        )

        overlay = np.zeros_like(
            result
        )

        color = ANNOTATION_COLORS[
            class_id
            % len(ANNOTATION_COLORS)
        ]

        overlay[binary] = color

        result = cv2.addWeighted(
            result,
            1.0,
            overlay,
            0.38,
            0,
        )

        contours, _ = cv2.findContours(
            binary.astype(np.uint8),
            cv2.RETR_EXTERNAL,
            cv2.CHAIN_APPROX_SIMPLE,
        )

        cv2.drawContours(
            result,
            contours,
            -1,
            color,
            2,
        )

        if not contours:
            continue

        # IMPORTANT:
        # Use the actual visible segmentation
        # mask instead of the SAM3 model box.
        #
        # This keeps the prompt attached to
        # the actual object displayed to the user.

        contour = max(
            contours,
            key=cv2.contourArea,
        )

        x, object_top, contour_width, _contour_height = cv2.boundingRect(contour)
        center_x = x + contour_width // 2

        label = (
            prompts[class_id]
            if class_id < len(prompts)
            else f"class {class_id}"
        )
        if scores and detection_index < len(scores):
            label = f"{label} {scores[detection_index]:.2f}"

        draw_label(
            result,
            label[:56],
            center_x,
            object_top,
            color,
        )

    return result

def mask_polygon(mask):
    """Encode all external regions of one instance as one YOLO polygon.

    A YOLO row holds one contour. Join separate contours with a path traversed
    in both directions; this retains their boundaries without filling the
    convex hull between them or turning fragments into extra instances.
    Rasterization can add a one-pixel connector along the repeated path.
    """
    binary = (np.asarray(mask).squeeze() > 0).astype(np.uint8)
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    contours = [c.reshape(-1, 2) for c in contours if len(c) >= 3 and cv2.contourArea(c) > 0]
    if not contours:
        return np.empty((0, 2), dtype=np.int32)
    contours.sort(key=lambda c: (-cv2.contourArea(c), int(c[:, 1].min()), int(c[:, 0].min())))
    joined = contours[0]
    for contour in contours[1:]:
        nearest = (float("inf"), 0, 0)
        # Bound distance-matrix memory even for detailed freehand masks.
        for start in range(0, len(joined), 256):
            for other in range(0, len(contour), 256):
                delta = joined[start:start + 256, None].astype(np.float64) - contour[None, other:other + 256]
                distances = (delta * delta).sum(axis=2)
                i, j = np.unravel_index(np.argmin(distances), distances.shape)
                candidate = (float(distances[i, j]), start + int(i), other + int(j))
                if candidate < nearest:
                    nearest = candidate
        _, i, j = nearest
        loop = np.concatenate((contour[j:], contour[:j + 1]))
        joined = np.concatenate((joined[:i + 1], loop, joined[i:i + 1], joined[i + 1:]))
    return joined


def label_lines(mode: str, detections: list[tuple[int, np.ndarray, np.ndarray]], width: int, height: int) -> list[str]:
    lines: list[str] = []
    if mode == "detection":
        for class_id, _mask, box in detections:
            x1, y1, x2, y2 = np.asarray(box, dtype=float).tolist()
            center_x, center_y = (x1 + x2) / 2 / width, (y1 + y2) / 2 / height
            box_width, box_height = (x2 - x1) / width, (y2 - y1) / height
            lines.append(f"{class_id} {center_x:.6f} {center_y:.6f} {box_width:.6f} {box_height:.6f}")
        return lines
    for class_id, mask, _box in detections:
        contour = mask_polygon(mask)
        if len(contour) < 3:
            continue
        normalized = [f"{coordinate:.6f}" for point in contour for coordinate in (point[0] / width, point[1] / height)]
        lines.append(str(class_id) + " " + " ".join(normalized))
    return lines


def detection_overlays(detections, prompts: list[str], width: int, height: int, scores: list[float] | None = None) -> list[dict]:
    overlays = []
    for detection_index, (class_id, mask, _box) in enumerate(detections):
        binary = (np.asarray(mask).squeeze() > 0).astype(np.uint8)
        contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not contours:
            continue
        contour = max(contours, key=cv2.contourArea)
        epsilon = max(1.0, 0.002 * cv2.arcLength(contour, True))
        polygon = cv2.approxPolyDP(contour, epsilon, True).reshape(-1, 2)
        if len(polygon) < 3:
            continue
        overlays.append({
            "id": detection_index,
            "classId": class_id,
            "label": prompts[class_id] if class_id < len(prompts) else f"class {class_id}",
            "confidence": round(scores[detection_index], 4) if scores and detection_index < len(scores) else None,
            "polygon": [[round(float(x) / width, 6), round(float(y) / height, 6)] for x, y in polygon],
        })
    return overlays


def annotate_image(processor, image_path: Path, request: dict, output_dir: Path, image=None, keep_frame: bool = False) -> dict:
    from services.model_runtime import runtime
    # The persistent processor is mutable. Serialize batch inference and manual
    # corrections so their image/prompt states cannot leak into one another.
    with runtime.inference_lock:
        if request.get("annotation_model") == "sam2":
            from ml.sam2_backend import annotate
            return annotate(processor, image_path, request, output_dir, image=image, keep_frame=keep_frame)
        return _annotate_image(processor, image_path, request, output_dir, image=image, keep_frame=keep_frame)


def _annotate_image(processor, image_path: Path, request: dict, output_dir: Path, image=None, keep_frame: bool = False) -> dict:
    # A direct-video frame is already decoded in memory (the caller passes the
    # ndarray it just read from the video); reuse it instead of re-reading the
    # JPEG we only wrote to disk for the dataset's Raw folder. Every other
    # caller (folder/upload annotation, Semi-Annotation) still reads its file.
    if image is not None:
        pil_image = Image.fromarray(cv2.cvtColor(image, cv2.COLOR_BGR2RGB)) if isinstance(image, np.ndarray) else image
    else:
        pil_image = Image.open(image_path).convert("RGB")
    width, height = pil_image.size
    device_properties = torch.cuda.get_device_properties(0)
    inference_dtype = torch.bfloat16 if device_properties.major >= 8 else torch.float16
    requested_threshold = min(1.0, max(0.0, float(request.get("confidence_threshold", 0.0))))
    # Honor the selected threshold consistently in the model and exported
    # detections, including zero during interactive candidate inspection.
    confidence_threshold = requested_threshold
    processor.set_confidence_threshold(confidence_threshold)
    with torch.autocast(device_type="cuda", dtype=inference_dtype):
        reference = reference_features(processor, request)
        roi_source_file = request.get("roi_source_file")
        # A manual ROI is an object reference on one image, not a stationary
        # search area shared by a folder. Apply its geometric prompt only to
        # the source image on which the user drew it. Every other image is
        # searched globally using the text prompt, allowing the object to move.
        use_reference_roi = bool(
            roi_source_file
            and Path(roi_source_file).resolve() == image_path.resolve()
        )
        roi = normalized_polygon(
            request.get("roi", []) if use_reference_roi else [],
            width,
            height,
        )
        aoi = normalized_polygon(request.get("aoi", []), width, height)
        aoi_bounds = None
        aoi_mask = None
        if len(aoi) >= 4:
            aoi_x1, aoi_y1 = np.floor(aoi.min(axis=0)).astype(int)
            aoi_x2, aoi_y2 = np.ceil(aoi.max(axis=0)).astype(int)
            aoi_bounds = (
                max(0, aoi_x1), max(0, aoi_y1),
                min(width, aoi_x2), min(height, aoi_y2),
            )
            aoi_mask = np.zeros((height, width), dtype=np.uint8)
            cv2.fillPoly(aoi_mask, [np.rint(aoi).astype(np.int32)], 1)
        detections: list[tuple[int, np.ndarray, np.ndarray]] = []
        detection_scores: list[float] = []
        for class_id, prompt in enumerate(request["prompts"]):
            # SAM3 text prompting mutates its inference state.  Start from the
            # raw image for every prompt so later prompts cannot replace earlier
            # prompts (the former behaviour only returned the final prompt).
            state = processor.set_image(pil_image)
            if len(roi) >= 3 and reference is None:
                # Attach the correctly normalized positive box directly to
                # the state. Calling add_geometric_prompt here would execute
                # an unnecessary visual-only inference before text inference.
                state = attach_roi_box(processor, state, roi, width, height)
            with visual_reference_prompt(processor.model, reference):
                output = processor.set_text_prompt(prompt=prompt, state=state)
            prompt_masks = np.asarray(output["masks"].cpu())
            prompt_boxes = np.asarray(output["boxes"].cpu())
            score_output = output.get("scores")
            prompt_scores = (
                np.asarray(score_output.detach().float().cpu()).reshape(-1)
                if score_output is not None
                else np.ones(len(prompt_masks), dtype=np.float32)
            )
            if not len(prompt_masks) == len(prompt_boxes) == len(prompt_scores):
                raise ValueError("SAM3 returned inconsistent masks, boxes, and scores; review this image.")
            for mask, box, score in zip(prompt_masks, prompt_boxes, prompt_scores):
                if float(score) < confidence_threshold:
                    continue
                if aoi_bounds:
                    clipped = (np.asarray(mask).squeeze() > 0) * aoi_mask
                    if not np.any(clipped > 0):
                        continue
                    mask = clipped
                    ys, xs = np.where(clipped > 0)
                    box = np.asarray([xs.min(), ys.min(), xs.max(), ys.max()])
                detections.append((class_id, mask, box))
                detection_scores.append(float(score))
    image = cv2.cvtColor(np.asarray(pil_image), cv2.COLOR_RGB2BGR)
    return save_annotation(image, detections, detection_scores, image_path, request, output_dir, keep_frame=keep_frame)


def save_annotation(image, detections, detection_scores, image_path, request, output_dir, keep_frame: bool = False):
    height, width = image.shape[:2]
    lines = label_lines(request["mode"], detections, width, height)
    if len(lines) != len(detections):
        raise ValueError("Some masks cannot form valid labels. Reprocess this image in Semi-Annotation.")
    # The app renders SAM3 masks itself, keeping a color and visible name for
    # every user prompt rather than relying on the inference package overlay.
    annotated = draw_annotation(image, detections, request["prompts"], detection_scores)
    output_name = request.get("output_names", {}).get(str(image_path), image_path.name)
    output_stem = Path(output_name).stem
    annotated_path = output_dir / f"{output_stem}_annotated.jpg"
    label_path = output_dir / f"{output_stem}.txt"
    if not cv2.imwrite(str(annotated_path), annotated):
        raise OSError("Could not save annotated image.")
    label_path.write_text("\n".join(lines), encoding="utf-8")
    result = {
        "filename": output_name,
        "annotated": str(annotated_path.relative_to(output_dir.parent)),
        "label": str(label_path.relative_to(output_dir.parent)),
        "detections": int(len(detections)),
        "overlays": detection_overlays(detections, request["prompts"], width, height, detection_scores),
    }
    if keep_frame:
        # Handed back only for an in-process caller (the direct-video loop) to
        # write straight into its output video; never persisted to progress
        # JSON, which is why every other caller leaves keep_frame off.
        result["__frame__"] = annotated
    return result


def publish_results(job_dir: Path, items: list[dict]) -> None:
    metadata_path = job_dir / "job.json"
    if not metadata_path.is_file():
        return
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if metadata.get("previewOnly"):
        return
    project_directory = Path(metadata.get("projectDirectory", ""))
    if not project_directory:
        return
    annotated_directory = project_directory / "Annotated"
    pass_directory = annotated_directory / "Pass"
    fail_directory = annotated_directory / "Fail"
    labels_directory = project_directory / "Labels"
    pass_directory.mkdir(parents=True, exist_ok=True)
    fail_directory.mkdir(parents=True, exist_ok=True)
    labels_directory.mkdir(parents=True, exist_ok=True)
    for item in items:
        annotated_source = job_dir / item["annotated"]
        label_source = job_dir / item["label"]
        if annotated_source.is_file():
            destination = pass_directory if item.get("detections", 0) > 0 else fail_directory
            shutil.copy2(annotated_source, destination / annotated_source.name)
        if label_source.is_file():
            shutil.copy2(label_source, labels_directory / label_source.name)
        if metadata.get("datasetId"):
            from services.dataset_store import record_item
            record_item(metadata["datasetId"], item)


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: run_annotation.py <job_directory>")
    job_dir = Path(sys.argv[1]).resolve()
    request = json.loads((job_dir / "request.json").read_text(encoding="utf-8"))
    output_dir = job_dir / "output"
    output_dir.mkdir(exist_ok=True)
    processor = load_sam3()
    valid_extensions = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    if request.get("source_files"):
        image_paths = [Path(path) for path in request["source_files"]]
    else:
        image_paths = [path for path in sorted((job_dir / "input").iterdir()) if path.suffix.lower() in valid_extensions]
    image_paths = [path for path in image_paths if path.is_file() and path.suffix.lower() in valid_extensions]
    progress_path = job_dir / "progress.json"
    items = []
    progress_path.write_text(json.dumps({"state": "running", "completed": 0, "total": len(image_paths), "items": []}, indent=2), encoding="utf-8")
    for path in image_paths:
        items.append(annotate_image(processor, path, request, output_dir))
        progress_path.write_text(json.dumps({"state": "running", "completed": len(items), "total": len(image_paths), "items": items}, indent=2), encoding="utf-8")
    publish_results(job_dir, items)
    (job_dir / "result.json").write_text(json.dumps({"items": items}, indent=2), encoding="utf-8")
    progress_path.write_text(json.dumps({"state": "complete", "completed": len(items), "total": len(image_paths), "items": items}, indent=2), encoding="utf-8")
    print(f"Completed {len(items)} image(s).")


if __name__ == "__main__":
    main()