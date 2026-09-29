"""Adapted from supplied AutoAnnotation_1 sam_backend.py / annotator.py.

Full-frame SAM2 encoding with box and positive/negative click prompts.
Grounding DINO supplies text grounding; SAM2 itself has no text encoder.
"""
import os
from pathlib import Path, PureWindowsPath
import cv2
import numpy as np
import torch
from PIL import Image


def grounding_source(value):
    """Never pass an invalid local Windows path to the Hub repo resolver."""
    value = os.path.expandvars(os.path.expanduser(str(value).strip().strip('"').strip("'")))
    path = Path(value)
    local = path.exists() or path.is_absolute() or bool(PureWindowsPath(value).drive) or value.startswith(".") or "\\" in value
    if not local:
        return value, {}
    if not path.is_dir():
        raise ValueError(f"Grounding DINO folder not found on the backend PC: {value}. Set SAM2 text grounding in Settings to the complete downloaded model folder, or IDEA-Research/grounding-dino-base for online download.")
    # Also accept the Hugging Face cache repository folder itself.
    if not (path / "config.json").is_file():
        snapshots = path / "snapshots"
        candidates = sorted(snapshots.glob("*/config.json")) if snapshots.is_dir() else []
        if len(candidates) == 1:
            path = candidates[0].parent
        else:
            # Windows Extract All preserves the archive's enclosing folder.
            candidates = sorted(path.glob("*/config.json"))
            if len(candidates) == 1:
                path = candidates[0].parent
    required = ["config.json", "preprocessor_config.json", "tokenizer_config.json"]
    missing = [name for name in required if not (path / name).is_file()]
    if not any((path / name).is_file() for name in ("tokenizer.json", "vocab.txt")):
        missing.append("tokenizer.json or vocab.txt")
    if not any((path / name).is_file() for name in ("model.safetensors", "pytorch_model.bin", "model.safetensors.index.json", "pytorch_model.bin.index.json")):
        missing.append("model weights")
    if missing:
        raise ValueError(f"Incomplete Grounding DINO folder on backend PC: {path}. Missing: {', '.join(missing)}. Copy the complete model download, not only its weights.")
    return str(path.resolve()), {"local_files_only": True}


