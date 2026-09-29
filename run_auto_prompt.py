from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

import cv2
import numpy as np
import torch


QWEN_SAM3_PROMPT = """
You are looking at a cropped photo of a single object. The crop may include
background, glare, or reflections. Ignore those; they are not part of the
object. Your answer will be passed directly to SAM3 to find the same object
in other images.

Output only one lowercase noun phrase, two to seven words, with no punctuation:
<shape> <color1> <color2> <object>

Use the most specific visible colors. Include a shape only when it helps
distinguish an object such as a box, container, frame, lamp, phone, clock, or
knob. Use a specific everyday object name; never output object, item, thing,
device, product, brand name, printed text, numbers, or what the object contains.
Describe the foreground object, not the background.

Examples:
white and light-blue Dailies package -> rectangular white lightblue box
brown package -> rectangular brown box
clear tubing with blue tint -> blue tube
""".strip()


def normalized_polygon(points: list, width: int, height: int) -> np.ndarray:
    values = np.asarray(
        [[point["x"], point["y"]] if isinstance(point, dict) else point for point in points],
        dtype=np.float32,
    )
    if values.size == 0:
        return np.empty((0, 2), dtype=np.int32)
    values = values.reshape(-1, 2)
    if values.max() <= 1.0:
        values[:, 0] *= width
        values[:, 1] *= height
    return values.astype(np.int32)


def crop_to_roi(image_path: Path, points: list, output_path: Path) -> Path:
    image = cv2.imread(str(image_path))
    if image is None:
        raise RuntimeError(f"Could not read image: {image_path}")
    height, width = image.shape[:2]
    polygon = normalized_polygon(points, width, height)
    if len(polygon) < 3:
        return image_path
    mask = np.zeros((height, width), dtype=np.uint8)
    cv2.fillPoly(mask, [polygon], 255)
    x, y, crop_width, crop_height = cv2.boundingRect(polygon)
    cropped = image[y : y + crop_height, x : x + crop_width]
    cropped_mask = mask[y : y + crop_height, x : x + crop_width]
    # Match the ML engineer's ROI preparation: retain the object and black
    # out everything outside the polygon so Qwen does not describe the desk.
    roi_image = np.zeros_like(cropped)
    roi_image[cropped_mask > 0] = cropped[cropped_mask > 0]
    cv2.imwrite(str(output_path), roi_image)
    return output_path


def clean_prompt(value: str) -> str:
    prompt = value.strip().splitlines()[0].strip(" .,:;\"'")
    prompt = re.sub(r"^(the|a|an)\s+", "", prompt, flags=re.IGNORECASE)
    return prompt[:120]


def load_vlm():
    """Load Qwen once; the API keeps this pair alive for later requests."""
    model_name = os.getenv("SAIL_VLM_MODEL")
    if not model_name:
        raise RuntimeError("SAIL_VLM_MODEL must identify a native Qwen3-VL model.")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable in the native Conda environment for VLM auto-prompting.")
    from transformers import AutoProcessor, Qwen3VLForConditionalGeneration

    model = Qwen3VLForConditionalGeneration.from_pretrained(
        model_name, torch_dtype=torch.float16, device_map="auto"
    )
    return model, AutoProcessor.from_pretrained(model_name)


def generate_prompt(model, processor, image_path: Path, roi: list, working_directory: Path) -> str:
    from qwen_vl_utils import process_vision_info

    prompt_image = crop_to_roi(image_path, roi, working_directory / "roi.png")
    messages = [
        {"role": "user", "content": [
            {"type": "image", "image": str(prompt_image)},
            {"type": "text", "text": QWEN_SAM3_PROMPT},
        ]}
    ]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    image_inputs, video_inputs = process_vision_info(messages)
    inputs = processor(text=[text], images=image_inputs, videos=video_inputs, padding=True, return_tensors="pt").to(model.device)
    with torch.no_grad():
        generated_ids = model.generate(**inputs, max_new_tokens=18, do_sample=False)
    generated_ids = [output[len(input_ids):] for input_ids, output in zip(inputs.input_ids, generated_ids)]
    prompt = clean_prompt(processor.batch_decode(generated_ids, skip_special_tokens=True, clean_up_tokenization_spaces=False)[0])
    if not prompt:
        raise RuntimeError("VLM response did not contain a usable prompt.")
    return prompt


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: run_auto_prompt.py <job_directory>")
    job_dir = Path(sys.argv[1]).resolve()
    request = json.loads((job_dir / "request.json").read_text(encoding="utf-8"))
    image_path = Path(request["image"])
    model, processor = load_vlm()
    prompt = generate_prompt(model, processor, image_path, request.get("roi", []), job_dir)
    (job_dir / "result.json").write_text(json.dumps({"prompt": prompt}), encoding="utf-8")
    print(f"Generated prompt: {prompt}")


if __name__ == "__main__":
    main()