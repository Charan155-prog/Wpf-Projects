# # """
# # run_sam3.py
# # ============
# # Phase 3 of the pipeline, run as its own clean process — started fresh
# # AFTER generate_prompts.py has fully exited, so SAM3 gets the entire
# # GPU/host memory budget to itself (no leftover Qwen allocations to
# # fight with).

# # Reads:
# #   handoff/prompts.json  -> [[class_id, prompt_text], ...]

# # Then runs the same multi-class SAM3 detection + IoU dedup + edge
# # filtering + YOLO label export as multi_class_sam3_edge.py, just driven
# # by the auto-generated prompts instead of manual keyboard input.

# # ROI:
# #   At the start you're asked whether you want to draw a detection ROI.
# #   If yes, you draw one or more freehand regions on the FIRST image and
# #   detection is restricted to their union for EVERY image in the batch —
# #   useful when the same object type also appears near the frame edges and
# #   you don't want those edge copies annotated.
# # """

# # import json
# # import os
# # import sys
# # import time

# # import cv2
# # import numpy as np
# # import torch
# # from PIL import Image

# # # ─────────────────────────────────────────────────────────────────────────
# # # CONFIG — edit these (keep IMAGE_DIR / HANDOFF_DIR in sync with generate_prompts.py)
# # # ─────────────────────────────────────────────────────────────────────────


# # IMAGE_DIR = "./images_left_precision_30"
# # HANDOFF_DIR = "./handoff/"
# # OUTPUT_DIR = "./fi_ouput_left_precision_30"
# # CONFIDENCE = 0.25
# # EDGE_MARGIN = 2
# # IOU_THRESHOLD = 0.5

# # # Output naming: files are saved as f"{OUTPUT_PREFIX}_{index:0{OUTPUT_NUM_DIGITS}d}"
# # # e.g. Precision_30_0000.jpg / Precision_30_0000.txt, Precision_30_0001.jpg / .txt, ...
# # OUTPUT_PREFIX = "left_Precision_30"
# # OUTPUT_START_INDEX = 0
# # OUTPUT_NUM_DIGITS = 4

# # SAM3_CHECKPOINT_PATH = (
# #     "/home/omi/.cache/huggingface/hub/models--facebook--sam3/"
# #     "snapshots/3c879f39826c281e95690f02c7821c4de09afae7/sam3.pt"
# # )

# # # ─────────────────────────────────────────────────────────────────────────
# # # Helpers
# # # ─────────────────────────────────────────────────────────────────────────


# # def get_sorted_images(folder):
# #     exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
# #     # single file path given directly
# #     if os.path.isfile(folder):
# #         if os.path.splitext(folder)[1].lower() in exts:
# #             return [folder]
# #         else:
# #             return []
# #     # folder given — list all images inside
# #     paths = [
# #         os.path.join(folder, f)
# #         for f in os.listdir(folder)
# #         if os.path.splitext(f)[1].lower() in exts
# #     ]
# #     try:
# #         paths.sort(key=lambda p: int(os.path.splitext(os.path.basename(p))[0]))
# #     except ValueError:
# #         paths.sort()
# #     return paths


# # def draw_detection_roi(image_bgr):
# #     """
# #     Let the user draw a SINGLE freehand ROI region on `image_bgr`.
# #     Detection will be restricted to this region.

# #     Controls:
# #         - Hold LEFT mouse button and drag to draw a freehand outline
# #         - Press ENTER / SPACE to confirm and finish
# #         - (Re-drag any time before confirming to start the outline over)

# #     Returns:
# #         A binary uint8 mask (255 inside ROI, 0 outside), or None if the
# #         user drew nothing.
# #     """
# #     drawing = {"active": False}
# #     points = []
# #     current_canvas = image_bgr.copy()

# #     window = "Detection ROI  (drag=draw, Enter=confirm)"

# #     def callback(event, x, y, flags, param):
# #         nonlocal current_canvas, points
# #         if event == cv2.EVENT_LBUTTONDOWN:
# #             drawing["active"] = True
# #             points = [(x, y)]
# #             current_canvas = param.copy()
# #         elif event == cv2.EVENT_MOUSEMOVE and drawing["active"]:
# #             points.append((x, y))
# #             cv2.line(current_canvas, points[-2], points[-1], (0, 255, 0), 2)
# #             cv2.imshow(window, current_canvas)
# #         elif event == cv2.EVENT_LBUTTONUP:
# #             drawing["active"] = False
# #             if len(points) > 2:
# #                 points.append(points[0])
# #                 cv2.polylines(
# #                     current_canvas, [np.array(points)],
# #                     isClosed=True, color=(0, 255, 0), thickness=2,
# #                 )
# #                 cv2.imshow(window, current_canvas)

# #     cv2.namedWindow(window, cv2.WINDOW_NORMAL)
# #     cv2.resizeWindow(window, 1200, 800)
# #     cv2.setMouseCallback(window, callback, image_bgr.copy())

# #     print("Draw the detection ROI:")
# #     print("  Hold LEFT CLICK and drag around the region you want to detect INSIDE")
# #     print("  Re-drag any time to redo the outline")
# #     print("  Press ENTER to confirm\n")

# #     while True:
# #         cv2.imshow(window, current_canvas)
# #         key = cv2.waitKey(20) & 0xFF

# #         if key in (13, 32) and len(points) > 2:
# #             print("  ROI captured.\n")
# #             break

# #     cv2.destroyAllWindows()

# #     if len(points) <= 2:
# #         return None

# #     mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)
# #     cv2.fillPoly(mask, [np.array(points)], 255)
# #     if not mask.any():
# #         return None
# #     return mask


# # def is_touching_edge(mask, margin=5):
# #     if mask[:margin, :].any():
# #         return True
# #     if mask[-margin:, :].any():
# #         return True
# #     if mask[:, :margin].any():
# #         return True
# #     if mask[:, -margin:].any():
# #         return True
# #     return False


# # def overlay_masks_and_boxes(image_pil, masks, boxes, scores, class_ids, alpha=0.45):
# #     img_np = np.array(image_pil).copy()

# #     palette = [
# #         (80, 200, 80),
# #         (80, 120, 220),
# #         (220, 80, 80),
# #         (220, 180, 50),
# #         (180, 80, 220),
# #         (80, 210, 210),
# #         (220, 130, 50),
# #     ]

