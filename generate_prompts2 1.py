

"""
generate_prompts.py
====================
Phase 1 + Phase 2 of the pipeline, run as a standalone, short-lived
process so that ALL of its memory (GPU + host RAM) is guaranteed to be
released by the OS when it exits — this is more reliable on WSL2 than
trying to manually unload a model in-process before loading SAM3.

What it does:
  1. Lets the user draw freehand ROIs on the first image in IMAGE_DIR.
  2. Runs Qwen3-VL-2B-Instruct on each ROI crop to generate a short
     "<shape> <color> <object>" text description.
  3. Lets the user confirm or override the generated prompts.
  4. Writes two files to disk for run_sam3.py to pick up:
       - prompts.json   -> [[class_id, prompt_text], ...]
       - roi_mask.png    -> union of all drawn ROI polygons (binary mask)

Run this, let it finish and exit completely, THEN run run_sam3.py.
"""

import json
import os
import sys

import cv2
import numpy as np
import torch
from PIL import Image

# ─────────────────────────────────────────────────────────────────────────
# CONFIG — edit these
# ─────────────────────────────────────────────────────────────────────────

IMAGE_DIR = "./Allimg/0_back_dailies_5.jpg"   # single file ✓
HANDOFF_DIR = "./handoff/"             # where prompts.json + roi_mask.png get written

QWEN_MODEL_NAME = "Qwen/Qwen3-VL-2B-Instruct"
# QWEN_MAX_NEW_TOKENS = 500

# QWEN_PROMPT = """
# Look at the cropped object. 
# Find the Object inside it . Describe its shape and colors.
# Output format (2-7 words):
# <shape> <color1> <color2> <object>

# - shape: Include the shape word ONLY if the object's shape is not obvious
#   from its name (skip shape for objects like pens, cans, bottles,bulb, donuts,tube,
#   balls — where the shape is implied by what the object is).
#   Include shape when the object could have multiple different shapes
#   (boxes, frames, lamps, containers, phones, clocks, knobs,tube, etc.).
#   If shape is genuinely ambiguous or hard to pin to one word
#   (e.g. rounded-corner square), skip the shape word rather than guessing.

# - colors: Include the most specific visible colors
# - color1, color2: the most specific visible colors
#   (e.g. transparent, white, lavender, purple, teal, golden, grey,
#    lightblue, matte black, darkblue, or any other
#   specific color name that fits)

#   If the object is transparent or clear but has a visible color tint,use the only  tint color.
#   If the Tube has the Blue color then Give as Blue Tube 
# - object: the specific everyday name for what this object is.
#   NEVER use generic placeholder words like "object," "item," "thing,"
#   or "device." Always commit to a real, specific name
#   (e.g. knob, lock, handle, pot, can, box, bottle, lamp, clock,
#   phone, pen, glasses, plate, donut).

# Do NOT include background colors
# Do NOT include any text, words, numbers, or names you see written on the object.
# Do NOT include what the object contains or is used for.
# Only describe what you would see if all text and labels were blank.

# No punctuation. Lowercase only. Maximum 7 words.

# Example for a box of color white lightblue  with "Focus Dailies All Day Comfort" printed on it:
# rectangular white lightblue box

# Example for a brown box with "Amazon" printed on it:
# rectangular brown box
# """

# QWEN_PROMPT = """
# Look at the cropped object.
# Find the Object inside it . Describe its shape and colors.
# Output format (2-7 words):
# <shape> <color1> <color2> <object>

# - shape: include a shape word ONLY if this type of object comes in many
#   different shapes (like boxes or containers do). If the object's name
#   already tells you its shape, skip the shape word. If the shape is hard
#   to pin to one word, skip it rather than guessing.

# - color1, color2: 1 or 2 of the most specific visible colors
#   (e.g. white transparent, white, lavender, purple, teal, golden, grey,
#   lightblue, matte black, darkblue, or any other specific color
#   name that fits).
#   If the object is transparent or clear but has a visible color tint,
#   use the tint color, not "transparent".

# - object: the specific everyday name for what this object is.
#   NEVER use generic placeholder words like "object," "item," "thing,"
#   or "device." Always commit to a real, specific name.

