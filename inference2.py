

# # """
# # validate_carton_detector.py

# # Runs the trained YOLO carton detector on a video and saves an annotated
# # output video with segmentation masks (+ boxes) drawn. Use this to visually
# # confirm detections on good or bad videos before wiring the model into the
# # inference pipeline.

# # IMPORTANT: This requires a segmentation model (e.g. weights trained from
# # yolov8n-seg.yaml / yolo11n-seg.yaml / etc). A plain detection model (e.g.
# # yolov8n.pt-style) has no mask output — results[0].masks will be None and
# # this script will raise a clear error rather than silently only drawing boxes.

# # Run:
# #     conda activate dime_env
# #     python validate_carton_detector.py
# # """

# # import cv2
# # import numpy as np
# # from pathlib import Path
# # from ultralytics import YOLO

# # # ---------------------------------------------------------------------------
# # # CONFIG
# # # ---------------------------------------------------------------------------
# # WEIGHTS      = "./best.pt"
# # VIDEO_IN     = "./DHTX1.mp4"  # <- swap to bad video to test
# # OUTPUT_DIR   = "./Output_segment"

# # CONF_THRESH  = 0.3
# # IMG_SIZE     = 640
# # MASK_ALPHA   = 0.35          # opacity of the fill inside the mask
# # MASK_COLOR   = (0, 255, 0)   # BGR
# # DRAW_BOXES   = True          # also draw the bbox + confidence label
# # DRAW_OUTLINE = True          # draw a crisp outline around the mask polygon
# # # ---------------------------------------------------------------------------


# # def draw_masks(frame, result, conf_thresh: float):
# #     """
# #     Draw accurate segmentation masks using the polygon contours in
# #     result.masks.xy (already rescaled to the original frame size by
# #     Ultralytics). This avoids the blurry/leaking edges you get from
# #     resizing the raw low-res mask tensor (result.masks.data) with
# #     naive interpolation.
# #     """
# #     if result.masks is None:
# #         return frame

# #     overlay = frame.copy()
# #     boxes = result.boxes
# #     polys = result.masks.xy  # list of (N,2) float arrays, one per detection

# #     for i, poly in enumerate(polys):
# #         conf = float(boxes.conf[i]) if boxes is not None else 1.0
# #         if conf < conf_thresh:
# #             continue
# #         if poly is None or len(poly) < 3:
# #             continue

# #         pts = np.round(poly).astype(np.int32).reshape(-1, 1, 2)

# #         # Filled, semi-transparent mask region
# #         cv2.fillPoly(overlay, [pts], MASK_COLOR)

# #         if DRAW_OUTLINE:
# #             cv2.polylines(frame, [pts], isClosed=True,
# #                            color=MASK_COLOR, thickness=2, lineType=cv2.LINE_AA)

# #     # Blend only where we drew, so untouched pixels stay identical
# #     frame = cv2.addWeighted(overlay, MASK_ALPHA, frame, 1 - MASK_ALPHA, 0)
# #     return frame


# # def draw_boxes(frame, result, conf_thresh: float):
# #     if result.boxes is None:
# #         return frame
# #     for box in result.boxes:
# #         conf = float(box.conf[0])
# #         if conf < conf_thresh:
# #             continue
# #         x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
# #         label = f"carton {conf:.2f}"
# #         cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 255, 0), 2)
# #         cv2.putText(frame, label, (x1, max(y1 - 8, 0)),
# #                     cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
# #     return frame


# # def validate() -> None:
# #     weights_path = Path(WEIGHTS)
# #     if not weights_path.exists():
# #         raise FileNotFoundError(f"Weights not found: {weights_path}")

# #     video_path = Path(VIDEO_IN)
# #     if not video_path.exists():
# #         raise FileNotFoundError(f"Video not found: {video_path}")

# #     out_dir = Path(OUTPUT_DIR)
# #     out_dir.mkdir(parents=True, exist_ok=True)
# #     out_path = out_dir / f"{video_path.stem}_segmentation.mp4"

# #     cap = cv2.VideoCapture(str(video_path))
# #     if not cap.isOpened():
# #         raise RuntimeError(f"Cannot open video: {video_path}")

# #     fps    = cap.get(cv2.CAP_PROP_FPS) or 30
# #     width  = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
# #     height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
# #     total  = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

# #     writer = cv2.VideoWriter(
# #         str(out_path),
# #         cv2.VideoWriter_fourcc(*"mp4v"),
# #         fps,
# #         (width, height),
# #     )

# #     model = YOLO(str(weights_path))

