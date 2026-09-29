"""CPU-only contract/regression tests; YOLO execution is mocked, never simulated in the app."""
import json
import logging
import os
import sys
import tempfile
import threading
import types
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

BOOT = tempfile.TemporaryDirectory(prefix="sail-training-bootstrap-")
os.environ["SAIL_WORK_ROOT"] = BOOT.name
from PIL import Image
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient
from services import dataset_store as store, training_jobs as jobs, ground_truth as gt
from services.training_split import split_dataset
from routers.training import TrainingRequest, router
from ml import train as runner


def tearDownModule():
    logging.shutdown(); BOOT.cleanup()


class TrainingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sail-training-test-")
        self.root = Path(self.temp.name).resolve()
        self.patches = [patch.object(jobs, "WORK_ROOT", self.root), patch.object(jobs, "SETTINGS_FILE", self.root / "settings.json"),
                        patch.object(store, "WORK_ROOT", self.root), patch.object(store, "SETTINGS_FILE", self.root / "settings.json"),
                        patch.object(jobs, "CONDA_PYTHON", Path(sys.executable))]
        for p in self.patches: p.start()
        self.app = FastAPI(); self.app.include_router(router); self.client = TestClient(self.app)
        jobs.THREADS.clear(); jobs.CANCEL.clear()

    def tearDown(self):
        self.client.close()
        for p in reversed(self.patches): p.stop()
        self.temp.cleanup()

    def snapshot(self, mode="detection", count=10):
        directory = self.root / mode
        (directory / "images").mkdir(parents=True); (directory / "labels").mkdir()
        pairs = []
        for i in range(count):
            raw, label = directory / "images" / f"{i}.png", directory / "labels" / f"{i}.txt"
            Image.new("RGB", (32, 32), (i*10, 50, 60)).save(raw)
            label.write_text("0 0.5 0.5 0.4 0.4" if mode == "detection" else "0 0.3 0.3 0.7 0.3 0.7 0.7 0.3 0.7", encoding="utf-8")
            pairs.append({"image": "images/" + raw.name, "label": "labels/" + label.name,
                          "imageSha256": store.file_hash(raw), "labelSha256": store.file_hash(label)})
        return {"id": "a"*32, "directory": str(directory), "projectName": "Project A", "name": "Dailies",
                "mode": mode, "classes": ["Phone: #1", "Laptop"], "pairs": pairs}

    def test_split_is_deterministic_paired_and_non_destructive(self):
        for mode in ("detection", "segmentation"):
            data = self.snapshot(mode)
            a = split_dataset(data, self.root / (mode+"-a"))
            b = split_dataset(data, self.root / (mode+"-b"))
            self.assertEqual(a, b); self.assertEqual(a["counts"], {"train": 7, "val": 2, "test": 1})
            for pair in a["pairs"]:
                for key, folder in (("image", "images"), ("label", "labels")):
                    target = self.root / (mode+"-a") / folder / pair["split"] / Path(pair[key]).name
                    self.assertEqual(store.file_hash(target), pair[key+"Sha256"])
                    self.assertEqual(store.file_hash(Path(data["directory"]) / pair[key]), pair[key+"Sha256"])
            yaml = (self.root / (mode+"-a") / "data.yaml").read_text()
            self.assertIn(json.dumps(data["classes"]), yaml)
            self.assertNotIn("blister", yaml)
            with self.assertRaises(FileExistsError): split_dataset(data, self.root / (mode+"-a"))

    def test_duplicate_images_never_cross_splits(self):
        data = self.snapshot()
        import shutil
        shutil.copy2(Path(data["directory"]) / data["pairs"][0]["image"], Path(data["directory"]) / data["pairs"][1]["image"])
        data["pairs"][1]["imageSha256"] = data["pairs"][0]["imageSha256"]
        result = split_dataset(data, self.root / "split")
        matching = [p["split"] for p in result["pairs"] if p["imageSha256"] == data["pairs"][0]["imageSha256"]]
        self.assertEqual(len(set(matching)), 1)
        label = Path(data["directory"]) / data["pairs"][1]["label"]
        label.write_text("1 0.5 0.5 0.4 0.4")
        data["pairs"][1]["labelSha256"] = store.file_hash(label)
        with self.assertRaises(ValueError): split_dataset(data, self.root / "conflicting")

    def test_missing_changed_invalid_labels_and_too_small_rejected(self):
        data = self.snapshot(count=2)
        with self.assertRaises(ValueError): split_dataset(data, self.root / "tiny")
        data["pairs"][0]["image"] = "../escape.png"
        with self.assertRaises(HTTPException): split_dataset(data, self.root / "escape")
        data = self.snapshot("segmentation", 3)
        label = Path(data["directory"]) / data["pairs"][0]["label"]
        label.write_text("99 0.5 0.5 0.1 0.1")
        with self.assertRaises(ValueError): split_dataset(data, self.root / "tampered")
        data["pairs"][0]["labelSha256"] = store.file_hash(label)
        with self.assertRaises((ValueError, HTTPException)): split_dataset(data, self.root / "invalid")
        label.unlink()
        with self.assertRaises(ValueError): split_dataset(data, self.root / "missing")

    def test_api_config_validation_and_unknown_ids(self):
        self.assertEqual(TrainingRequest(datasetId="a"*32, imageSize=720).imageSize, 720)
        for change in ({"ratios": [80,20,10]}, {"imageSize":721}, {"mode":"incremental"}, {"device":"cpu"}, {"architecture":"../../evil.pt"}, {"epochs":0}):
            self.assertEqual(self.client.post("/training/runs", json={"datasetId":"a"*32, **change}).status_code, 422)
        self.assertEqual(self.client.get("/training/runs/invalid").status_code, 404)
        self.assertEqual(self.client.get("/models/not-found/download").status_code, 404)

    def launch_stub(self, data):
        config = TrainingRequest(datasetId=data["id"]).model_dump()
        with patch.object(jobs, "get_snapshot", return_value=data), patch.object(jobs.threading, "Thread") as thread:
            thread.return_value.is_alive.return_value = False
            return jobs.start(config)

    def test_runs_use_settings_and_do_not_replace_previous_outputs(self):
        data = self.snapshot()
        store.atomic_json(self.root / "settings.json", {"modelsDir": str(self.root / "custom-models")})
        first, second = self.launch_stub(data), self.launch_stub(data)
        self.assertNotEqual(first["id"], second["id"])
        self.assertEqual(Path(first["directory"]).parent.name, "Trained Models")
        self.assertTrue((Path(first["directory"]) / "request.json").is_file())
        self.assertEqual(jobs.get_run(first["id"])["state"], "interrupted")

    def test_successful_worker_registers_only_owned_checkpoint_and_download(self):
        data = self.snapshot(); run = self.launch_stub(data); directory = Path(run["directory"])
        def launch(*args, **kwargs):
            self.assertEqual(args[0][0], sys.executable)
            self.assertNotIn("shell", kwargs)
            best = directory / "yolo/weights/best.pt"; best.parent.mkdir(parents=True); best.write_bytes(b"test-checkpoint")
            store.atomic_json(directory / "result.json", {"best": str(best), "testMetrics": {"mAP":.8}, "validationMetrics":{}, "ultralyticsVersion":"test"})
            process = MagicMock(); process.poll.return_value = 0; process.returncode = 0
            return process
        runtime = MagicMock(); runtime.acquire_for_worker.return_value = True
        with patch.object(jobs, "runtime", runtime), patch.object(jobs.subprocess, "Popen", side_effect=launch): jobs.run(run, data)
        self.assertEqual(jobs.get_run(run["id"])["state"], "complete")
        model = jobs.get_model(run["id"])
        self.assertEqual(model["datasetId"], data["id"])
        self.assertEqual(Path(model["path"]).name, f"Dailies_detection_{run['id'][:8]}_best.pt")
        self.assertEqual(self.client.get(f"/models/{run['id']}/download").content, b"test-checkpoint")
        gallery = self.client.get(f"/training/runs/{run['id']}/images?split=train").json()
        self.assertEqual(gallery["total"], 7)
        self.assertEqual(len(gallery["items"]), 7)
        self.assertEqual(self.client.get(gallery["items"][0]["url"]).status_code, 200)
        Path(model["path"]).write_bytes(b"changed")
        with self.assertRaises(HTTPException): jobs.get_model(run["id"])

    def test_failure_and_cancel_never_register_model(self):
        data = self.snapshot()
        for cancelled in (False, True):
            run = self.launch_stub(data)
            if cancelled: jobs.CANCEL[run["id"]].set()
            runtime = MagicMock(); runtime.acquire_for_worker.return_value = not cancelled
            process = MagicMock(); process.poll.return_value = 1; process.returncode = 1
            with patch.object(jobs, "runtime", runtime), patch.object(jobs.subprocess, "Popen", return_value=process): jobs.run(run, data)
            self.assertEqual(jobs.get_run(run["id"])["state"], "cancelled" if cancelled else "failed")
        self.assertEqual(jobs.list_models(), [])

    def test_incremental_requires_exact_project_task_and_classes(self):
        data = self.snapshot()
        config = TrainingRequest(datasetId=data["id"], mode="incremental", baseModelId="b"*32).model_dump()
        base = {"projectName":data["projectName"], "mode":data["mode"], "classes":data["classes"], "path":"base.pt"}
        for field, value in (("projectName", "Other"), ("mode","segmentation"), ("classes",list(reversed(data["classes"])))):
            with patch.object(jobs, "get_snapshot", return_value=data), patch.object(jobs, "get_model", return_value={**base, field:value}):
                with self.assertRaises(HTTPException): jobs.start(config)

    def test_runner_contract_trains_then_tests_best_for_both_tasks(self):
        for mode in ("detection", "segmentation"):
            data = self.snapshot(mode); directory = self.root / (mode+"-run"); directory.mkdir()
            config = TrainingRequest(datasetId=data["id"], epochs=1).model_dump()
            source = "yolov8n-seg.pt" if mode == "segmentation" else "yolov8n.pt"
            store.atomic_json(directory / "request.json", {"config":config, "snapshot":data, "modelSource":source})
            calls = []
            batches = []
            class FakeYOLO:
                def __init__(self, path):
                    self.task = "segment" if mode == "segmentation" else "detect"
                    self.names = dict(enumerate(data["classes"]))
                    self.trainer = types.SimpleNamespace(best=directory/"yolo/weights/best.pt", epoch=0, metrics={"mAP":.5},
                        train_loader=types.SimpleNamespace(dataset=list(range(7))), preprocess_batch=lambda b: b)
                    self.callbacks = {}
                def add_callback(self, name, callback): self.callbacks[name] = callback
                def train(self, **kwargs):
                    calls.append(("train", kwargs)); self.trainer.best.parent.mkdir(parents=True)
                    self.trainer.best.write_bytes(b"checkpoint")
                    self.callbacks["on_train_start"](self.trainer)
                    self.callbacks["on_train_epoch_start"](self.trainer)
                    for size in (4, 3):
                        self.trainer.preprocess_batch({"img": list(range(size)), "im_file": [f"{i}.png" for i in range(size)]})
                        self.callbacks["on_train_batch_end"](self.trainer)
                        batches.append(store.read_json(directory / "progress.json", {}))
                    self.callbacks["on_fit_epoch_end"](self.trainer)
                def val(self, **kwargs): calls.append(("test", kwargs)); return types.SimpleNamespace(results_dict={"mAP":.4})
            module = types.ModuleType("ultralytics"); module.YOLO=FakeYOLO; module.__version__="test"
            with patch.dict(sys.modules, {"ultralytics":module}): runner.main(str(directory))
            self.assertEqual([c[0] for c in calls], ["train", "test"])
            self.assertEqual(calls[1][1]["split"], "test")
            self.assertEqual(calls[0][1]["workers"], 0)
            result = store.read_json(directory / "result.json", {})
            self.assertEqual(result["testMetrics"]["mAP"], .4)
            self.assertEqual([b["processedImages"] for b in batches], [4, 7])
            self.assertTrue(all(b["totalImages"] == 7 for b in batches))
            self.assertEqual(len(batches[-1]["batchImages"]), 3)
            self.assertEqual(store.read_json(directory / "progress.json", {})["processedImages"], 7)

    def test_user_ground_truth_is_frozen_class_aware_and_not_self_scored(self):
        for mode in ("detection", "segmentation"):
            data = self.snapshot(mode, 3); raw = Path(data["directory"]) / data["pairs"][0]["image"]
            label = Path(data["directory"]) / data["pairs"][0]["label"]
            item = {"id":"item", "raw":data["pairs"][0]["image"], "label":data["pairs"][0]["label"], "status":"Pass", "filename":raw.name}
            data.update(state="complete", revision=1, items=[item])
            with patch.object(store,"get_dataset",return_value=data):
                with self.assertRaises(HTTPException): gt.freeze(data["id"], item["id"], 1, False)
                reference = gt.freeze(data["id"], item["id"], 1, True)
            hashes={"rawHash":store.file_hash(raw),"labelHash":store.file_hash(label)}
            self.assertEqual(gt.compare(data,item,raw,label,hashes)["status"], "review")
            other={**data,"id":"b"*32}
            self.assertEqual(gt.compare(other,item,raw,label,hashes)["status"], "candidate")
            label.write_text(label.read_text().replace("0 ","1 ",1))
            hashes["labelHash"]=store.file_hash(label)
            self.assertEqual(gt.compare(other,item,raw,label,hashes)["status"], "review")
            self.assertEqual(store.file_hash(Path(reference["directory"])/"label.txt"), reference["labelHash"])
            self.assertIsNone(gt.compare({**other,"projectName":"other"},item,raw,label,hashes))
            result={"rawHash":reference["rawHash"], "referenceId":reference["id"], "referenceLabelHash":reference["labelHash"]}
            gt.verify_result_reference(data, result)
            (Path(reference["directory"])/"label.txt").write_text("tampered")
            with self.assertRaises(HTTPException): gt.verify_result_reference(data, result)

if __name__ == "__main__": unittest.main()