# Do NOT include background colors
# Do NOT include any text, words, numbers, or names you see written on the object.
# Do NOT include what the object contains or is used for.
# Only describe what you would see if all text and labels were blank.

# No punctuation. Lowercase only. Maximum 7 words.

# Example for a box of color white lightblue with "Focus Dailies All Day Comfort" printed on it:
# rectangular white lightblue box

# Example for a brown box with "Amazon" printed on it:
# rectangular brown box

# Example for clear plastic tubing with a faint blue tint:
# blue tube

# Example for a red ballpoint pen with a silver clip:
# red pen

# Example for a metallic door knob with a darker handle:
# round grey knob
# """

QWEN_PROMPT = """
You are looking at a cropped photo of a single object. The crop may
include some background, glare, or reflections around the object —
ignore those, they are not part of the object.

Your answer will be used as a short search phrase to find every object
of this same type in other images. So describe the object TYPE, not
this particular copy's lighting, position, or printed text.

Look at the cropped object.
Find the Object inside it . Describe its shape and colors.
Output format (2-7 words):
<shape> <color1> <color2> <object>

- shape: Include the shape word ONLY if the object's shape is not obvious
  from its name (skip shape for objects like pens, cans, bottles,bulb, donuts,tube,
  balls — where the shape is implied by what the object is).
  Include shape when the object could have multiple different shapes
  (boxes, frames, lamps, containers, phones, clocks, knobs,tube, etc.).
  If shape is genuinely ambiguous or hard to pin to one word
  (e.g. rounded-corner square), skip the shape word rather than guessing.

- colors: Include the most specific visible colors
- color1, color2: the most specific visible colors
  (e.g. transparent, white, lavender, purple, teal, golden, grey,
  lightblue, matte black, darkblue, or any other
  specific color name that fits)

- object: the specific everyday name for what this object is.
  NEVER use generic placeholder words like "object," "item," "thing,"
  or "device." Always commit to a real, specific name
  (e.g. knob, lock, handle, pot, can, box, bottle, lamp, clock,
  phone, pen, glasses, plate, donut).

Do NOT include background colors
Do NOT include any text, words, numbers, or names you see written on the object.
Do NOT include what the object contains or is used for.
Only describe what you would see if all text and labels were blank.

No punctuation. Lowercase only. Maximum 7 words.

Example for a box of color white lightblue  with "Focus Dailies All Day Comfort" printed on it:
rectangular white lightblue box

Example for a brown box with "Amazon" printed on it:
rectangular brown box
"""


# QWEN_PROMPT = [
#     # 1. fully open — what does it see?
#     "Describe this image.",
#     # 2. object-focused, still free-form
#     "What is the main object in this image? Describe its specific correct color and appearance.",
#     # 3. short-form, but no rules yet
#     "Name the main object in this image in a few words, mentioning its color.",
# ]


# QWEN_PROMPT = """
# You are looking at a cropped photo of a object. The crop may
# include some background, glare, or reflections — they are not part
# of the object.

# Your answer will be used as a short search phrase to find every object
# of this same type in other images. Describe the object TYPE, not this
# particular copy's lighting, position, or printed text.

# Output ONLY a short noun phrase, 2-7 words, lowercase, no punctuation:
# attributes first, object name LAST.

# - object name: the specific everyday name for what this object is
 
#   NEVER use generic words like "object", "item", "thing", "device".
#   Prefer the most specific name: "desk lamp" not "lamp",
#   "telephone" not "phone", if it fits in the word limit.

# - colors: 1 or 2 of the most specific visible colors
#   (e.g. transparent, white, lavender, teal, turquoise, golden, grey,
#   metallic silver, lightblue, matte black, darkblue).
#   If the object is transparent or clear but has a visible color tint,
#   include the tint color.

# - shape: include a shape word ONLY if the object could have multiple
#   different shapes (boxes, frames, lamps, containers, phones, clocks,
#   knobs). Skip shape for objects whose name implies it (pens, cans,
#   bottles, bulbs, donuts, tubes, balls). If the shape is hard to pin
#   to one word, skip it rather than guessing.