# #     if getattr(model, "task", None) != "segment":
# #         raise RuntimeError(
# #             f"Loaded model task is '{model.task}', not 'segment'. "
# #             "This weights file was not trained for segmentation, so it "
# #             "cannot produce masks — only boxes. Point WEIGHTS at a "
# #             "-seg model (e.g. trained from yolov8n-seg.yaml / "
# #             "yolo11n-seg.yaml) to get mask output."
# #         )

# #     print(f"Video  : {video_path.name}  ({width}x{height} @ {fps:.1f}fps, {total} frames)")
# #     print(f"Output : {out_path}")
# #     print("Processing...")

# #     frame_idx = 0
# #     while True:
# #         ret, frame = cap.read()
# #         if not ret:
# #             break

# #         results = model.predict(
# #             source  = frame,
# #             imgsz   = IMG_SIZE,
# #             conf    = CONF_THRESH,
# #             device  = 0,
# #             verbose = False,
# #             retina_masks = True,   # compute masks at full input res -> tighter, less leaking
# #         )

# #         result = results[0]
# #         annotated = draw_masks(frame.copy(), result, CONF_THRESH)
# #         if DRAW_BOXES:
# #             annotated = draw_boxes(annotated, result, CONF_THRESH)

# #         n_det = len(result.boxes) if result.boxes is not None else 0
# #         cv2.putText(annotated, f"frame {frame_idx}  detections: {n_det}",
# #                     (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 200, 255), 2)

# #         writer.write(annotated)
# #         frame_idx += 1

# #         if frame_idx % 50 == 0:
# #             print(f"  {frame_idx}/{total} frames done...")

# #     cap.release()
# #     writer.release()
# #     print(f"\nDone. Annotated video saved to:\n  {out_path}")


# # if __name__ == "__main__":
# #     validate()

# """
# validate_carton_detector.py

# Runs the trained YOLO carton detector on a video and saves an annotated
# output video with segmentation masks (+ boxes) drawn. Use this to visually
# confirm detections on good or bad videos before wiring the model into the
# inference pipeline.

# IMPORTANT: This requires a segmentation model (e.g. weights trained from
# yolov8n-seg.yaml / yolo11n-seg.yaml / etc). A plain detection model (e.g.
# yolov8n.pt-style) has no mask output — results[0].masks will be None and
# this script will raise a clear error rather than silently only drawing boxes.

# Run:
#     conda activate dime_env
#     python validate_carton_detector.py
# """

# import cv2
# import numpy as np
# from pathlib import Path
# from ultralytics import YOLO

# # ---------------------------------------------------------------------------
# # CONFIG
# # ---------------------------------------------------------------------------
# WEIGHTS      = "./best.pt"
# VIDEO_IN     = "./DHTX3.mp4"  # <- swap to bad video to test
# OUTPUT_DIR   = "./Output_dynamic2"

# CONF_THRESH  = 0.28
# IMG_SIZE     = 640
# MASK_ALPHA   = 0.45          # opacity of the fill inside the mask
# MASK_COLOR   = (0, 255, 0)   # BGR
# DRAW_BOXES   = True          # also draw the bbox + confidence label
# DRAW_OUTLINE = True          # draw a crisp outline around the mask polygon
# # ---------------------------------------------------------------------------


# def draw_masks(frame, result, conf_thresh: float):
#     """
#     Draw accurate segmentation masks using the polygon contours in
#     result.masks.xy (already rescaled to the original frame size by
#     Ultralytics). This avoids the blurry/leaking edges you get from
#     resizing the raw low-res mask tensor (result.masks.data) with
#     naive interpolation.
#     """
#     if result.masks is None:
#         return frame

#     overlay = frame.copy()
#     boxes = result.boxes
#     polys = result.masks.xy  # list of (N,2) float arrays, one per detection
#     names = result.names     # {class_id: class_name} from the model itself

#     for i, poly in enumerate(polys):
#         conf = float(boxes.conf[i]) if boxes is not None else 1.0
#         if conf < conf_thresh:
#             continue
#         if poly is None or len(poly) < 3:
#             continue

#         pts = np.round(poly).astype(np.int32).reshape(-1, 1, 2)

#         # Filled, semi-transparent mask region
#         cv2.fillPoly(overlay, [pts], MASK_COLOR)

#         if DRAW_OUTLINE:
#             cv2.polylines(frame, [pts], isClosed=True,
#                            color=MASK_COLOR, thickness=2, lineType=cv2.LINE_AA)

#             cls_id = int(boxes.cls[i]) if boxes is not None else -1
#             label = f"{names.get(cls_id, 'obj')} {conf:.2f}"
#             x_text, y_text = pts[:, 0, 0].min(), pts[:, 0, 1].min()
#             cv2.putText(frame, label, (int(x_text), max(int(y_text) - 8, 0)),
#                         cv2.FONT_HERSHEY_SIMPLEX, 0.6, MASK_COLOR, 2)

