# Validation → Training → saved model

This change is implemented in the local application source. Copy the updated backend to the existing GPU machine; no deployment or training is performed automatically. Inference is now integrated; see [ANNOTATION_INFERENCE.md](ANNOTATION_INFERENCE.md) for SAM2 setup, reprocessing and inference.

## Copy/update

- Copy the **complete `backend` source folder**, including `routers`, `services`, `ml`, and `requirements-training.txt`; preserve the destination's existing `workspace`, Settings, model files and environment configuration. Do not replace the test PC's workspace with this PC's workspace.
- Use the frontend from the same updated source (`src`) and rebuild/restart it as usual. Keep its existing `NEXT_PUBLIC_SAIL_API_URL`.
- Restart the backend as a **single worker**, using its existing Python environment. If that environment does not already contain Ultralytics, install `ultralytics>=8.3,<9` into it. `requirements-training.txt` records the dependency. Keep the working GPU-compatible PyTorch installation.
- The training process uses the existing `SAIL_CONDA_PYTHON` setting (default: the application's `.conda/sail/python.exe`). GPU 0 is the default and only UI workflow; there is no device or machine selector.
- GET `/training/runs` and GET `/models` should return an `items` array, not 404. Copy all new source modules together if an endpoint is missing.

## User workflow

1. Select the project and inspect its annotated Pass images in Datasets. Edit incorrect annotations or exclude images with Delete. Expanded images retain edit/delete and previous/next carousel navigation.
2. Manual Validate, or accept candidates after Automatic checks, creates an immutable raw-image + label snapshot. Annotation overlays are never Training inputs.
3. Select that snapshot in Training. Choose YOLOv8 size, normal/fine-tuning mode, optimizer, epochs, batch size, image size, learning rate, pretrained weights, early-stopping patience, seed and train/validation/test percentages (70/20/10 by default).
4. Start Training explicitly. It verifies the snapshot's hashes and labels, copies a unique split, writes `data.yaml`, then runs `ml/train.py`. Labels and class order come from the snapshot, not the supplied example's hard-coded `blister` class.
5. Follow persisted run status, epochs, metrics and logs. The counter shows actual processed images **within the current epoch**, increasing at batch boundaries (e.g. 4, 8, …, 496 / 496), and resets for each new epoch. It is not a count of images guaranteed to have been learned correctly. Browse every train, validation or test split image with pagination while training runs. Stop cancels the job; partial outputs remain isolated and are not registered as a completed model.
6. After training, the **best checkpoint selected using validation** is evaluated on the held-out test split. Only successful runs with a real owned `best.pt` are registered in Models. A source-prefixed copy named `<source>_<mode>_<run-id-prefix>_best.pt` is registered and downloaded. The original YOLO `best.pt` remains intact. Select the saved model in Inference.

## Output paths

All paths resolve on the backend machine. Use existing Settings → Models directory; no `D:` paths from the supplied scripts remain. For example, if Models directory is `C:\Charan\SAIL-Models`:

```
C:\Charan\SAIL-Models\<project>\Detection|Segmentation\<unique-run-id>\
  request.json               # immutable input/config provenance
  run.json                   # state and split counts
  split\
    images\train|val|test\
    labels\train|val|test\
    data.yaml                # absolute quoted path and actual classes
    split.json               # seed, assignments and hashes
  train.log
  progress.json
  yolo\weights\best.pt
  yolo\weights\last.pt
  yolo\results.csv           # Ultralytics outputs
  test\                     # held-out test plots/results
  result.json
  model.json                 # successful checkpoint metadata and checksum
  <source>_<mode>_<run-id-prefix>_best.pt
```

The small persistent registries are in `SAIL_WORK_ROOT/.training` and `.snapshots`. Preserve them along with output folders and Settings when copying installations. Missing pointers are rebuilt from durable manifests under the configured storage roots. Validated snapshots are independent of the original annotation run. Closing/reopening the UI does not remove datasets or models. Recovery after moving files to a different absolute path is not automatic: retain the saved paths. Missing/changed checkpoint files cannot be downloaded or used as base models.

Pretrained YOLO weights may download into the new run directory on first use. For an offline backend, place the appropriate `yolov8n.pt` / `yolov8n-seg.pt` (or selected size) under **Settings Models directory / `pretrained`** before starting; this local copy takes precedence over automatic download. Alternatively run without pretrained weights. Source code does not download or start training until the user clicks Start.

## Data-quality safeguards and limitations

- At least three distinct images are needed for non-empty train/validation/test partitions. With small datasets the final counts can differ from percentages. Exact byte-identical images are grouped into the same split. Missing labels, corrupted hashes, invalid labels, mismatched stems and duplicate stems fail closed.
- Random splitting is not a guarantee against leakage from similar video frames, repeated scenes or subjects. Prefer independent capture sessions for evaluation. Small splits and classes with no training examples are reported as warnings, not claimed accurate results.
- Incremental Learning fine-tunes a registered checkpoint with identical project, task and ordered class mapping. It does not automatically add new classes or prevent forgetting. Test images previously used for training/validation of the base model are rejected using saved image hashes.
- Image size must be a multiple of 32 (default 640); the supplied 720 would otherwise be rounded by YOLO. Workers stay at 0 for Windows compatibility. CPU/device switching is not exposed.
- Training waits for active annotation/automatic-review work and owns the existing model lock while running. SAM3/Qwen caches are released to free VRAM and reload on their next use. Other work is not modified. Do not run multiple API workers against this workspace.
- If the API exits, its training subprocess exits too. An unfinished run is displayed as interrupted, not as a completed model; start a fresh run. No auto-resume or fake success metrics are provided.

## User-confirmed ground truth

In the expanded validation viewer, **Use as ground truth** asks the user to confirm every class, object and boundary is correct. Correct mistakes with ROI editing first. The reference is a frozen raw image + label copy under `SAIL_WORK_ROOT/.ground-truth`, keyed by project, mode, class order and raw-image SHA-256. It is not a ground-truth label inferred by an ML model.

Future predictions for that exact image can be checked against this reference. The comparison is class-aware, requires equal instance counts and a one-to-one match at IoU ≥ 0.90 for every object. Detection uses box IoU; segmentation uses rasterized polygons at a recorded maximum side of 1024 pixels, with tiny/dense cases sent to manual review. This is intentionally more conservative than the supplied script's 0.5 threshold. References are never scored against themselves. Images without a matching reference keep the existing Qwen + geometry triage; candidate status does not guarantee correctness. The existing configured Qwen model is still required for Automatic mode.

The supplied Hungarian script was not executed unchanged: it assumed unavailable matching ground truth, did not match by class, and maximizing summed IoU before threshold filtering can undercount valid matches. The implementation uses maximum-cardinality matching of edges already above the threshold. Changed references invalidate candidate export. Accepting automatic candidates still requires explicit user action.

## Verification in this source tree

```
.conda\sail\python.exe -m unittest discover -s backend -p test_training_pipeline.py -v
.conda\sail\python.exe -m unittest discover -s backend -p test_dataset_validation.py -v
node node_modules/next/dist/bin/next build
```

Tests use temporary datasets and a mocked YOLO/process boundary. They verify the integration contract, not model accuracy or the GPU machine's runtime. The first real GPU run after copying these files remains the end-to-end deployment check.