class Sam2Backend:
    def __init__(self):
        from core import SAM3_CHECKPOINT, SETTINGS_FILE, read_json
        from ultralytics.models.sam import SAM2Predictor
        settings = read_json(SETTINGS_FILE, {})
        checkpoint = settings.get("sam2Checkpoint") or os.getenv("SAIL_SAM2_CHECKPOINT", str(Path(SAM3_CHECKPOINT).parent.parent / "sam2/sam2.1_b.pt"))
        checkpoint = os.path.expandvars(os.path.expanduser(checkpoint))
        if not Path(checkpoint).is_file():
            raise ValueError("SAM2 checkpoint not found. Set its path in Settings (sam2.1_b.pt from AutoAnnotation_1).")
        self.predictor = SAM2Predictor(overrides=dict(task="segment", mode="predict", imgsz=1024,
            model=checkpoint, save=False, verbose=False, device=0, conf=0.0))
        self.key = None
        self.grounder = self.processor = None
        self.dino_path = settings.get("groundingDinoModel") or os.getenv("SAIL_GROUNDING_DINO_MODEL", "IDEA-Research/grounding-dino-base")

    def set_image(self, image, key):
        if self.key != key:
            self.predictor.reset_image()
            self.predictor.set_image(image)
            self.key = key
        self.shape = image.shape[:2]

    def predict(self, box, points=None, labels=None):
        kwargs = {"bboxes": [list(box)]}
        if points:
            kwargs.update(points=[points], labels=[labels])
        result = self.predictor(**kwargs)[0]
        masks = []
        if result.masks is not None:
            values = getattr(result.masks, "data", None)
            if values is not None:
                values = values.detach().cpu().numpy() if hasattr(values, "detach") else np.asarray(values)
                if values.ndim != 3 or tuple(values.shape[-2:]) != self.shape:
                    raise ValueError("SAM2 mask coordinates do not match the original image.")
                return [(mask > 0).astype(np.uint8) for mask in values]
            # Native polygons are in original image coordinates.
            for polygon in result.masks.xy:
                if len(polygon) < 3: continue
                mask = np.zeros(self.shape, np.uint8)
                cv2.fillPoly(mask, [np.rint(polygon).astype(np.int32)], 1)
                masks.append(mask)
        return masks

    @torch.inference_mode()
    def detect(self, image, prompts, threshold):
        if self.grounder is None:
            from transformers import AutoProcessor, AutoModelForZeroShotObjectDetection
            source, options = grounding_source(self.dino_path)
            processor = AutoProcessor.from_pretrained(source, **options)
            grounder = AutoModelForZeroShotObjectDetection.from_pretrained(source, **options).to("cuda").eval()
            self.processor, self.grounder = processor, grounder
        h, w = image.shape[:2]
        rgb = Image.fromarray(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
        # Evaluate each requested class explicitly. DINO phrase labels often
        # contain only a subset of a verbose prompt; string matching those
        # phrases against the complete caption silently loses valid classes.
        return self._detect_each_prompt(rgb, prompts, threshold, h, w)

    def _detect_each_prompt(self, rgb, prompts, threshold, h, w):
        proposals = []
        for class_id, prompt in enumerate(prompts):
            inputs = self.processor(images=rgb, text=prompt.lower().strip().rstrip(".") + ".", return_tensors="pt").to("cuda")
            outputs = self.grounder(**inputs)
            kwargs = dict(text_threshold=.20, target_sizes=[(h, w)])
            try:
                result = self.processor.post_process_grounded_object_detection(outputs, inputs.input_ids, threshold=threshold, **kwargs)[0]
            except TypeError:
                result = self.processor.post_process_grounded_object_detection(outputs, inputs.input_ids, box_threshold=threshold, **kwargs)[0]
            for box, score in zip(result["boxes"], result["scores"]):
                box = box.detach().cpu().numpy()
                box[[0, 2]] = np.clip(box[[0, 2]], 0, w - 1)
                box[[1, 3]] = np.clip(box[[1, 3]], 0, h - 1)
                if box[2] > box[0] and box[3] > box[1]:
                    proposals.append((class_id, box, float(score)))
        return proposals


def annotate(engine, image_path, request, output_dir, image=None, keep_frame: bool = False):
    from ml.run_annotation import normalized_polygon, save_annotation
    if image is None:
        image = cv2.imread(str(image_path))
    if image is None: raise ValueError(f"Cannot decode {image_path.name}")
    h, w = image.shape[:2]
    engine.set_image(image, str(image_path.resolve()) + str(image_path.stat().st_mtime_ns))
    roi_file = request.get("roi_source_file")
    reference_roi = roi_file and Path(roi_file).resolve() == image_path.resolve() and request.get("roi")
    if reference_roi:
        if len(request["prompts"]) != 1:
            raise ValueError("Use per-object class regions in Semi-Annotation for a multi-class ROI.")
        roi = normalized_polygon(request["roi"], w, h)
        proposals = [(0, np.r_[roi.min(axis=0), roi.max(axis=0)], 1.0)]
    else:
        proposals = engine.detect(image, request["prompts"], float(request.get("confidence_threshold", .25)))
    gate = np.ones((h, w), np.uint8)
    if request.get("aoi"):
        gate[:] = 0
        cv2.fillPoly(gate, [np.rint(normalized_polygon(request["aoi"], w, h)).astype(np.int32)], 1)
    detections, scores = [], []
    for cid, box, score in proposals:
        masks = engine.predict(box)
        if not masks: raise ValueError("SAM2 could not segment a grounded object; reprocess with an ROI.")
        mask = masks[0] * gate
        ys, xs = np.where(mask)
        if len(xs) < 4: continue
        detections.append((cid, mask, np.array([xs.min(), ys.min(), xs.max(), ys.max()])))
        scores.append(score)
    # Scores on text-grounded images belong to DINO, not predicted SAM2 IoU.
    return save_annotation(image, detections, [] if reference_roi else scores, image_path, request, output_dir, keep_frame=keep_frame)