#     # Blend only where we drew, so untouched pixels stay identical
#     frame = cv2.addWeighted(overlay, MASK_ALPHA, frame, 1 - MASK_ALPHA, 0)
#     return frame


# def draw_boxes(frame, result, conf_thresh: float):
#     if result.boxes is None:
#         return frame
#     names = result.names  # {class_id: class_name} from the model itself
#     for box in result.boxes:
#         conf = float(box.conf[0])
#         if conf < conf_thresh:
#             continue
#         x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
#         cls_id = int(box.cls[0])
#         label = f"{names.get(cls_id, 'obj')} {conf:.2f}"
#         cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 255, 0), 2)
#         cv2.putText(frame, label, (x1, max(y1 - 8, 0)),
#                     cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
#     return frame


# def validate() -> None:
#     weights_path = Path(WEIGHTS)
#     if not weights_path.exists():
#         raise FileNotFoundError(f"Weights not found: {weights_path}")

#     video_path = Path(VIDEO_IN)
#     if not video_path.exists():
#         raise FileNotFoundError(f"Video not found: {video_path}")

#     out_dir = Path(OUTPUT_DIR)
#     out_dir.mkdir(parents=True, exist_ok=True)
#     out_path = out_dir / f"{video_path.stem}_segmentation.mp4"

#     cap = cv2.VideoCapture(str(video_path))
#     if not cap.isOpened():
#         raise RuntimeError(f"Cannot open video: {video_path}")

#     fps    = cap.get(cv2.CAP_PROP_FPS) or 30
#     width  = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
#     height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
#     total  = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

#     writer = cv2.VideoWriter(
#         str(out_path),
#         cv2.VideoWriter_fourcc(*"mp4v"),
#         fps,
#         (width, height),
#     )

#     model = YOLO(str(weights_path))

#     if getattr(model, "task", None) != "segment":
#         raise RuntimeError(
#             f"Loaded model task is '{model.task}', not 'segment'. "
#             "This weights file was not trained for segmentation, so it "
#             "cannot produce masks — only boxes. Point WEIGHTS at a "
#             "-seg model (e.g. trained from yolov8n-seg.yaml / "
#             "yolo11n-seg.yaml) to get mask output."
#         )

#     print(f"Video  : {video_path.name}  ({width}x{height} @ {fps:.1f}fps, {total} frames)")
#     print(f"Output : {out_path}")
#     print("Processing...")

#     frame_idx = 0
#     while True:
#         ret, frame = cap.read()
#         if not ret:
#             break

#         results = model.predict(
#             source  = frame,
#             imgsz   = IMG_SIZE,
#             conf    = CONF_THRESH,
#             device  = 0,
#             verbose = False,
#             retina_masks = True,   # compute masks at full input res -> tighter, less leaking
#         )

#         result = results[0]
#         annotated = draw_masks(frame.copy(), result, CONF_THRESH)
#         if DRAW_BOXES:
#             annotated = draw_boxes(annotated, result, CONF_THRESH)

#         n_det = len(result.boxes) if result.boxes is not None else 0
#         cv2.putText(annotated, f"frame {frame_idx}  detections: {n_det}",
#                     (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 200, 255), 2)

#         writer.write(annotated)
#         frame_idx += 1

#         if frame_idx % 50 == 0:
#             print(f"  {frame_idx}/{total} frames done...")

#     cap.release()
#     writer.release()
#     print(f"\nDone. Annotated video saved to:\n  {out_path}")


# if __name__ == "__main__":
#     validate()


"""
validate_carton_detector.py

Runs the trained YOLO carton detector on a video and saves an annotated
output video with segmentation masks (+ boxes) drawn. Use this to visually
confirm detections on good or bad videos before wiring the model into the
inference pipeline.

IMPORTANT: This requires a segmentation model (e.g. weights trained from
yolov8n-seg.yaml / yolo11n-seg.yaml / etc). A plain detection model (e.g.
yolov8n.pt-style) has no mask output — results[0].masks will be None and
this script will raise a clear error rather than silently only drawing boxes.

Run:
    conda activate dime_env
    python validate_carton_detector.py
"""

import cv2
import numpy as np
from pathlib import Path
from ultralytics import YOLO

# ---------------------------------------------------------------------------
# CONFIG
# ---------------------------------------------------------------------------
WEIGHTS      = "./best.pt"
VIDEO_IN     = "./front_dailies_5.mp4"  # <- swap to bad video to test
OUTPUT_DIR   = "./inf2"

CONF_THRESH  = 0.9
IOU_THRESH   = 0.45           # NMS IoU threshold - lower = more aggressive dup suppression
IMG_SIZE     = 640
MASK_ALPHA   = 0.35          # opacity of the fill inside the mask
MASK_COLOR   = (0, 255, 0)   # BGR
DRAW_BOXES   = True          # also draw the bbox + confidence label
DRAW_OUTLINE = True          # draw a crisp outline around the mask polygon
# --------------------------------------------------------------------------




