# Automatic annotation validation

This is no-reference quality triage, not measured ground-truth accuracy.
It uses the existing local Qwen3-VL model (`SAIL_VLM_MODEL`) and CUDA runtime.
No images are sent to an external judge API and no second model is downloaded
by the validation service itself. The configured model loader behaves as before.

1. Validate each included Pass image's actual YOLO label syntax and class IDs.
2. Reconstruct an overlay from those labels, not a potentially outdated preview.
3. Flag out-of-bounds geometry, extreme area, dense scenes and objects too small
   after resizing for a reliable visual judgment.
4. Compare raw image and reconstructed overlay using deterministic Qwen generation.
   Require four strict boolean checks (correct classes/objects, completeness,
   tight boundaries, no extra objects) and a rating of at least 4/5.
5. Parse failures, model errors and any uncertain checks require manual review.
6. User explicitly accepts candidates to export only those raw/label pairs.
   Flagged images remain unchanged for manual correction. Nothing is deleted.

The rating threshold and geometric limits are conservative heuristics, not
calibrated probabilities. Small local VLMs can still confidently approve errors,
particularly tiny objects and subtle segmentation edges. Spot-check candidate
images and evaluate precision/recall on a representative human-labeled sample
before relying on this pipeline in production. IoU/Dice against ground truth
are not reported because no ground truth is supplied.

Reviews run on a background thread with per-image disk checkpoints, 24-result
pagination, progress polling and cancellation after the current image. A process
restart marks unfinished reviews interrupted; rerun them. Annotation edits make
results stale. Export rejects stale revisions and changed raw/label checksums.
Snapshots record `validationMethod=automatic` and the report ID. Human validation
remains separate and cannot be satisfied by a partial automatic snapshot.

Deploy the complete updated backend and restart it; the frontend alone cannot
add `/datasets/{id}/automatic` endpoints. Tests use mocked judge outputs and do
not establish real GPU/model quality or throughput. 10,000 images require one
model judgment per image surviving the geometry filter, so runtime depends on
the GPU, model, resolution and scene complexity.

Implementation references:
- https://huggingface.co/docs/transformers/en/model_doc/qwen3_vl
- https://github.com/QwenLM/Qwen3-VL
- https://github.com/facebookresearch/sam3/blob/main/sam3/model/sam3_image_processor.py

SAM3 detection scores are not treated as ground-truth IoU or accuracy.

## Assessment of the supplied Validation script.txt (2026-09-05)

The script evaluates predictions against independent ground-truth instance masks.
It builds pairwise mask IoUs, assigns instances with Hungarian matching, then
counts matched and unmatched instances to calculate precision, recall and F1.
This can help evaluate segmentation when trusted reference annotations exist.
The current workflow has no ground-truth input, so it is not integrated as a
replacement for the existing no-reference review. Comparing SAM output to itself
would yield misleading agreement; comparing to another model measures model
agreement, not ground-truth accuracy.

Before adopting it for a future reference-based evaluation:
- Match by class as well as overlap; the supplied code ignores class identity.
- Check image IDs, collection lengths and mask shapes. `zip` silently truncates
  unequal dataset lists, and NumPy may broadcast incompatible mask dimensions.
- Detection requires box IoU rather than mask IoU and task-specific thresholds.
- Maximizing total IoU and then rejecting subthreshold pairs does not always
  maximize the number of valid matches. For example at threshold 0.5, the matrix
  [[0.90, 0.60], [0.60, 0.49]] selects a diagonal sum of 1.39 but leaves one valid
  match; the off-diagonal assignment yields two valid matches. Define and test
  the intended evaluation matching policy rather than calling this COCO mAP.
- Matched-only mean IoU hides missed and extra objects; retain TP/FP/FN alongside
  it. Define empty-image behavior explicitly and avoid matching twice per image.
- Centroid proximity can explain localization errors but must not turn a poor
  overlap into an accepted annotation.

The supplied PQ-style score is not a complete class-aware panoptic evaluation.
Its document instructions and follow-up question were treated as reference
material, not as requirements to modify this project's data formats.