# #     for i in range(masks.shape[0]):
# #         colour = palette[class_ids[i] % len(palette)]
# #         mask = masks[i, 0].cpu().numpy()

# #         coloured = np.zeros_like(img_np)
# #         coloured[mask] = colour
# #         img_np = cv2.addWeighted(img_np, 1.0, coloured, alpha, 0)

# #         contours, _ = cv2.findContours(
# #             mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
# #         )
# #         cv2.drawContours(img_np, contours, -1, colour, 2)

# #     return img_np


# # def load_handoff():
# #     prompts_path = os.path.join(HANDOFF_DIR, "prompts.json")

# #     if not os.path.exists(prompts_path):
# #         print(f"Could not find {prompts_path}")
# #         print("Run generate_prompts.py first.")
# #         sys.exit(1)

# #     with open(prompts_path) as f:
# #         raw = json.load(f)
# #     prompts = [(int(cls_id), prompt) for cls_id, prompt in raw]
# #     prompts.sort(key=lambda x: x[0])

# #     print("Loaded class mapping:")
# #     for cls_id, p in prompts:
# #         print(f"  class {cls_id} -> \"{p}\"")
# #     print()

# #     return prompts


# # # ─────────────────────────────────────────────────────────────────────────
# # # Main
# # # ─────────────────────────────────────────────────────────────────────────


# # def main():
# #     prompts = load_handoff()

# #     image_paths = get_sorted_images(IMAGE_DIR)
# #     if not image_paths:
# #         print(f"No images found in {IMAGE_DIR}")
# #         sys.exit(1)
# #     print(f"Found {len(image_paths)} images in {IMAGE_DIR}\n")

# #     # ── Optional detection ROI ───────────────────────────────────────────
# #     # Ask the user whether they want to draw a region. If yes, detection is
# #     # confined to that single region for every image — this excludes copies
# #     # of the same object that appear near the frame edges.
# #     roi_mask = None
# #     draw_roi = input(
# #         "Do you want to draw an ROI? (detection will happen only inside it) (y/n): "
# #     ).strip().lower() == "y"

# #     if draw_roi:
# #         first_image = cv2.imread(image_paths[0])
# #         if first_image is None:
# #             print(f"Could not read {image_paths[0]} — skipping ROI, using full image.\n")
# #         else:
# #             roi_mask = draw_detection_roi(first_image)
# #             if roi_mask is None:
# #                 print("No ROI drawn — detection will run on the full image.\n")
# #             else:
# #                 print("Detection restricted to the drawn ROI region.\n")

# #     use_edge_filter = input("Enable edge filtering? (y/n): ").strip().lower() == "y"
# #     if use_edge_filter:
# #         print(f"Edge filtering ON — masks touching border within {EDGE_MARGIN}px will be rejected.\n")
# #     else:
# #         print("Edge filtering OFF — all masks will be accepted.\n")

# #     import sam3
# #     from sam3 import build_sam3_image_model
# #     from sam3.model.sam3_image_processor import Sam3Processor

# #     print("Loading SAM3 model...")
# #     sam3_root = os.path.join(os.path.dirname(sam3.__file__), "..")
# #     bpe_path = os.path.join(sam3_root, "sam3", "assets", "bpe_simple_vocab_16e6.txt.gz")

# #     torch.backends.cuda.matmul.allow_tf32 = True
# #     torch.backends.cudnn.allow_tf32 = True

# #     model = build_sam3_image_model(
# #         bpe_path=bpe_path,
# #         checkpoint_path=SAM3_CHECKPOINT_PATH,
# #         load_from_HF=False,
# #     )
# #     processor = Sam3Processor(model, confidence_threshold=CONFIDENCE)
# #     print(f"Model device : {next(model.parameters()).device}")
# #     if torch.cuda.is_available():
# #         print(f"GPU          : {torch.cuda.get_device_name(0)}")
# #     print("Model ready.\n")

# #     os.makedirs(OUTPUT_DIR, exist_ok=True)
# #     images_dir = os.path.join(OUTPUT_DIR, "images")
# #     labels_dir = os.path.join(OUTPUT_DIR, "labels")
# #     os.makedirs(images_dir, exist_ok=True)
# #     os.makedirs(labels_dir, exist_ok=True)

# #     timings = []

# #     with torch.autocast("cuda", dtype=torch.bfloat16):
# #         for idx, img_path in enumerate(image_paths):
# #             fname = os.path.basename(img_path)
# #             print(f"[{idx + 1}/{len(image_paths)}] {fname}", end=" ... ", flush=True)

# #             image = Image.open(img_path).convert("RGB")
# #             t_start = time.perf_counter()

# #             detect_image = image
# #             if roi_mask is not None:
# #                 img_np = np.array(image)
# #                 rm = roi_mask
# #                 if rm.shape[:2] != img_np.shape[:2]:
# #                     rm = cv2.resize(
# #                         rm, (img_np.shape[1], img_np.shape[0]),
# #                         interpolation=cv2.INTER_NEAREST,
# #                     )
# #                 masked = img_np.copy()
# #                 masked[rm == 0] = 0
# #                 detect_image = Image.fromarray(masked)

# #             all_masks, all_boxes, all_scores, all_class_ids = [], [], [], []

# #             for cls_id, prompt in prompts:
# #                 inference_state = processor.set_image(detect_image)
# #                 processor.reset_all_prompts(inference_state)
# #                 inference_state = processor.set_text_prompt(
# #                     state=inference_state, prompt=prompt
# #                 )

# #                 masks = inference_state.get("masks")
# #                 boxes = inference_state.get("boxes")
# #                 scores = inference_state.get("scores")

# #                 if masks is not None and masks.shape[0] > 0:
# #                     if use_edge_filter:
# #                         valid = [
# #                             i for i in range(masks.shape[0])
# #                             if not is_touching_edge(masks[i, 0].cpu().numpy(), margin=EDGE_MARGIN)
# #                         ]
# #                         if valid:
# #                             masks, boxes, scores = masks[valid], boxes[valid], scores[valid]
# #                         else:
# #                             masks = None
# #                     if masks is not None and masks.shape[0] > 0:
# #                         all_masks.append(masks)
# #                         all_boxes.append(boxes)
# #                         all_scores.append(scores)
# #                         all_class_ids.extend([cls_id] * masks.shape[0])
# #                         print(
# #                             f'\n    class {cls_id} "{prompt}": {masks.shape[0]} '
# #                             f'detection(s)  score: {float(scores[0].cpu()):.2f}',
# #                             end="",
# #                         )