# Do NOT include background colors or surfaces.
# Do NOT include any text, numbers, logos, or brand names written on
# the object. Describe it as if all printing were blank.
# Do NOT include what the object contains or is used for.

# Examples:
# A white and light-blue contact lens box with "Focus Dailies" printed on it:
# rectangular white lightblue box

# A brown cardboard box with "Amazon" printed on it:
# rectangular brown box

# A turquoise-green box fading into blue:
# rectangular teal blue box

# A round metallic door lock with a darker handle:
# round grey metal knob

# A red desk lamp with a black flexible neck:
# red desk lamp
# """

QWEN_MAX_NEW_TOKENS = 18


# # QWEN_PROMPT = "Give a short text prompt for SAM3 to segment this object.completely ignore the background. Maximum 5 words"
# # QWEN_PROMPT = "Give a short text prompt for SAM3 to segment this object.."
# # ─────────────────────────────────────────────────────────────────────────
# # Helpers
# # ─────────────────────────────────────────────────────────────────────────


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


def draw_rois_on_image(image_bgr):
    """
    Lets the user draw multiple freehand ROIs on `image_bgr`.

    Controls:
        - Hold LEFT mouse button and drag to draw a freehand outline
        - Press ENTER / SPACE to confirm the current ROI
        - Press C to finish drawing all ROIs

    Returns:
        rois: list of dicts with "id" (== draw order), "crop" (PIL.Image,
              masked), "polygon" (np.ndarray of drawn points).
    """
    drawing = {"active": False}
    points = []
    current_canvas = image_bgr.copy()
    rois = []
    roi_id = 0

    window = "ROI Selector  (drag=draw, Enter=confirm, C=finish)"

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

    print("Phase 1: Draw ROIs on the first image")
    print("  Hold LEFT CLICK and drag around each object")
    print("  Press ENTER to confirm the ROI")
    print("  Press C when you're done drawing all ROIs\n")

    while True:
        cv2.imshow(window, current_canvas)
        key = cv2.waitKey(20) & 0xFF

        if key in (13, 32) and len(points) > 2:
            mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)
            cv2.fillPoly(mask, [np.array(points)], 255)

            x, y, w, h = cv2.boundingRect(np.array(points))
            crop_box = image_bgr[y:y + h, x:x + w]
            mask_box = mask[y:y + h, x:x + w]
            crop_masked = cv2.bitwise_and(crop_box, crop_box, mask=mask_box)

            cv2.imshow("Crop Preview", crop_masked)
            cv2.waitKey(600)
            cv2.destroyWindow("Crop Preview")

            pil_crop = Image.fromarray(cv2.cvtColor(crop_masked, cv2.COLOR_BGR2RGB))
            rois.append({"id": roi_id, "crop": pil_crop, "polygon": np.array(points)})

            cv2.putText(
                current_canvas, str(roi_id), (x, y - 8),
                cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 255, 0), 2,
            )
            print(f"  ROI {roi_id} captured.")
            roi_id += 1

            points = []
            cv2.setMouseCallback(window, callback, current_canvas.copy())

        elif key == ord('c'):
            print(f"\n{roi_id} ROI(s) collected.\n")
            break

    cv2.destroyAllWindows()
    return rois


def generate_prompts_with_qwen(rois, device):
    from transformers import AutoProcessor, AutoModelForImageTextToText
    from qwen_vl_utils import process_vision_info

    print("Phase 2: Loading Qwen3-VL and generating prompts...\n")

    processor = AutoProcessor.from_pretrained(QWEN_MODEL_NAME)
    model = AutoModelForImageTextToText.from_pretrained(
        QWEN_MODEL_NAME,
        torch_dtype=torch.float16 if device == "cuda" else torch.float32,
        device_map="auto",
    )

    prompts = []
    for roi in rois:
        messages = [{
            "role": "user",
            "content": [
                {"type": "image", "image": roi["crop"]},
                {"type": "text", "text": QWEN_PROMPT},
            ],
        }]

        text = processor.apply_chat_template(
            messages, tokenize=False, add_generation_prompt=True
        )
        image_inputs, video_inputs = process_vision_info(messages)
        inputs = processor(
            text=[text],
            images=image_inputs,
            videos=video_inputs,
            return_tensors="pt",
            padding=True,
        ).to(model.device)

        with torch.no_grad():
            output = model.generate(
                **inputs, max_new_tokens=QWEN_MAX_NEW_TOKENS, do_sample=False
            )
        output = [out[len(inp):] for inp, out in zip(inputs.input_ids, output)]
        response = processor.batch_decode(
            output, skip_special_tokens=True, clean_up_tokenization_spaces=False
        )[0].strip()

        response = response.strip().strip(".").lower()
        if not response:
            response = "object"

        prompts.append((roi["id"], response))
        print(f"  ROI {roi['id']} -> \"{response}\"")

    prompts.sort(key=lambda x: x[0])
    return prompts