def draw_masks(frame, result, conf_thresh: float):
    """
    Draw accurate segmentation masks using the polygon contours in
    result.masks.xy (already rescaled to the original frame size by
    Ultralytics). This avoids the blurry/leaking edges you get from
    resizing the raw low-res mask tensor (result.masks.data) with
    naive interpolation.
    """
    if result.masks is None:
        return frame

    overlay = frame.copy()
    boxes = result.boxes
    polys = result.masks.xy  # list of (N,2) float arrays, one per detection
    names = result.names     # {class_id: class_name} from the model itself

    for i, poly in enumerate(polys):
        conf = float(boxes.conf[i]) if boxes is not None else 1.0
        if conf < conf_thresh:
            continue
        if poly is None or len(poly) < 3:
            continue

        pts = np.round(poly).astype(np.int32).reshape(-1, 1, 2)

        # Filled, semi-transparent mask region
        cv2.fillPoly(overlay, [pts], MASK_COLOR)

        if DRAW_OUTLINE:
            cv2.polylines(frame, [pts], isClosed=True,
                           color=MASK_COLOR, thickness=2, lineType=cv2.LINE_AA)

            cls_id = int(boxes.cls[i]) if boxes is not None else -1
            label = f"{names.get(cls_id, 'obj')} {conf:.2f}"
            x_text, y_text = pts[:, 0, 0].min(), pts[:, 0, 1].min()
            cv2.putText(frame, label, (int(x_text), max(int(y_text) - 8, 0)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, MASK_COLOR, 2)

    # Blend only where we drew, so untouched pixels stay identical
    frame = cv2.addWeighted(overlay, MASK_ALPHA, frame, 1 - MASK_ALPHA, 0)
    return frame


def draw_boxes(frame, result, conf_thresh: float):
    if result.boxes is None:
        return frame
    names = result.names  # {class_id: class_name} from the model itself
    for box in result.boxes:
        conf = float(box.conf[0])
        if conf < conf_thresh:
            continue
        x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
        cls_id = int(box.cls[0])
        label = f"{names.get(cls_id, 'obj')} {conf:.2f}"
        cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 255, 0), 2)
        cv2.putText(frame, label, (x1, max(y1 - 8, 0)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
    return frame


def validate() -> None:
    weights_path = Path(WEIGHTS)
    if not weights_path.exists():
        raise FileNotFoundError(f"Weights not found: {weights_path}")

    video_path = Path(VIDEO_IN)
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    out_dir = Path(OUTPUT_DIR)
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"{video_path.stem}_segmentation.mp4"

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {video_path}")

    fps    = cap.get(cv2.CAP_PROP_FPS) or 30
    width  = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    total  = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    writer = cv2.VideoWriter(
        str(out_path),
        cv2.VideoWriter_fourcc(*"mp4v"),
        fps,
        (width, height),
    )

    model = YOLO(str(weights_path))

    if getattr(model, "task", None) != "segment":
        raise RuntimeError(
            f"Loaded model task is '{model.task}', not 'segment'. "
            "This weights file was not trained for segmentation, so it "
            "cannot produce masks — only boxes. Point WEIGHTS at a "
            "-seg model (e.g. trained from yolov8n-seg.yaml / "
            "yolo11n-seg.yaml) to get mask output."
        )

    print(f"Video  : {video_path.name}  ({width}x{height} @ {fps:.1f}fps, {total} frames)")
    print(f"Output : {out_path}")
    print("Processing...")

    frame_idx = 0
    while True:
        ret, frame = cap.read()
        if not ret:
            break

        results = model.predict(
            source  = frame,
            imgsz   = IMG_SIZE,
            conf    = CONF_THRESH,
            iou     = IOU_THRESH,
            device  = 0,
            verbose = False,
            retina_masks = True,   # compute masks at full input res -> tighter, less leaking
        )

        result = results[0]
        annotated = draw_masks(frame.copy(), result, CONF_THRESH)
        if DRAW_BOXES:
            annotated = draw_boxes(annotated, result, CONF_THRESH)

        n_det = len(result.boxes) if result.boxes is not None else 0
        cv2.putText(annotated, f"frame {frame_idx}  detections: {n_det}",
                    (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 200, 255), 2)

        writer.write(annotated)
        frame_idx += 1

        if frame_idx % 50 == 0:
            print(f"  {frame_idx}/{total} frames done...")

    cap.release()
    writer.release()
    print(f"\nDone. Annotated video saved to:\n  {out_path}")


if __name__ == "__main__":
    validate()