# #             if all_masks:
# #                 final_masks = torch.cat(all_masks, dim=0)
# #                 final_boxes = torch.cat(all_boxes, dim=0)
# #                 final_scores = torch.cat(all_scores, dim=0)
# #                 final_class_ids = all_class_ids
# #             else:
# #                 final_masks = None

# #             if final_masks is not None and final_masks.shape[0] > 1:
# #                 boxes_np = final_boxes.cpu().numpy()
# #                 scores_np = final_scores.float().cpu().numpy()
# #                 n = len(boxes_np)

# #                 def compute_iou(a, b):
# #                     ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
# #                     ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
# #                     inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
# #                     if inter == 0:
# #                         return 0.0
# #                     area_a = (a[2] - a[0]) * (a[3] - a[1])
# #                     area_b = (b[2] - b[0]) * (b[3] - b[1])
# #                     return inter / (area_a + area_b - inter)

# #                 drop = set()
# #                 for i in range(n):
# #                     if i in drop:
# #                         continue
# #                     for j in range(i + 1, n):
# #                         if j in drop:
# #                             continue
# #                         if compute_iou(boxes_np[i], boxes_np[j]) > IOU_THRESHOLD:
# #                             loser = j if scores_np[i] >= scores_np[j] else i
# #                             drop.add(loser)

# #                 keep = [k for k in range(n) if k not in drop]
# #                 if drop:
# #                     print(f"  dedup: dropped {len(drop)} overlapping detection(s)", end="")
# #                 final_masks = final_masks[keep]
# #                 final_boxes = final_boxes[keep]
# #                 final_scores = final_scores[keep]
# #                 final_class_ids = [final_class_ids[k] for k in keep]

# #             if final_masks is None or final_masks.shape[0] == 0:
# #                 print("  no valid detections — saving original")
# #                 annotated = np.array(image)
# #             else:
# #                 annotated = overlay_masks_and_boxes(
# #                     image, final_masks, final_boxes, final_scores, final_class_ids
# #                 )

# #             stem = f"{OUTPUT_PREFIX}_{idx + OUTPUT_START_INDEX:0{OUTPUT_NUM_DIGITS}d}"

# #             out_name = stem + ".jpg"
# #             save_path = os.path.join(images_dir, out_name)
# #             cv2.imwrite(save_path, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR))

# #             elapsed = time.perf_counter() - t_start
# #             timings.append(elapsed)
# #             print(f"  time: {elapsed:.2f}s")

# #             label_name = stem + ".txt"
# #             label_path = os.path.join(labels_dir, label_name)
# #             # always write the label file — YOLO training expects one per image
# #             # even if empty (no detections); this also makes it easy to spot
# #             # which images had zero detections
# #             with open(label_path, "w") as lf:
# #                 if final_masks is not None and final_masks.shape[0] > 0:
# #                     img_w, img_h = image.size
# #                     for i in range(final_boxes.shape[0]):
# #                         x1, y1, x2, y2 = final_boxes[i].cpu().numpy().astype(float)
# #                         cx = ((x1 + x2) / 2) / img_w
# #                         cy = ((y1 + y2) / 2) / img_h
# #                         bw = (x2 - x1) / img_w
# #                         bh = (y2 - y1) / img_h
# #                         lf.write(f"{final_class_ids[i]} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")
# #                     print(f"    label: {label_path}  ({final_boxes.shape[0]} object(s))")
# #                 else:
# #                     print(f"    label: {label_path}  (empty — no detections)")

# #     print(f"\nDone. Annotated images : {images_dir}")
# #     print(f"      YOLO labels       : {labels_dir}")
# #     if timings:
# #         total = sum(timings)
# #         print("\n── Timing summary ──────────────────────")
# #         print(f"  Images processed : {len(timings)}")
# #         print(f"  Total time       : {total:.2f}s")
# #         print(f"  Avg per image    : {total / len(timings):.2f}s")
# #         print(f"  Fastest          : {min(timings):.2f}s")
# #         print(f"  Slowest          : {max(timings):.2f}s")
# #         print("────────────────────────────────────────")


# # if __name__ == "__main__":
# #     main()











# """
# run_sam3.py
# ============
# Phase 3 of the pipeline, run as its own clean process — started fresh
# AFTER generate_prompts.py has fully exited, so SAM3 gets the entire
# GPU/host memory budget to itself (no leftover Qwen allocations to
# fight with).

# Reads:
#   handoff/prompts.json  -> [[class_id, prompt_text], ...]

# Then runs the same multi-class SAM3 detection + IoU dedup + edge
# filtering + YOLO label export as multi_class_sam3_edge.py, just driven
# by the auto-generated prompts instead of manual keyboard input.

# ROI:
#   At the start you're asked whether you want to draw a detection ROI.
#   If yes, you draw one or more freehand regions on the FIRST image and
#   detection is restricted to their union for EVERY image in the batch —
#   useful when the same object type also appears near the frame edges and
#   you don't want those edge copies annotated.
# """

# import json
# import os
# import sys
# import time

# import cv2
# import numpy as np
# import torch
# from PIL import Image

# # ─────────────────────────────────────────────────────────────────────────
# # CONFIG — edit these (keep IMAGE_DIR / HANDOFF_DIR in sync with generate_prompts.py)
# # ─────────────────────────────────────────────────────────────────────────

# IMAGE_DIR = "./images_right_precision_30"
# HANDOFF_DIR = "./handoff/"
# OUTPUT_DIR = "./fi_ouput_right_precision_30"
# CONFIDENCE = 0.25
# EDGE_MARGIN = 2
# IOU_THRESHOLD = 0.5

# # Output naming: files are saved as f"{OUTPUT_PREFIX}_{index:0{OUTPUT_NUM_DIGITS}d}"
# # e.g. Precision_30_0000.jpg / Precision_30_0000.txt, Precision_30_0001.jpg / .txt, ...
# OUTPUT_PREFIX = "right_Precision_30"
# OUTPUT_START_INDEX = 0
# OUTPUT_NUM_DIGITS = 4