def confirm_or_edit_prompts(prompts):
    print("\nAuto-generated class mapping:")
    for cls_id, p in prompts:
        print(f"  class {cls_id} -> \"{p}\"")

    edit = input(
        "\nUse these prompts as-is? (press Enter to accept, or type "
        "replacement as id:prompt,id:prompt,...): "
    ).strip()

    # Accept Enter, or common "yes" affirmations, as "keep as-is"
    if not edit or edit.lower() in ("y", "yes"):
        return prompts

    # Start from the existing prompts so any class id the user doesn't
    # mention keeps its auto-generated value instead of being dropped.
    prompt_map = {cls_id: p for cls_id, p in prompts}
    updates = {}
    parse_errors = []
    for token in edit.split(","):
        token = token.strip()
        if not token:
            continue
        if ":" not in token:
            parse_errors.append(f"  missing ':' in -> \"{token}\"")
            continue
        id_part, prompt_part = token.split(":", 1)
        id_part, prompt_part = id_part.strip(), prompt_part.strip()
        if not id_part.isdigit() or not prompt_part:
            parse_errors.append(f"  bad token -> \"{token}\"")
            continue
        updates[int(id_part)] = prompt_part

    if parse_errors or not updates:
        print("\nCouldn't parse override, falling back to auto-generated prompts:")
        for e in parse_errors:
            print(e)
        return prompts

    unknown_ids = [cid for cid in updates if cid not in prompt_map]
    if unknown_ids:
        print(f"\nWarning: id(s) {unknown_ids} not in the original class mapping — adding as new class(es).")

    prompt_map.update(updates)
    new_prompts = sorted(prompt_map.items(), key=lambda x: x[0])
    return new_prompts


# ─────────────────────────────────────────────────────────────────────────
# Main
# ─────────────────────────────────────────────────────────────────────────


def main():
    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"Using device: {device}\n")

    image_paths = get_sorted_images(IMAGE_DIR)
    if not image_paths:
        print(f"No images found in {IMAGE_DIR}")
        sys.exit(1)

    first_image = cv2.imread(image_paths[0])
    if first_image is None:
        print(f"Could not read {image_paths[0]}")
        sys.exit(1)

    rois = draw_rois_on_image(first_image)
    if not rois:
        print("No ROIs drawn. Exiting.")
        sys.exit(1)

    prompts = generate_prompts_with_qwen(rois, device)
    prompts = confirm_or_edit_prompts(prompts)

    # union of drawn ROI polygons -> optional detection mask for run_sam3.py
    combined_mask = np.zeros(first_image.shape[:2], dtype=np.uint8)
    for roi in rois:
        cv2.fillPoly(combined_mask, [roi["polygon"]], 255)

    os.makedirs(HANDOFF_DIR, exist_ok=True)
    prompts_path = os.path.join(HANDOFF_DIR, "prompts.json")
    mask_path = os.path.join(HANDOFF_DIR, "roi_mask.png")

    with open(prompts_path, "w") as f:
        json.dump(prompts, f, indent=2)
    cv2.imwrite(mask_path, combined_mask)

    print(f"\nWrote prompts -> {prompts_path}")
    print(f"Wrote ROI mask -> {mask_path}")
    print("\nDone. This process will now exit, fully releasing GPU + host RAM.")
    print("Next: run `python run_sam3.py`")


if __name__ == "__main__":
    main()