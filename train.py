"""Application adaptation of the supplied train.py; invoked only by a registered job."""
import json
import math
import os
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


def save(path, value):
    """Best-effort progress reporting must never terminate a training run."""
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")
        for attempt in range(8):
            try:
                temporary.replace(path)
                return
            except PermissionError:
                if attempt == 7:
                    return
                time.sleep(min(0.05 * (2 ** attempt), 0.5))
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass


def metrics(values):
    return {str(k): float(v) for k, v in values.items() if math.isfinite(float(v))}


def main(directory):
    from ultralytics import YOLO
    import ultralytics
    directory = Path(directory)
    request = json.loads((directory / "request.json").read_text(encoding="utf-8"))
    c = request["config"]
    model = YOLO(request["modelSource"])
    expected_task = "segment" if request["snapshot"]["mode"] == "segmentation" else "detect"
    if model.task != expected_task:
        raise ValueError("Checkpoint task does not match the validated dataset.")
    batch_state = {"processedImages": 0, "batchImages": [], "batchSize": 0}
    def epoch_number(trainer):
        # on_train_start runs before some Ultralytics releases create .epoch.
        # Progress reporting must be compatible with both callback lifecycles.
        return int(getattr(trainer, "epoch", -1)) + 1

    def report(trainer, phase):
        save(directory / "progress.json", {"epoch": epoch_number(trainer), "epochs": c["epochs"],
             "phase": phase, "processedImages": batch_state["processedImages"],
             "totalImages": len(trainer.train_loader.dataset), "batchImages": batch_state["batchImages"],
             "updatedAt": datetime.now(timezone.utc).isoformat()})
    def train_start(trainer):
        original = trainer.preprocess_batch
        def capture_batch(batch):
            batch_state["batchSize"] = len(batch["img"])
            batch_state["batchImages"] = [Path(p).name for p in batch.get("im_file", [])]
            return original(batch)
        trainer.preprocess_batch = capture_batch
        report(trainer, "training")
    def epoch_start(trainer):
        batch_state.update(processedImages=0, batchImages=[])
        report(trainer, "training")
    def batch_end(trainer):
        batch_state["processedImages"] += batch_state["batchSize"]
        report(trainer, "training")
    def progress(trainer):
        save(directory / "progress.json", {"epoch": epoch_number(trainer), "epochs": c["epochs"],
             "metrics": metrics(trainer.metrics), "phase": "training",
             "processedImages": batch_state["processedImages"],
             "totalImages": len(trainer.train_loader.dataset) if hasattr(trainer, "train_loader") else 0})
    model.add_callback("on_train_start", train_start)
    model.add_callback("on_train_epoch_start", epoch_start)
    model.add_callback("on_train_batch_end", batch_end)
    model.add_callback("on_fit_epoch_end", progress)
    model.train(data=str(directory / "split" / "data.yaml"), imgsz=c["imageSize"],
        epochs=c["epochs"], batch=c["batchSize"], device=c["device"], workers=0,
        patience=c["patience"], optimizer=c["optimizer"], lr0=c["learningRate"],
        seed=c["seed"], deterministic=True, project=str(directory), name="yolo", exist_ok=False,
        pretrained=c["pretrained"], freeze=c["freezeLayers"] if c["mode"] == "incremental" else 0,
        save=True, save_period=1, plots=True, amp=False)
    best = Path(model.trainer.best).resolve()
    if not best.is_file() or not best.is_relative_to(directory.resolve()):
        raise RuntimeError("Training did not produce a best.pt checkpoint.")
    trained = YOLO(str(best))
    names = [trained.names[i] for i in range(len(trained.names))]
    if names != request["snapshot"]["classes"]:
        raise RuntimeError("Trained class mapping differs from the validated snapshot.")
    report(model.trainer, "testing")
    # Test is never used for gradient updates or checkpoint selection.
    result = trained.val(data=str(directory / "split" / "data.yaml"), split="test",
                         imgsz=c["imageSize"], batch=c["batchSize"], device=c["device"],
                         workers=0, project=str(directory), name="test", plots=True)
    save(directory / "result.json", {"best": str(best), "validationMetrics": metrics(model.trainer.metrics),
          "testMetrics": metrics(result.results_dict), "ultralyticsVersion": ultralytics.__version__})


if __name__ == "__main__":
    from process_guard import watch_parent
    watch_parent()
    os.environ.setdefault("YOLO_AUTOINSTALL", "false")
    main(sys.argv[1])