# SAM3_CHECKPOINT_PATH = (
#     "/home/omi/.cache/huggingface/hub/models--facebook--sam3/"
#     "snapshots/3c879f39826c281e95690f02c7821c4de09afae7/sam3.pt"
# )

# # ─────────────────────────────────────────────────────────────────────────
# # Helpers
# # ─────────────────────────────────────────────────────────────────────────


# def get_sorted_images(folder):
#     exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
#     # single file path given directly
#     if os.path.isfile(folder):
#         if os.path.splitext(folder)[1].lower() in exts:
#             return [folder]
#         else:
#             return []
#     # folder given — list all images inside
#     paths = [
#         os.path.join(folder, f)
#         for f in os.listdir(folder)
#         if os.path.splitext(f)[1].lower() in exts
#     ]
#     try:
#         paths.sort(key=lambda p: int(os.path.splitext(os.path.basename(p))[0]))
#     except ValueError:
#         paths.sort()
#     return paths


# def draw_detection_roi(image_bgr):
#     """
#     Let the user draw a SINGLE freehand ROI region on `image_bgr`.
#     Detection will be restricted to this region.

#     Controls:
#         - Hold LEFT mouse button and drag to draw a freehand outline
#         - Press ENTER / SPACE to confirm and finish
#         - (Re-drag any time before confirming to start the outline over)

#     Returns:
#         A binary uint8 mask (255 inside ROI, 0 outside), or None if the
#         user drew nothing.
#     """
#     drawing = {"active": False}
#     points = []
#     current_canvas = image_bgr.copy()

#     window = "Detection ROI  (drag=draw, Enter=confirm)"

#     def callback(event, x, y, flags, param):
#         nonlocal current_canvas, points
#         if event == cv2.EVENT_LBUTTONDOWN:
#             drawing["active"] = True
#             points = [(x, y)]
#             current_canvas = param.copy()
#         elif event == cv2.EVENT_MOUSEMOVE and drawing["active"]:
#             points.append((x, y))
#             cv2.line(current_canvas, points[-2], points[-1], (0, 255, 0), 2)
#             cv2.imshow(window, current_canvas)
#         elif event == cv2.EVENT_LBUTTONUP:
#             drawing["active"] = False
#             if len(points) > 2:
#                 points.append(points[0])
#                 cv2.polylines(
#                     current_canvas, [np.array(points)],
#                     isClosed=True, color=(0, 255, 0), thickness=2,
#                 )
#                 cv2.imshow(window, current_canvas)

#     cv2.namedWindow(window, cv2.WINDOW_NORMAL)
#     cv2.resizeWindow(window, 1200, 800)
#     cv2.setMouseCallback(window, callback, image_bgr.copy())

#     print("Draw the detection ROI:")
#     print("  Hold LEFT CLICK and drag around the region you want to detect INSIDE")
#     print("  Re-drag any time to redo the outline")
#     print("  Press ENTER to confirm\n")

#     while True:
#         cv2.imshow(window, current_canvas)
#         key = cv2.waitKey(20) & 0xFF

#         if key in (13, 32) and len(points) > 2:
#             print("  ROI captured.\n")
#             break

#     cv2.destroyAllWindows()

#     if len(points) <= 2:
#         return None

#     mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)
#     cv2.fillPoly(mask, [np.array(points)], 255)
#     if not mask.any():
#         return None
#     return mask


# def is_touching_edge(mask, margin=5):
#     if mask[:margin, :].any():
#         return True
#     if mask[-margin:, :].any():
#         return True
#     if mask[:, :margin].any():
#         return True
#     if mask[:, -margin:].any():
#         return True
#     return False


# def overlay_masks_and_boxes(image_pil, masks, boxes, scores, class_ids, alpha=0.45):
#     img_np = np.array(image_pil).copy()

#     palette = [
#         (80, 200, 80),
#         (80, 120, 220),
#         (220, 80, 80),
#         (220, 180, 50),
#         (180, 80, 220),
#         (80, 210, 210),
#         (220, 130, 50),
#     ]

#     for i in range(masks.shape[0]):
#         colour = palette[class_ids[i] % len(palette)]
#         mask = masks[i, 0].cpu().numpy()

#         coloured = np.zeros_like(img_np)
#         coloured[mask] = colour
#         img_np = cv2.addWeighted(img_np, 1.0, coloured, alpha, 0)

#         contours, _ = cv2.findContours(
#             mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
#         )
#         cv2.drawContours(img_np, contours, -1, colour, 2)

#     return img_np


# def load_handoff():
#     prompts_path = os.path.join(HANDOFF_DIR, "prompts.json")

#     if not os.path.exists(prompts_path):
#         print(f"Could not find {prompts_path}")
#         print("Run generate_prompts.py first.")
#         sys.exit(1)

#     with open(prompts_path) as f:
#         raw = json.load(f)
#     prompts = [(int(cls_id), prompt) for cls_id, prompt in raw]
#     prompts.sort(key=lambda x: x[0])

#     print("Loaded class mapping:")
#     for cls_id, p in prompts:
#         print(f"  class {cls_id} -> \"{p}\"")
#     print()

#     return prompts


# # ─────────────────────────────────────────────────────────────────────────
# # Main
# # ─────────────────────────────────────────────────────────────────────────


# def main():
#     prompts = load_handoff()

#     image_paths = get_sorted_images(IMAGE_DIR)
#     if not image_paths:
#         print(f"No images found in {IMAGE_DIR}")
#         sys.exit(1)
#     print(f"Found {len(image_paths)} images in {IMAGE_DIR}\n")

#     # ── Optional detection ROI ───────────────────────────────────────────
#     # Ask the user whether they want to draw a region. If yes, detection is
#     # confined to that single region for every image — this excludes copies
#     # of the same object that appear near the frame edges.
#     roi_mask = None
#     draw_roi = input(
#         "Do you want to draw an ROI? (detection will happen only inside it) (y/n): "
#     ).strip().lower() == "y"

