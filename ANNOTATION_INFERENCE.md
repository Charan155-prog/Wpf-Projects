# SAM2/SAM3, reprocessing, training progress and inference

## Copy to the existing GPU PC

Copy the updated `src` and complete backend **source** (`main.py`, `routers`, `services`, `ml`, requirements and documentation). Also copy `models/sam2/sam2.1_b.pt`, extracted from the supplied `AutoAnnotation_1.7z`. Do not overwrite the test PC's `backend/workspace`, Settings, trained models, validated datasets, `.env.local`, Conda environment or its existing SAM3/Qwen weights.

Use the same Python environment that runs the backend. For the default project location, in PowerShell:

```powershell
Set-Location 'C:\Charan\cdi_measurement_system_frontend\backend'
& '..\.conda\sail\python.exe' -m pip install -r .\requirements-sam2.txt
& '..\.conda\sail\python.exe' -c "import torch, ultralytics; print('Ultralytics:', ultralytics.__version__); print('CUDA:', torch.cuda.is_available()); print('GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'unavailable')"
```

If `SAIL_CONDA_PYTHON` points elsewhere, substitute that interpreter. Keep the working GPU-compatible PyTorch installation; do not deliberately replace it with a CPU build. Run one backend worker and rebuild/restart the matching frontend. There is no new test-PC/device selector; model execution remains on the existing backend GPU 0.

In Settings → Storage & Paths:

- Set **SAM2 checkpoint** to the copied `sam2.1_b.pt`.
- **SAM2 text grounding** defaults to `IDEA-Research/grounding-dino-base`. Text-based Smart Annotation needs this additional model because SAM2 does not accept text prompts directly. It downloads through Transformers on its first text-based use. For an offline PC, provide a previously downloaded complete Grounding DINO model folder, including processor/tokenizer files.
- Set or retain Dataset Root, Validated Training Datasets, Models and Inference Results locations. All paths belong to the backend PC; folder choosers open there.
- Restart the backend after changing model locations so cached models reload with the new paths.

No GPU inference, dependency installation or training was performed automatically on the test PC by this implementation.

## Workflow and model selection

1. Select a project, then choose SAM2 or SAM3 at the top of Smart Annotation.
2. Smart SAM2 also forces Semi-Annotation to SAM2. Smart SAM3 lets Semi-Annotation choose SAM2 or SAM3 independently. Existing datasets created with SAM2 retain SAM2 correction mode.
3. Smart SAM2 adapts the supplied Screen Annotation approach: Grounding DINO text proposals → full-frame SAM2 box prompts. It preserves class IDs and uses the full raw image rather than cropping and stretching the ROI. SAM2 box plus foreground/background clicks are supported in the editor.
4. Semi-Annotation retains sample calibration and adds **Reprocess entire loaded source**. This runs every loaded source image (or every direct-video frame), not just the calibration sample. Previous output runs are preserved in history.
5. **Reprocess output images** opens the project-scoped full output queue. Filter Needs attention / Annotated / All images and paginate through the complete source. Every image opens the correction editor, including zero-detection and failed inputs. Unreadable/missing raw inputs retain an error placeholder but cannot be corrected until a readable source is provided and rerun.
6. In the editor, **Reprocess whole image** predicts again with the selected model. Or draw each object's rectangle/polygon and use model-assisted refinement. SAM2 accepts foreground/background clicks as decoder prompts; SAM3 uses clicks to reject inconsistent candidate masks. **Exact manual ROI** writes the user-drawn geometry without a model when neither model is adequate. Preview before Save; saving replaces the image's annotation set, so include every object to retain. Original files and edit history are preserved.
7. Datasets continues to show the selected project's annotated Pass images for manual/automatic validation. Validation exports only approved raw-image + label pairs. Existing ground-truth and conservative automatic-review safeguards remain in place.
8. Training uses those immutable snapshots. The per-epoch image counter advances on actual completed batches, and all split images are browsable. Models receive a source/mode/run-prefixed checkpoint filename, so repeated trainings remain distinguishable.
9. In Models, choose **Use in Inference**, or select the checkpoint directly in Inference. Supply a backend image/video/folder path and start explicitly. Detection uses boxes; segmentation uses original-coordinate mask polygons and optional boxes. Checkpoint task, class mapping and checksum are verified before use.

## Coverage and accuracy

Annotation failures are retried once, then retained as Fail entries with an error and empty labels; the next input still runs. Coverage manifests record expected/attempted inputs, failures and no-object results. Early video decoding termination fails the run instead of reporting success. Unsupported/fragmented polygon output is sent to review rather than silently dropping components. Model errors are never replaced by invented detections.

This guards against **silently skipped inputs**, not against every semantic object miss. SAM2, SAM3 and confidence thresholds cannot guarantee zero escapes. An image with one detected object and one missed object can still need manual review. A Pass annotation is not ground truth. DINO confidence is not SAM2 mask IoU; manual corrections do not display a fake 1.00 confidence. Review the queue and validate the actual annotations before training.

Detection labels are YOLO boxes; segmentation labels are YOLO polygons. One drawn ROI represents one object. A single polygon cannot encode arbitrary disconnected instances or holes; use separate object regions or an appropriate outline and inspect the preview.

## Persistent outputs

Annotation layout remains `<Dataset Root>/<project>/<Detection|Segmentation>/<source>/Raw + Labels + Annotated/{Pass,Fail}`. Distinct sources do not replace each other. Same-source reruns archive the previous run under `.history`. Validated snapshots and completed models are independent durable records; they remain after closing/reopening the UI. Missing registry pointers are recovered from manifests in configured storage roots. Keep those physical folders and backend Settings when updating the application.

Inference outputs use `<Inference Results>/<project>/<unique-run-id>/` with request/run/progress JSON, log, per-image/frame `predictions.jsonl`, and annotated images or browser-compatible WebM. Runs can be cancelled and are retained in history. Interrupted work is not claimed complete. Inference, annotation and training share the GPU lock; no simultaneous training/inference GPU processes are launched by the app.

## Verification

```powershell
& '..\.conda\sail\python.exe' -m unittest discover -s . -p test_annotation_inference.py -v
& '..\.conda\sail\python.exe' -m unittest discover -s . -p test_training_pipeline.py -v
& '..\.conda\sail\python.exe' -m unittest discover -s . -p test_dataset_validation.py -v
```

These tests use temporary files and mocked ML/process boundaries, not fabricated production predictions. They cover both label modes, correction prompts, failure accounting, output persistence, model integrity, training batch counts, gallery endpoints, inference rendering, cancellation and incomplete-output rejection. A real small GPU run in each model/task mode is still required on the test PC before production use. Official APIs checked: [SAM predictor](https://docs.ultralytics.com/reference/models/sam/predict/) and [trainer callbacks](https://docs.ultralytics.com/usage/callbacks/).
