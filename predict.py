"""Detection/segmentation inference adapted from the two user-supplied video scripts."""
import json
import os
import sys
import time
from pathlib import Path
import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ml.train import save
from services.video_encoding import browser_video_size, normalize_video_frame, open_browser_video_writer


def render(frame, result, mode, draw_boxes=True, alpha=.35):
    painted = frame.copy()
    objects = []
    boxes = result.boxes
    polygons = result.masks.xy if result.masks is not None else []
    count = len(boxes) if boxes is not None else 0
    if mode == "segmentation" and count and len(polygons) != count:
        raise ValueError("Segmentation checkpoint returned boxes without matching masks.")
    for i in range(count):
        box = boxes[i]
        confidence, cid = float(box.conf[0]), int(box.cls[0])
        xyxy = list(map(float, box.xyxy[0].tolist()))
        name = result.names[cid]
        entry = {"classId": cid, "label": name, "confidence": confidence, "box": xyxy}
        color = ((cid * 79 + 60) % 256, (cid * 131 + 220) % 256, (cid * 53 + 80) % 256)
        if mode == "segmentation":
            poly = np.asarray(polygons[i])
            if len(poly) < 3: raise ValueError("Invalid segmentation polygon.")
            entry["polygon"] = poly.tolist()
            pts = np.rint(poly).astype(np.int32)
            mask = np.zeros(frame.shape[:2], np.uint8)
            cv2.fillPoly(mask, [pts], 1)
            # Blend only the mask pixels; the supplied script blended the whole frame.
            inside = mask.astype(bool)
            painted[inside] = (painted[inside] * (1 - alpha) + np.asarray(color) * alpha).astype(np.uint8)
            cv2.polylines(painted, [pts], True, color, 2, cv2.LINE_AA)
        x1, y1, x2, y2 = map(int, xyxy)
        if draw_boxes: cv2.rectangle(painted, (x1, y1), (x2, y2), color, 2)
        cv2.putText(painted, f"{name} {confidence:.2f}", (max(0, x1), max(18, y1 - 6)), cv2.FONT_HERSHEY_SIMPLEX, .6, color, 2)
        objects.append(entry)
    return painted, objects


def main(directory):
    from ultralytics import YOLO
    directory = Path(directory)
    request = json.loads((directory / "request.json").read_text(encoding="utf-8"))
    started = time.monotonic()
    print(f"Loading model: {request['model']['path']}", flush=True)
    model = YOLO(request["model"]["path"])
    mode = request["model"]["mode"]
    if model.task != ("segment" if mode == "segmentation" else "detect"):
        raise ValueError("Checkpoint task does not match the registered model.")
    names = [model.names[i] for i in range(len(model.names))]
    if names != request["model"]["classes"]: raise ValueError("Checkpoint class mapping changed.")
    config = request["config"]
    processed = total_objects = 0
    capture = writer = mp4_writer = None
    output = directory / "output"
    output.mkdir(exist_ok=True)
    assets = []
    cancelled = lambda: (directory / "cancel.request").exists()
    def checkpoint():
        save(directory / "result.json", {"processed": processed, "objects": total_objects, "assets": assets})
    print(f"Input: {request.get('source', request['sourceType'])} | {mode} | confidence={config['confidence']} | image size={config['imageSize']}", flush=True)
    def infer(frame, name, total, report):
        nonlocal processed, total_objects
        result = model.predict(source=frame, imgsz=config["imageSize"], conf=config["confidence"],
            iou=config["iou"], device=0, verbose=False, retina_masks=mode == "segmentation")[0]
        annotated, objects = render(frame, result, mode, config["drawBoxes"], config["maskAlpha"])
        processed += 1
        total_objects += len(objects)
        report.write(json.dumps({"index": processed - 1, "name": name, "objects": objects}) + "\n")
        report.flush()
        temporary = directory / "preview-next.jpg"
        if not cv2.imwrite(str(temporary), annotated): raise OSError("Cannot save inference preview.")
        try:
            temporary.replace(directory / "preview.jpg")
        except PermissionError:
            # Windows may still be streaming the previous thumbnail. Keep it
            # for this refresh; the real output and JSONL record are retained.
            pass
        save(directory / "progress.json", {"processed": processed, "total": total,
            "objects": total_objects, "current": name, "phase": "inference",
            "elapsedSeconds": round(time.monotonic() - started, 2)})
        if processed == 1 or processed % 25 == 0:
            print(f"Processed {processed}/{total}: {name} | {total_objects} objects | {time.monotonic() - started:.1f}s", flush=True)
        return annotated
    try:
        with (directory / "predictions.jsonl").open("w", encoding="utf-8") as report:
            if request["sourceType"] == "video":
                path = Path(request["source"])
                capture = cv2.VideoCapture(str(path))
                if not capture.isOpened(): raise ValueError("Cannot open the selected video.")
                fps = capture.get(cv2.CAP_PROP_FPS) or 30
                total = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
                size = browser_video_size(int(capture.get(cv2.CAP_PROP_FRAME_WIDTH)), int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT)))
                target = output / f"{path.stem}_{mode}.webm"
                writer = open_browser_video_writer(target, fps, size)
                mp4_target = output / f"{path.stem}_{mode}.mp4"
                mp4_writer = cv2.VideoWriter(str(mp4_target), cv2.VideoWriter_fourcc(*"mp4v"), fps, size)
                if not mp4_writer.isOpened():
                    raise RuntimeError("OpenCV could not open the MP4 output writer.")
                while True:
                    if cancelled(): break
                    ok, frame = capture.read()
                    if not ok: break
                    annotated = normalize_video_frame(infer(frame, f"frame_{processed:06d}", total, report), size)
                    writer.write(annotated)
                    mp4_writer.write(annotated)
                if not cancelled() and (processed == 0 or (total > 0 and processed < total)):
                    raise ValueError(f"Video decoding ended early: {processed}/{total} frames.")
                writer.release(); writer = None
                mp4_writer.release(); mp4_writer = None
                if processed:
                    assets = [{"name": mp4_target.name, "path": f"output/{mp4_target.name}", "type": "video", "playbackPath": f"output/{target.name}"}]
            else:
                assets = []
                for i, value in enumerate(request["files"]):
                    if cancelled(): break
                    path = Path(value)
                    frame = cv2.imread(str(path))
                    if frame is None: raise ValueError(f"Cannot decode {path.name}; inference is incomplete.")
                    annotated = infer(frame, path.name, len(request["files"]), report)
                    name = f"{i:06d}_{path.stem}_{mode}.jpg"
                    if not cv2.imwrite(str(output / name), annotated): raise OSError("Cannot write inference result.")
                    assets.append({"name": path.name, "path": f"output/{name}", "type": "image"})
                    checkpoint()
        checkpoint()
        print(f"{'Stopped' if cancelled() else 'Completed'}: {processed} processed, {total_objects} objects, {len(assets)} output(s). Saved to {output}", flush=True)
    finally:
        if capture is not None: capture.release()
        if writer is not None: writer.release()
        if mp4_writer is not None: mp4_writer.release()


if __name__ == "__main__":
    from process_guard import watch_parent
    watch_parent()
    os.environ.setdefault("YOLO_AUTOINSTALL", "false")
    main(sys.argv[1])