#     if draw_roi:
#         first_image = cv2.imread(image_paths[0])
#         if first_image is None:
#             print(f"Could not read {image_paths[0]} — skipping ROI, using full image.\n")
#         else:
#             roi_mask = draw_detection_roi(first_image)
#             if roi_mask is None:
#                 print("No ROI drawn — detection will run on the full image.\n")
#             else:
#                 print("Detection restricted to the drawn ROI region.\n")

#     use_edge_filter = input("Enable edge filtering? (y/n): ").strip().lower() == "y"
#     if use_edge_filter:
#         print(f"Edge filtering ON — masks touching border within {EDGE_MARGIN}px will be rejected.\n")
#     else:
#         print("Edge filtering OFF — all masks will be accepted.\n")

#     import sam3
#     from sam3 import build_sam3_image_model
#     from sam3.model.sam3_image_processor import Sam3Processor

#     print("Loading SAM3 model...")
#     sam3_root = os.path.join(os.path.dirname(sam3.__file__), "..")
#     bpe_path = os.path.join(sam3_root, "sam3", "assets", "bpe_simple_vocab_16e6.txt.gz")

#     torch.backends.cuda.matmul.allow_tf32 = True
#     torch.backends.cudnn.allow_tf32 = True

#     model = build_sam3_image_model(
#         bpe_path=bpe_path,
#         checkpoint_path=SAM3_CHECKPOINT_PATH,
#         load_from_HF=False,
#     )
#     processor = Sam3Processor(model, confidence_threshold=CONFIDENCE)
#     print(f"Model device : {next(model.parameters()).device}")
#     if torch.cuda.is_available():
#         print(f"GPU          : {torch.cuda.get_device_name(0)}")
#     print("Model ready.\n")

#     os.makedirs(OUTPUT_DIR, exist_ok=True)
#     images_dir = os.path.join(OUTPUT_DIR, "images")
#     labels_dir = os.path.join(OUTPUT_DIR, "labels")
#     os.makedirs(images_dir, exist_ok=True)
#     os.makedirs(labels_dir, exist_ok=True)

#     timings = []

#     with torch.autocast("cuda", dtype=torch.bfloat16):
#         for idx, img_path in enumerate(image_paths):
#             fname = os.path.basename(img_path)
#             print(f"[{idx + 1}/{len(image_paths)}] {fname}", end=" ... ", flush=True)

#             image = Image.open(img_path).convert("RGB")
#             t_start = time.perf_counter()

#             detect_image = image
#             if roi_mask is not None:
#                 img_np = np.array(image)
#                 rm = roi_mask
#                 if rm.shape[:2] != img_np.shape[:2]:
#                     rm = cv2.resize(
#                         rm, (img_np.shape[1], img_np.shape[0]),
#                         interpolation=cv2.INTER_NEAREST,
#                     )
#                 masked = img_np.copy()
#                 masked[rm == 0] = 0
#                 detect_image = Image.fromarray(masked)

#             all_masks, all_boxes, all_scores, all_class_ids = [], [], [], []

#             for cls_id, prompt in prompts:
#                 inference_state = processor.set_image(detect_image)
#                 processor.reset_all_prompts(inference_state)
#                 inference_state = processor.set_text_prompt(
#                     state=inference_state, prompt=prompt
#                 )

#                 masks = inference_state.get("masks")
#                 boxes = inference_state.get("boxes")
#                 scores = inference_state.get("scores")

#                 if masks is not None and masks.shape[0] > 0:
#                     if use_edge_filter:
#                         valid = [
#                             i for i in range(masks.shape[0])
#                             if not is_touching_edge(masks[i, 0].cpu().numpy(), margin=EDGE_MARGIN)
#                         ]
#                         if valid:
#                             masks, boxes, scores = masks[valid], boxes[valid], scores[valid]
#                         else:
#                             masks = None
#                     if masks is not None and masks.shape[0] > 0:
#                         all_masks.append(masks)
#                         all_boxes.append(boxes)
#                         all_scores.append(scores)
#                         all_class_ids.extend([cls_id] * masks.shape[0])
#                         print(
#                             f'\n    class {cls_id} "{prompt}": {masks.shape[0]} '
#                             f'detection(s)  score: {float(scores[0].cpu()):.2f}',
#                             end="",
#                         )

#             if all_masks:
#                 final_masks = torch.cat(all_masks, dim=0)
#                 final_boxes = torch.cat(all_boxes, dim=0)
#                 final_scores = torch.cat(all_scores, dim=0)
#                 final_class_ids = all_class_ids
#             else:
#                 final_masks = None

#             if final_masks is not None and final_masks.shape[0] > 1:
#                 boxes_np = final_boxes.cpu().numpy()
#                 scores_np = final_scores.float().cpu().numpy()
#                 n = len(boxes_np)

#                 def compute_iou(a, b):
#                     ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
#                     ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
#                     inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
#                     if inter == 0:
#                         return 0.0
#                     area_a = (a[2] - a[0]) * (a[3] - a[1])
#                     area_b = (b[2] - b[0]) * (b[3] - b[1])
#                     return inter / (area_a + area_b - inter)

#                 drop = set()
#                 for i in range(n):
#                     if i in drop:
#                         continue
#                     for j in range(i + 1, n):
#                         if j in drop:
#                             continue
#                         if compute_iou(boxes_np[i], boxes_np[j]) > IOU_THRESHOLD:
#                             loser = j if scores_np[i] >= scores_np[j] else i
#                             drop.add(loser)

#                 keep = [k for k in range(n) if k not in drop]
#                 if drop:
#                     print(f"  dedup: dropped {len(drop)} overlapping detection(s)", end="")
#                 final_masks = final_masks[keep]
#                 final_boxes = final_boxes[keep]
#                 final_scores = final_scores[keep]
#                 final_class_ids = [final_class_ids[k] for k in keep]

#             if final_masks is None or final_masks.shape[0] == 0:
#                 print("  no valid detections — saving original")
#                 annotated = np.array(image)
#             else:
#                 annotated = overlay_masks_and_boxes(
#                     image, final_masks, final_boxes, final_scores, final_class_ids
#                 )

#             stem = f"{OUTPUT_PREFIX}_{idx + OUTPUT_START_INDEX:0{OUTPUT_NUM_DIGITS}d}"

#             out_name = stem + ".jpg"
#             save_path = os.path.join(images_dir, out_name)
#             cv2.imwrite(save_path, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR))

#             elapsed = time.perf_counter() - t_start
#             timings.append(elapsed)
#             print(f"  time: {elapsed:.2f}s")

#             label_name = stem + ".txt"
#             label_path = os.path.join(labels_dir, label_name)
#             # only write a label file when there's at least one detection —
#             # frames with zero detections get no label file at all
#             if final_masks is not None and final_masks.shape[0] > 0:
#                 with open(label_path, "w") as lf:
#                     img_w, img_h = image.size
#                     for i in range(final_boxes.shape[0]):
#                         x1, y1, x2, y2 = final_boxes[i].cpu().numpy().astype(float)
#                         cx = ((x1 + x2) / 2) / img_w
#                         cy = ((y1 + y2) / 2) / img_h
#                         bw = (x2 - x1) / img_w
#                         bh = (y2 - y1) / img_h
#                         lf.write(f"{final_class_ids[i]} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")
#                 print(f"    label: {label_path}  ({final_boxes.shape[0]} object(s))")
#             else:
#                 print(f"    label: skipped  (no detections)")

#     print(f"\nDone. Annotated images : {images_dir}")
#     print(f"      YOLO labels       : {labels_dir}")
#     if timings:
#         total = sum(timings)
#         print("\n── Timing summary ──────────────────────")
#         print(f"  Images processed : {len(timings)}")
#         print(f"  Total time       : {total:.2f}s")
#         print(f"  Avg per image    : {total / len(timings):.2f}s")
#         print(f"  Fastest          : {min(timings):.2f}s")
#         print(f"  Slowest          : {max(timings):.2f}s")
#         print("────────────────────────────────────────")


# if __name__ == "__main__":
#     main()


"""
run_sam3.py
============
Phase 3 of the pipeline, run as its own clean process — started fresh
AFTER generate_prompts.py has fully exited, so SAM3 gets the entire
GPU/host memory budget to itself (no leftover Qwen allocations to
fight with).

Prompts:
  You are asked at the start to type one text prompt per class directly
  in the terminal (e.g. "person", "car", "traffic light"). Class IDs are
  assigned in the order you type them, starting at 0. Press Enter on an
  empty line when you're done.

Then runs the same multi-class SAM3 detection + IoU dedup + edge
filtering + YOLO label export as multi_class_sam3_edge.py, just driven
by the typed-in prompts instead of a prompts.json handoff file.

ROI:
  At the start you're asked whether you want to draw a detection ROI.
  If yes, you draw one or more freehand regions on the FIRST image and
  detection is restricted to their union for EVERY image in the batch —
  useful when the same object type also appears near the frame edges and
  you don't want those edge copies annotated.
"""

import os
import sys
import time

import cv2
import numpy as np
import torch
from PIL import Image

# ─────────────────────────────────────────────────────────────────────────
# CONFIG — edit these (keep IMAGE_DIR / HANDOFF_DIR in sync with generate_prompts.py)
# ─────────────────────────────────────────────────────────────────────────



IMAGE_DIR = "./images_back_dailies_5"
OUTPUT_DIR = "./sam3_seg_images_back_dailies_5_roi"
CONFIDENCE = 0.26
EDGE_MARGIN = 2
IOU_THRESHOLD = 0.4

# Output naming: files are saved as f"{OUTPUT_PREFIX}_{index:0{OUTPUT_NUM_DIGITS}d}"
# e.g. Precision_30_0000.jpg / Precision_30_0000.txt, Precision_30_0001.jpg / .txt, ...
OUTPUT_PREFIX = "images_back_dailies_5"
OUTPUT_START_INDEX = 0
OUTPUT_NUM_DIGITS = 4

SAM3_CHECKPOINT_PATH = (
    "/home/omi/.cache/huggingface/hub/models--facebook--sam3/"
    "snapshots/3c879f39826c281e95690f02c7821c4de09afae7/sam3.pt"
)

# ─────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────


def get_sorted_images(folder):
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    # single file path given directly
    if os.path.isfile(folder):
        if os.path.splitext(folder)[1].lower() in exts:
            return [folder]
        else:
            return []
    # folder given — list all images inside
    paths = [
        os.path.join(folder, f)
        for f in os.listdir(folder)
        if os.path.splitext(f)[1].lower() in exts
    ]
    try:
        paths.sort(key=lambda p: int(os.path.splitext(os.path.basename(p))[0]))
    except ValueError:
        paths.sort()
    return paths


def draw_detection_roi(image_bgr):
    """
    Let the user draw a SINGLE freehand ROI region on `image_bgr`.
    Detection will be restricted to this region.

    Controls:
        - Hold LEFT mouse button and drag to draw a freehand outline
        - Press ENTER / SPACE to confirm and finish
        - (Re-drag any time before confirming to start the outline over)

    Returns:
        A binary uint8 mask (255 inside ROI, 0 outside), or None if the
        user drew nothing.
    """
    drawing = {"active": False}
    points = []
    current_canvas = image_bgr.copy()

    window = "Detection ROI  (drag=draw, Enter=confirm)"

    def callback(event, x, y, flags, param):
        nonlocal current_canvas, points
        if event == cv2.EVENT_LBUTTONDOWN:
            drawing["active"] = True
            points = [(x, y)]
            current_canvas = param.copy()
        elif event == cv2.EVENT_MOUSEMOVE and drawing["active"]:
            points.append((x, y))
            cv2.line(current_canvas, points[-2], points[-1], (0, 255, 0), 2)
            cv2.imshow(window, current_canvas)
        elif event == cv2.EVENT_LBUTTONUP:
            drawing["active"] = False
            if len(points) > 2:
                points.append(points[0])
                cv2.polylines(
                    current_canvas, [np.array(points)],
                    isClosed=True, color=(0, 255, 0), thickness=2,
                )
                cv2.imshow(window, current_canvas)

    cv2.namedWindow(window, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(window, 1200, 800)
    cv2.setMouseCallback(window, callback, image_bgr.copy())

    print("Draw the detection ROI:")
    print("  Hold LEFT CLICK and drag around the region you want to detect INSIDE")
    print("  Re-drag any time to redo the outline")
    print("  Press ENTER to confirm\n")

    while True:
        cv2.imshow(window, current_canvas)
        key = cv2.waitKey(20) & 0xFF

        if key in (13, 32) and len(points) > 2:
            print("  ROI captured.\n")
            break

    cv2.destroyAllWindows()

    if len(points) <= 2:
        return None

    mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)
    cv2.fillPoly(mask, [np.array(points)], 255)
    if not mask.any():
        return None
    return mask


def is_touching_edge(mask, margin=5):
    if mask[:margin, :].any():
        return True
    if mask[-margin:, :].any():
        return True
    if mask[:, :margin].any():
        return True
    if mask[:, -margin:].any():
        return True
    return False


def mask_to_yolo_polygon(mask, img_w, img_h, epsilon_frac=0.002):
    """
    Convert a binary mask into a single normalized polygon for YOLO
    segmentation labels: [x1, y1, x2, y2, ...] each in the 0-1 range.

    - Picks the largest external contour (handles masks with small
      noisy islands by ignoring anything but the main blob).
    - Simplifies the contour with approxPolyDP so labels aren't
      bloated with thousands of near-duplicate points.

    Returns None if no usable contour is found.
    """
    contours, _ = cv2.findContours(
        mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
    )
    if not contours:
        return None

    contour = max(contours, key=cv2.contourArea)
    if cv2.contourArea(contour) <= 0:
        return None

    epsilon = epsilon_frac * cv2.arcLength(contour, True)
    approx = cv2.approxPolyDP(contour, epsilon, True).reshape(-1, 2)

    if len(approx) < 3:
        return None

    norm_points = []
    for x, y in approx:
        norm_points.append(min(max(x / img_w, 0.0), 1.0))
        norm_points.append(min(max(y / img_h, 0.0), 1.0))
    return norm_points


def overlay_masks_and_boxes(image_pil, masks, boxes, scores, class_ids, alpha=0.45):
    img_np = np.array(image_pil).copy()

    palette = [
        (80, 200, 80),
        (80, 120, 220),
        (220, 80, 80),
        (220, 180, 50),
        (180, 80, 220),
        (80, 210, 210),
        (220, 130, 50),
    ]

    for i in range(masks.shape[0]):
        colour = palette[class_ids[i] % len(palette)]
        mask = masks[i, 0].cpu().numpy()

        coloured = np.zeros_like(img_np)
        coloured[mask] = colour
        img_np = cv2.addWeighted(img_np, 1.0, coloured, alpha, 0)

        contours, _ = cv2.findContours(
            mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
        )
        cv2.drawContours(img_np, contours, -1, colour, 2)

    return img_np


def get_prompts_from_terminal():
    """
    Ask the user to type all class prompts on a single line, instead of
    loading them from handoff/prompts.json.

    Expected format: comma-separated "class_id:prompt" pairs, e.g.
        0: teal purple box,1:blue tube,2:lightblue white box
    Spaces around the colon/comma are optional. The part after the first
    colon (up to the next comma) is taken as the prompt text as-is, so
    multi-word prompts like "teal purple rectangular box" work fine.
    """
    print("Enter all class prompts on one line, comma-separated, as class_id:prompt.")
    print('Example: 0:teal purple box,1:blue tube,2:lightblue white box\n')

    while True:
        line = input("  prompts: ").strip()
        if not line:
            print("  No input given — please enter at least one class_id:prompt pair.\n")
            continue

        prompts = []
        ok = True
        for chunk in line.split(","):
            chunk = chunk.strip()
            if not chunk:
                continue
            if ":" not in chunk:
                print(f'  Could not parse "{chunk}" — expected format class_id:prompt. Try again.\n')
                ok = False
                break
            cls_id_str, prompt = chunk.split(":", 1)
            cls_id_str = cls_id_str.strip()
            prompt = prompt.strip()
            if not cls_id_str.isdigit() or not prompt:
                print(f'  Could not parse "{chunk}" — expected format class_id:prompt. Try again.\n')
                ok = False
                break
            prompts.append((int(cls_id_str), prompt))

        if ok and prompts:
            break

    prompts.sort(key=lambda x: x[0])

    print("\nClass mapping:")
    for cls_id, p in prompts:
        print(f"  class {cls_id} -> \"{p}\"")
    print()

    return prompts


# ─────────────────────────────────────────────────────────────────────────
# Main
# ─────────────────────────────────────────────────────────────────────────


def main():
    prompts = get_prompts_from_terminal()

    image_paths = get_sorted_images(IMAGE_DIR)
    if not image_paths:
        print(f"No images found in {IMAGE_DIR}")
        sys.exit(1)
    print(f"Found {len(image_paths)} images in {IMAGE_DIR}\n")

    # ── Optional detection ROI ───────────────────────────────────────────
    # Ask the user whether they want to draw a region. If yes, detection is
    # confined to that single region for every image — this excludes copies
    # of the same object that appear near the frame edges.
    roi_mask = None
    draw_roi = input(
        "Do you want to draw an ROI? (detection will happen only inside it) (y/n): "
    ).strip().lower() == "y"

    if draw_roi:
        first_image = cv2.imread(image_paths[0])
        if first_image is None:
            print(f"Could not read {image_paths[0]} — skipping ROI, using full image.\n")
        else:
            roi_mask = draw_detection_roi(first_image)
            if roi_mask is None:
                print("No ROI drawn — detection will run on the full image.\n")
            else:
                print("Detection restricted to the drawn ROI region.\n")

    use_edge_filter = input("Enable edge filtering? (y/n): ").strip().lower() == "y"
    if use_edge_filter:
        print(f"Edge filtering ON — masks touching border within {EDGE_MARGIN}px will be rejected.\n")
    else:
        print("Edge filtering OFF — all masks will be accepted.\n")

    import sam3
    from sam3 import build_sam3_image_model
    from sam3.model.sam3_image_processor import Sam3Processor

    print("Loading SAM3 model...")
    sam3_root = os.path.join(os.path.dirname(sam3.__file__), "..")
    bpe_path = os.path.join(sam3_root, "sam3", "assets", "bpe_simple_vocab_16e6.txt.gz")

    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True

    model = build_sam3_image_model(
        bpe_path=bpe_path,
        checkpoint_path=SAM3_CHECKPOINT_PATH,
        load_from_HF=False,
    )
    processor = Sam3Processor(model, confidence_threshold=CONFIDENCE)
    print(f"Model device : {next(model.parameters()).device}")
    if torch.cuda.is_available():
        print(f"GPU          : {torch.cuda.get_device_name(0)}")
    print("Model ready.\n")

    os.makedirs(OUTPUT_DIR, exist_ok=True)
    images_dir = os.path.join(OUTPUT_DIR, "images")
    labels_dir = os.path.join(OUTPUT_DIR, "labels")
    os.makedirs(images_dir, exist_ok=True)
    os.makedirs(labels_dir, exist_ok=True)

    timings = []

    with torch.autocast("cuda", dtype=torch.bfloat16):
        for idx, img_path in enumerate(image_paths):
            fname = os.path.basename(img_path)
            print(f"[{idx + 1}/{len(image_paths)}] {fname}", end=" ... ", flush=True)

            image = Image.open(img_path).convert("RGB")
            t_start = time.perf_counter()

            detect_image = image
            if roi_mask is not None:
                img_np = np.array(image)
                rm = roi_mask
                if rm.shape[:2] != img_np.shape[:2]:
                    rm = cv2.resize(
                        rm, (img_np.shape[1], img_np.shape[0]),
                        interpolation=cv2.INTER_NEAREST,
                    )
                masked = img_np.copy()
                masked[rm == 0] = 0
                detect_image = Image.fromarray(masked)

            all_masks, all_boxes, all_scores, all_class_ids = [], [], [], []

            for cls_id, prompt in prompts:
                inference_state = processor.set_image(detect_image)
                processor.reset_all_prompts(inference_state)
                inference_state = processor.set_text_prompt(
                    state=inference_state, prompt=prompt
                )

                masks = inference_state.get("masks")
                boxes = inference_state.get("boxes")
                scores = inference_state.get("scores")

                if masks is not None and masks.shape[0] > 0:
                    if use_edge_filter:
                        valid = [
                            i for i in range(masks.shape[0])
                            if not is_touching_edge(masks[i, 0].cpu().numpy(), margin=EDGE_MARGIN)
                        ]
                        if valid:
                            masks, boxes, scores = masks[valid], boxes[valid], scores[valid]
                        else:
                            masks = None
                    if masks is not None and masks.shape[0] > 0:
                        all_masks.append(masks)
                        all_boxes.append(boxes)
                        all_scores.append(scores)
                        all_class_ids.extend([cls_id] * masks.shape[0])
                        print(
                            f'\n    class {cls_id} "{prompt}": {masks.shape[0]} '
                            f'detection(s)  score: {float(scores[0].cpu()):.2f}',
                            end="",
                        )

            if all_masks:
                final_masks = torch.cat(all_masks, dim=0)
                final_boxes = torch.cat(all_boxes, dim=0)
                final_scores = torch.cat(all_scores, dim=0)
                final_class_ids = all_class_ids
            else:
                final_masks = None

            if final_masks is not None and final_masks.shape[0] > 1:
                boxes_np = final_boxes.cpu().numpy()
                scores_np = final_scores.float().cpu().numpy()
                n = len(boxes_np)

                def compute_iou(a, b):
                    ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
                    ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
                    inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
                    if inter == 0:
                        return 0.0
                    area_a = (a[2] - a[0]) * (a[3] - a[1])
                    area_b = (b[2] - b[0]) * (b[3] - b[1])
                    return inter / (area_a + area_b - inter)

                drop = set()
                for i in range(n):
                    if i in drop:
                        continue
                    for j in range(i + 1, n):
                        if j in drop:
                            continue
                        if compute_iou(boxes_np[i], boxes_np[j]) > IOU_THRESHOLD:
                            loser = j if scores_np[i] >= scores_np[j] else i
                            drop.add(loser)

                keep = [k for k in range(n) if k not in drop]
                if drop:
                    print(f"  dedup: dropped {len(drop)} overlapping detection(s)", end="")
                final_masks = final_masks[keep]
                final_boxes = final_boxes[keep]
                final_scores = final_scores[keep]
                final_class_ids = [final_class_ids[k] for k in keep]

            if final_masks is None or final_masks.shape[0] == 0:
                print("  no valid detections — saving original")
                annotated = np.array(image)
            else:
                annotated = overlay_masks_and_boxes(
                    image, final_masks, final_boxes, final_scores, final_class_ids
                )

            stem = f"{OUTPUT_PREFIX}_{idx + OUTPUT_START_INDEX:0{OUTPUT_NUM_DIGITS}d}"

            out_name = stem + ".jpg"
            save_path = os.path.join(images_dir, out_name)
            cv2.imwrite(save_path, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR))

            elapsed = time.perf_counter() - t_start
            timings.append(elapsed)
            print(f"  time: {elapsed:.2f}s")

            label_name = stem + ".txt"
            label_path = os.path.join(labels_dir, label_name)
            # only write a label file when there's at least one detection —
            # frames with zero detections get no label file at all.
            # each line is: class cx cy w h (normalized YOLO detection box)
            if final_masks is not None and final_masks.shape[0] > 0:
                img_w, img_h = image.size
                lines = []
                for i in range(final_boxes.shape[0]):
                    x1, y1, x2, y2 = final_boxes[i].cpu().numpy().astype(float)
                    cx = ((x1 + x2) / 2) / img_w
                    cy = ((y1 + y2) / 2) / img_h
                    bw = (x2 - x1) / img_w
                    bh = (y2 - y1) / img_h
                    lines.append(f"{final_class_ids[i]} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}")

                with open(label_path, "w") as lf:
                    lf.write("\n".join(lines) + "\n")
                print(f"    label: {label_path}  ({len(lines)} object(s))")
            else:
                print(f"    label: skipped  (no detections)")

    print(f"\nDone. Annotated images : {images_dir}")
    print(f"      YOLO labels       : {labels_dir}")
    if timings:
        total = sum(timings)
        print("\n── Timing summary ──────────────────────")
        print(f"  Images processed : {len(timings)}")
        print(f"  Total time       : {total:.2f}s")
        print(f"  Avg per image    : {total / len(timings):.2f}s")
        print(f"  Fastest          : {min(timings):.2f}s")
        print(f"  Slowest          : {max(timings):.2f}s")
        print("────────────────────────────────────────")


if __name__ == "__main__":
    main()