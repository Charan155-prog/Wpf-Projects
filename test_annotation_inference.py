"""CPU regression contracts. ML predictions are mocked only in these tests."""
import json
import logging
import os
import sys
import tempfile
import threading
import types
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

BOOT = tempfile.TemporaryDirectory(prefix="sail-integration-bootstrap-")
os.environ["SAIL_WORK_ROOT"] = BOOT.name
import cv2
import numpy as np
from PIL import Image
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient
from services import annotation_jobs as annotation, dataset_store as store, inference_jobs as inference
from services import dataset_validation as correction, training_jobs as training
from services.model_runtime import ModelRuntime
from ml import run_annotation, run_validation, sam2_backend, predict
from routers.datasets import Region
from routers.inference import router as inference_router


def tearDownModule():
    logging.shutdown()
    BOOT.cleanup()


class IntegrationTests(unittest.TestCase):
    def test_grounding_source_distinguishes_local_paths_from_hub_ids(self):
        self.assertEqual(sam2_backend.grounding_source("IDEA-Research/grounding-dino-base"),
                         ("IDEA-Research/grounding-dino-base", {}))
        with self.assertRaisesRegex(ValueError, "folder not found on the backend PC"):
            sam2_backend.grounding_source(str(self.root / "missing-dino"))
        folder = self.root / "dino"
        folder.mkdir()
        with self.assertRaisesRegex(ValueError, "Incomplete Grounding DINO"):
            sam2_backend.grounding_source(str(folder))
        for name in ("config.json", "preprocessor_config.json", "tokenizer_config.json", "tokenizer.json", "model.safetensors"):
            (folder / name).touch()
        self.assertEqual(sam2_backend.grounding_source(f'"{folder}"'),
                         (str(folder.resolve()), {"local_files_only": True}))

    def test_live_display_waits_for_ack_and_releases_on_disconnect_or_cancel(self):
        state = {"condition": threading.Condition(), "clients": 1,
                 "connected_once": True, "ack": 0, "produced": 1}
        cancelled = threading.Event()
        with patch.dict(annotation.LIVE_DISPLAYS, {"display-test": state}):
            for release in ("ack", "disconnect", "cancel"):
                state.update(clients=1, ack=0)
                cancelled.clear()
                finished = threading.Event()
                def wait():
                    annotation.wait_for_display("display-test", 1, cancelled)
                    finished.set()
                worker = threading.Thread(target=wait)
                worker.start()
                try:
                    self.assertFalse(finished.wait(.05))
                    annotation.acknowledge_display("display-test", 0)
                    self.assertFalse(finished.wait(.05))
                    if release == "ack":
                        annotation.acknowledge_display("display-test", 1)
                    elif release == "disconnect":
                        annotation.display_connection("display-test", False)
                    else:
                        cancelled.set()
                    self.assertTrue(finished.wait(1))
                finally:
                    cancelled.set()
                    worker.join(2)

    def test_parallel_workflow_guard_only_blocks_overlapping_source_and_mode(self):
        from services import workflow_guard as guard
        thread = MagicMock(); thread.is_alive.return_value = True
        operation = MagicMock(return_value={"id": "new"})
        def launch(source, mode):
            return operation(source, mode)
        with patch.object(guard, "RUNNING", [((self.root / "camera-a").resolve(), "detection", "annotation", thread)]), patch.object(guard, "source_identity", side_effect=lambda kind, args, namespace: (args["source"], args["mode"])):
            # A completed launch with no worker requires no retained reservation.
            with patch.dict(launch.__globals__, {"THREADS": {}}):
                start = guard.guarded_start("inference")(launch)
                with self.assertRaises(HTTPException) as conflict:
                    start(str(self.root / "camera-a"), "detection")
                self.assertEqual(conflict.exception.status_code, 409)
                operation.assert_not_called()
                start(str(self.root / "camera-b"), "detection")
                start(str(self.root / "camera-a"), "segmentation")
                self.assertEqual(operation.call_count, 2)
                thread.is_alive.return_value = False
                start(str(self.root / "camera-a"), "detection")
                self.assertEqual(operation.call_count, 3)

    def test_semi_preview_only_processes_selected_source_without_publishing(self):
        sources = [self.raw(f"source-{i}.png") for i in range(3)]
        def process(engine, path, request, output):
            self.assertEqual(path, sources[1])
            self.assertEqual(request["source_files"], [str(sources[1])])
            self.assertEqual(request["confidence_threshold"], .17)
            return {"filename": path.name, "overlays": [], "detections": 0}
        with patch.object(annotation, "WORK_ROOT", self.root), patch.object(annotation, "files_for", return_value=sources), patch.object(annotation, "active_job", return_value=None), patch.object(annotation.runtime, "annotation_model", return_value=object()), patch.object(annotation, "process_image", side_effect=process) as called, patch.object(annotation, "create_dataset") as create, patch.object(annotation, "_start_job") as start:
            result = annotation.preview_source_image("project", "detection", [], [], ["box"], "source", 1, -1, "sam2", .17)
        self.assertEqual(result["filename"], sources[1].name)
        called.assert_called_once()
        create.assert_not_called()
        start.assert_not_called()
        self.assertFalse((self.root / ".jobs").exists())

    def test_visual_reference_forwarding_restores_model_after_error(self):
        class Model:
            def _encode_prompt(self, **kwargs):
                return kwargs
        model = Model()
        with self.assertRaises(RuntimeError):
            with run_annotation.visual_reference_prompt(model, ("source appearance", "source mask")):
                value = model._encode_prompt(backbone_out="different target image")
                self.assertEqual(value["visual_prompt_embed"], "source appearance")
                self.assertEqual(value["backbone_out"], "different target image")
                raise RuntimeError("simulated inference error")
        self.assertNotIn("_encode_prompt", model.__dict__)
        self.assertEqual(model._encode_prompt(), {})

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sail-integration-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def raw(self, name="raw.png"):
        path = self.root / name
        Image.new("RGB", (128, 96), (60, 80, 120)).save(path)
        return path

    def test_disconnected_mask_exports_all_regions_as_one_instance(self):
        mask = np.zeros((96, 128), np.uint8)
        mask[10:30, 8:28] = 1
        mask[50:80, 70:110] = 1
        mask[8:18, 100:115] = 1
        source = self.raw()
        output = self.root / "output"
        output.mkdir()
        result = run_annotation.save_annotation(cv2.imread(str(source)), [(0, mask, np.array([8, 8, 115, 80]))], [.8], source,
            {"mode": "segmentation", "prompts": ["carton"]}, output)
        rows = (output / "raw.txt").read_text().splitlines()
        self.assertEqual(len(rows), 1)
        self.assertEqual(result["detections"], 1)
        store.validate_labels(output / "raw.txt", "segmentation", 1)
        coordinates = np.array(list(map(float, rows[0].split()[1:]))).reshape(-1, 2)
        polygon = np.rint(coordinates * [128, 96]).astype(np.int32)
        restored = np.zeros_like(mask)
        cv2.fillPoly(restored, [polygon], 1)
        self.assertTrue(np.all(restored[mask > 0] == 1), "Every disconnected region must survive export")
        self.assertLess(int((restored > mask).sum()), 150, "Do not fill the space between the regions")
        self.assertNotIn("error", result)

    def test_single_mask_and_detection_box_export_unchanged(self):
        mask = np.zeros((96, 128), np.uint8)
        mask[10:30, 8:28] = 1
        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        np.testing.assert_array_equal(run_annotation.mask_polygon(mask), contours[0].reshape(-1, 2))
        self.assertEqual(run_annotation.label_lines("detection", [(0, mask, np.array([0, 0, 128, 96]))], 128, 96),
                         ["0 0.500000 0.500000 1.000000 1.000000"])

    def test_slider_preview_selects_one_image_and_never_creates_dataset(self):
        sources = [self.raw(f"{index}.png") for index in range(4)]
        for model in ("sam2", "sam3"):
            with patch.object(annotation, "WORK_ROOT", self.root), patch.object(annotation, "LOCAL_SAM3_ENABLED", True), patch.object(annotation, "active_job", return_value=False), patch.object(annotation, "files_for", return_value=sources), patch.object(annotation, "source_info_for", return_value={"name": "source", "path": str(self.root / "source")}), patch.object(annotation, "dataset_project_directory", return_value=self.root / "project"), patch.object(annotation, "create_dataset") as create, patch.object(annotation, "_start_job", side_effect=lambda directory, prompts: directory):
                directory = annotation.create_job_from_source("project", "detection", [], [], ["carton"], "source", .2, pre_annotation=True, sample_indices=[0, 1, 2, 3], preview_index=2, annotation_model=model)
            request = store.read_json(directory / "request.json", {})
            self.assertEqual(request["source_files"], [str(sources[2])])
            self.assertEqual(request["annotation_model"], model)
            self.assertEqual(request["confidence_threshold"], .2)
            create.assert_not_called()

    def test_failed_image_is_retried_and_next_image_still_processed(self):
        source = self.raw()
        job = self.root / "job"; output = job / "output"; output.mkdir(parents=True)
        project = self.root / "project"; project.mkdir()
        store.atomic_json(job / "job.json", {"projectDirectory": str(project)})
        missing = self.root / "missing.tiff"
        store.atomic_json(job / "request.json", {"source_files": [str(missing), str(source)], "mode": "detection", "prompts": ["carton"], "annotation_model": "sam2"})
        calls = []
        def fake(processor, path, request, destination, **kwargs):
            calls.append(path.name)
            if path == missing: raise ValueError("Cannot decode input")
            return run_annotation.save_annotation(cv2.imread(str(path)), [], [], path, request, destination)
        with patch.object(annotation.runtime, "annotation_model", return_value=object()) as selected, patch.object(run_annotation, "annotate_image", side_effect=fake), patch.object(run_annotation, "publish_results"):
            annotation._run_job(job, threading.Event())
        self.assertEqual(calls, ["missing.tiff", "missing.tiff", "raw.png"])
        selected.assert_called_once_with("sam2")
        ledger = store.read_json(job / "coverage.json", {})
        self.assertEqual(ledger["attempted"], 2)
        self.assertEqual(ledger["failed"][0]["filename"], "missing.tiff")
        self.assertEqual(ledger["noObjects"], ["raw.png"])
        progress = store.read_json(job / "progress.json", {})
        self.assertEqual(progress["state"], "complete")
        self.assertEqual(len(progress["items"]), 2)
        self.assertTrue((output / "missing_annotated.jpg").is_file())

    def test_manual_roi_uses_raw_and_never_loads_a_model(self):
        source = self.raw()
        roi = {"classId": 0, "points": [[.2,.2],[.7,.2],[.7,.7],[.2,.7]]}
        for mode in ("detection", "segmentation"):
            output = self.root / mode; output.mkdir()
            with patch.object(run_validation.runtime, "annotation_model", side_effect=AssertionError("Manual ROI must not load ML")):
                run_validation.correct_image(source, [roi], ["carton"], mode, output, "sam2", "manual")
            store.validate_labels(output / "label.txt", mode, 1)
            parts = (output / "label.txt").read_text().split()
            self.assertEqual(len(parts), 5 if mode == "detection" else 9)
            self.assertTrue((output / "annotated.jpg").is_file())

    def test_sam2_keeps_full_frame_box_and_click_prompts(self):
        engine = sam2_backend.Sam2Backend.__new__(sam2_backend.Sam2Backend)
        events = []
        class Predictor:
            def reset_image(self): events.append("reset")
            def set_image(self, image): events.append(image.shape)
            def __call__(self, **kwargs):
                events.append(kwargs)
                return [types.SimpleNamespace(masks=types.SimpleNamespace(xy=[np.array([[10,10],[30,10],[30,30],[10,30]])]))]
        engine.predictor = Predictor(); engine.key = None
        frame = np.zeros((96,128,3), np.uint8)
        engine.set_image(frame, "same"); engine.set_image(frame, "same")
        masks = engine.predict([5,5,40,40], [[20,20],[35,35]], [1,0])
        self.assertEqual(events[:2], ["reset", (96,128,3)])
        self.assertEqual(len(events), 3)
        self.assertEqual(events[-1], {"bboxes": [[5,5,40,40]], "points": [[[20,20],[35,35]]], "labels": [[1,0]]})
        self.assertEqual(masks[0].shape, (96,128))
        self.assertEqual(int(masks[0][20,20]), 1)

    def test_model_choice_and_click_validation(self):
        runtime = ModelRuntime()
        with patch.object(runtime, "sam2", return_value="two"), patch.object(runtime, "sam3", return_value="three"):
            self.assertEqual(runtime.annotation_model("sam2"), "two")
            self.assertEqual(runtime.annotation_model("sam3"), "three")
            with self.assertRaises(ValueError): runtime.annotation_model("unknown")
        for clicks, labels in (([[.2,.2]], []), ([[2,.2]], [1]), ([[.2,.2]], [3])):
            with self.assertRaises(ValueError): Region(classId=0, points=[[0,0],[1,0],[1,1]], clicks=clicks, clickLabels=labels)

    def test_sam2_dataset_rejects_sam3_correction(self):
        raw = self.raw()
        data = {"directory": str(self.root), "state": "complete", "revision": 0, "classes": ["carton"], "annotationModel": "sam2",
                "items": [{"id": "item", "raw": raw.name}]}
        with patch.object(correction, "get_dataset", return_value=data):
            with self.assertRaises(HTTPException) as raised:
                correction.preview_correction("a"*32, "item", [{"classId":0, "points": [[0,0],[1,0],[1,1]]}], 0, "sam3")
        self.assertEqual(raised.exception.status_code, 422)

    def test_reprocess_preview_does_not_modify_original_label(self):
        raw = self.raw("label.png")
        original = self.root / "original.txt"; original.write_text("original")
        data = {"directory": str(self.root), "state": "complete", "revision": 0, "classes": ["carton"], "mode": "detection",
                "items": [{"id": "item", "raw": raw.name, "label": original.name}]}
        def fake(processor, source, request, output):
            mask = np.zeros((96,128), np.uint8); mask[20:40,20:40] = 1
            return run_annotation.save_annotation(cv2.imread(str(source)), [(0,mask,np.array([20,20,40,40]))], [.8], source, request, output)
        with patch.object(correction, "get_dataset", return_value=data), patch.object(run_annotation, "annotate_image", side_effect=fake), patch.object(annotation.runtime, "annotation_model", return_value=object()):
            result = correction.preview_reprocess("a"*32, "item", 0, "sam2", .25)
        self.assertTrue((self.root / ".edits" / result["previewId"] / "label.txt").is_file())
        self.assertEqual(original.read_text(), "original")

    def test_snapshot_recovery_without_annotation_registry(self):
        target = self.root / "validated-datasets/P/Detection/Dailies" / ("b"*32)
        target.mkdir(parents=True)
        data = {"id":"b"*32, "name":"Dailies", "directory":str(target), "createdAt":"2026-09-07", "pairs":[{"image":"images/1.png","label":"labels/1.txt"}]}
        store.atomic_json(target / "dataset.json", data)
        with patch.object(store, "WORK_ROOT", self.root), patch.object(store, "SETTINGS_FILE", self.root / "settings.json"), patch.object(store, "list_datasets", return_value=[]):
            self.assertEqual(store.training_datasets()[0]["name"], "Dailies")
            self.assertEqual(store.get_snapshot("b"*32)["pairs"], data["pairs"])
            self.assertTrue((self.root / ".snapshots" / ("b"*32 + ".json")).is_file())

    def test_completed_models_recover_from_run_manifests(self):
        target = self.root / "models/P/Detection" / ("c"*32); target.mkdir(parents=True)
        weight = target / "Dailies_detection_cccccccc_best.pt"; weight.write_bytes(b"checkpoint")
        model = {"id":"c"*32, "name":"Dailies", "filename":weight.name, "path":str(weight), "sha256":store.file_hash(weight)}
        store.atomic_json(target / "run.json", {"id":"c"*32, "directory":str(target), "state":"complete", "createdAt":"2026-09-07", "model":model})
        with patch.object(training, "WORK_ROOT", self.root), patch.object(training, "SETTINGS_FILE", self.root / "settings.json"):
            self.assertEqual(training.get_model("c"*32)["filename"], weight.name)
            self.assertTrue((self.root / ".training" / ("c"*32 + ".json")).is_file())
            weight.write_bytes(b"tampered")
            with self.assertRaises(HTTPException): training.get_model("c"*32)

    @staticmethod
    def prediction(segmentation=False, empty=False):
        box = types.SimpleNamespace(conf=np.array([.9]), cls=np.array([0]), xyxy=np.array([[30,40,70,70]]))
        return types.SimpleNamespace(boxes=[] if empty else [box], names={0:"carton"},
            masks=types.SimpleNamespace(xy=[np.array([[30,40],[70,40],[70,70],[30,70]])]) if segmentation and not empty else None)

    def test_inference_rendering_keeps_original_coordinates_and_background(self):
        frame = np.full((96,128,3), 100, np.uint8)
        result, objects = predict.render(frame, self.prediction(True), "segmentation", False)
        self.assertTrue(np.array_equal(result[90,120], frame[90,120]))
        self.assertFalse(np.array_equal(result[50,50], frame[50,50]))
        self.assertEqual(objects[0]["polygon"][0], [30,40])
        with self.assertRaises(ValueError): predict.render(frame, self.prediction(), "segmentation")
        empty, objects = predict.render(frame, self.prediction(empty=True), "segmentation")
        self.assertTrue(np.array_equal(empty, frame)); self.assertEqual(objects, [])

    def test_inference_runner_processes_every_image_for_both_modes(self):
        raw = self.raw()
        for mode in ("detection", "segmentation"):
            folder = self.root / mode; folder.mkdir()
            store.atomic_json(folder / "request.json", {"model":{"path":"best.pt", "mode":mode, "classes":["carton"]},
                "config":{"imageSize":640,"confidence":.9,"iou":.45,"drawBoxes":True,"maskAlpha":.35},
                "sourceType":"folder", "files":[str(raw),str(raw)]})
            owner = self; calls = []
            class YOLO:
                def __init__(self, path): self.task = "segment" if mode == "segmentation" else "detect"; self.names = {0:"carton"}
                def predict(self, **kwargs): calls.append(kwargs); return [owner.prediction(mode == "segmentation")]
            module = types.ModuleType("ultralytics"); module.YOLO = YOLO
            with patch.dict(sys.modules, {"ultralytics": module}): predict.main(folder)
            result = store.read_json(folder / "result.json", {})
            self.assertEqual(result["processed"], 2); self.assertEqual(len(result["assets"]), 2)
            self.assertEqual(len((folder / "predictions.jsonl").read_text().splitlines()), 2)
            self.assertEqual(calls[0]["retina_masks"], mode == "segmentation")
            self.assertEqual(calls[0]["device"], 0)
            self.assertNotEqual(result["assets"][0]["path"], result["assets"][1]["path"])

    def test_inference_api_validation_and_restart_history(self):
        app = FastAPI(); app.include_router(inference_router)
        with TestClient(app) as client:
            for change in ({"confidence":2}, {"imageSize":641}, {"maskAlpha":2}):
                response = client.post("/inference/runs", json={"modelId":"d"*32, "sourcePath":"x", **change})
                self.assertEqual(response.status_code, 422)
        target = self.root / "inference/P" / ("d"*32); target.mkdir(parents=True)
        store.atomic_json(target / "run.json", {"id":"d"*32,"directory":str(target),"state":"running","createdAt":"2026-09-07"})
        with patch.object(inference, "WORK_ROOT", self.root), patch.object(inference, "configured_root", return_value=self.root / "inference"):
            self.assertEqual(inference.list_runs()[0]["state"], "interrupted")
            with self.assertRaises(HTTPException): inference.asset("d"*32, 100)

    def test_inference_stop_retains_incremental_images(self):
        raw = self.raw()
        folder = self.root / "stopped"; folder.mkdir()
        store.atomic_json(folder / "request.json", {"model": {"path": "best.pt", "mode": "detection", "classes": ["carton"]},
            "config": {"imageSize": 640, "confidence": .9, "iou": .45, "drawBoxes": True, "maskAlpha": .35},
            "sourceType": "folder", "files": [str(raw)] * 5})
        owner = self
        class YOLO:
            task = "detect"
            names = {0: "carton"}
            def __init__(self, path): self.calls = 0
            def predict(self, **kwargs):
                self.calls += 1
                if self.calls == 2:
                    # First output is already available before the run ends.
                    owner.assertEqual(len(inference.result_assets(folder)), 1)
                    (folder / "cancel.request").touch()
                return [owner.prediction()]
        module = types.ModuleType("ultralytics"); module.YOLO = YOLO
        with patch.dict(sys.modules, {"ultralytics": module}): predict.main(folder)
        result = store.read_json(folder / "result.json", {})
        self.assertEqual(result["processed"], 2)
        self.assertEqual(len(result["assets"]), 2)
        for item in result["assets"]: self.assertTrue((folder / item["path"]).is_file())
        with patch.object(inference, "get_run", return_value={"directory": str(folder), "state": "cancelled"}):
            self.assertEqual(inference.detail("a" * 32)["totalAssets"], 2)
            self.assertTrue(inference.asset("a" * 32, 1).is_file())

    def test_old_stopped_images_recover_without_manifest(self):
        output = self.root / "output"; output.mkdir()
        self.raw("output/000000_saved.jpg")
        with patch.object(inference, "get_run", return_value={"directory": str(self.root), "state": "cancelled"}):
            self.assertEqual(inference.detail("a" * 32)["totalAssets"], 1)
            self.assertTrue(inference.asset("a" * 32, 0).is_file())

    def test_video_stop_finalizes_partial_video(self):
        folder = self.root / "video-stop"; folder.mkdir()
        store.atomic_json(folder / "request.json", {"model": {"path": "best.pt", "mode": "detection", "classes": ["carton"]},
            "config": {"imageSize": 640, "confidence": .9, "iou": .45, "drawBoxes": True, "maskAlpha": .35},
            "sourceType": "video", "source": "input.mp4"})
        owner = self
        class YOLO:
            task = "detect"
            names = {0: "carton"}
            def __init__(self, path): pass
            def predict(self, **kwargs):
                (folder / "cancel.request").touch()
                return [owner.prediction()]
        module = types.ModuleType("ultralytics"); module.YOLO = YOLO
        capture = MagicMock(); capture.isOpened.return_value = True
        capture.get.side_effect = lambda prop: {cv2.CAP_PROP_FPS: 25, cv2.CAP_PROP_FRAME_COUNT: 20,
            cv2.CAP_PROP_FRAME_WIDTH: 128, cv2.CAP_PROP_FRAME_HEIGHT: 96}.get(prop, 0)
        capture.read.return_value = (True, np.zeros((96, 128, 3), np.uint8))
        webm, mp4 = MagicMock(), MagicMock()
        mp4.isOpened.return_value = True
        with patch.dict(sys.modules, {"ultralytics": module}), patch.object(predict.cv2, "VideoCapture", return_value=capture), patch.object(predict, "open_browser_video_writer", return_value=webm), patch.object(predict.cv2, "VideoWriter", return_value=mp4):
            predict.main(folder)
        result = store.read_json(folder / "result.json", {})
        self.assertEqual(result["processed"], 1)
        self.assertEqual(result["assets"][0]["type"], "video")
        self.assertTrue(result["assets"][0]["path"].endswith(".mp4"))
        self.assertTrue(result["assets"][0]["playbackPath"].endswith(".webm"))
        for writer in (webm, mp4):
            writer.write.assert_called_once()
            writer.release.assert_called_once()
        capture.release.assert_called_once()

    def test_worker_requests_graceful_stop_before_termination(self):
        data = {"id": "e" * 32, "modelId": "f" * 32, "directory": str(self.root), "state": "queued"}
        event = threading.Event()
        process = MagicMock(); process.poll.side_effect = [None, 0]
        def launch(*args, **kwargs):
            event.set()
            return process
        def finish(timeout):
            self.assertTrue((self.root / "cancel.request").is_file())
        process.wait.side_effect = finish
        runtime = MagicMock(); runtime.acquire_for_worker.return_value = True
        with patch.object(inference, "runtime", runtime), patch.object(inference, "get_model", return_value={}), patch.object(inference.subprocess, "Popen", side_effect=launch):
            inference.run(data, event)
        process.terminate.assert_not_called()
        self.assertEqual(store.read_json(self.root / "run.json", {})["state"], "cancelled")

    def test_inference_browser_lists_backend_media_without_native_dialog(self):
        self.raw()
        (self.root / "subfolder").mkdir()
        (self.root / "movie.mp4").write_bytes(b"video")
        (self.root / "other.txt").write_text("not media")
        app = FastAPI(); app.include_router(inference_router)
        with TestClient(app) as client:
            folder = client.get("/inference/browse", params={"path": str(self.root), "kind": "folder"})
            self.assertEqual([p["name"] for p in folder.json()["items"]], ["subfolder"])
            media = client.get("/inference/browse", params={"path": str(self.root), "kind": "media"})
            self.assertEqual({p["name"] for p in media.json()["items"]}, {"subfolder", "movie.mp4", "raw.png"})
            self.assertEqual(client.get("/inference/browse", params={"path": str(self.root / "missing")}).status_code, 422)

    def test_permanent_delete_removes_only_owned_dataset_snapshot_and_inference_files(self):
        dataset = self.root / "datasets" / "source"; snapshot = self.root / "snapshots" / "snapshot"
        dataset.mkdir(parents=True); snapshot.mkdir(parents=True)
        dataset_id, snapshot_id = "a" * 32, "b" * 32
        store.atomic_json(dataset / "dataset.json", {"id": dataset_id, "directory": str(dataset), "snapshots": [{"id": snapshot_id, "directory": str(snapshot)}]})
        store.atomic_json(snapshot / "dataset.json", {"id": snapshot_id, "directory": str(snapshot), "pairs": []})
        (self.root / ".datasets").mkdir(); (self.root / ".snapshots").mkdir(); (self.root / ".training" / "excluded").mkdir(parents=True)
        store.atomic_json(self.root / ".datasets" / f"{dataset_id}.json", {"directory": str(dataset)})
        store.atomic_json(self.root / ".snapshots" / f"{snapshot_id}.json", {"directory": str(snapshot)})
        (self.root / ".training" / "excluded" / f"{snapshot_id}.json").write_text("{}")
        with patch.object(store, "WORK_ROOT", self.root):
            store.delete_dataset(dataset_id)
        self.assertFalse(dataset.exists()); self.assertFalse(snapshot.exists())
        self.assertFalse((self.root / ".datasets" / f"{dataset_id}.json").exists())
        self.assertFalse((self.root / ".snapshots" / f"{snapshot_id}.json").exists())

        run = self.root / "inference-run"; output = run / "output"; output.mkdir(parents=True)
        run_id = "c" * 32; image = output / "image.jpg"; image.write_bytes(b"image")
        store.atomic_json(run / "run.json", {"id": run_id, "directory": str(run), "state": "complete"})
        store.atomic_json(run / "result.json", {"processed": 1, "objects": 0, "assets": [{"name": image.name, "path": "output/image.jpg", "type": "image"}]})
        (self.root / ".inference").mkdir(); store.atomic_json(self.root / ".inference" / f"{run_id}.json", {"directory": str(run)})
        with patch.object(inference, "WORK_ROOT", self.root), patch.object(inference, "get_run", return_value={"id": run_id, "directory": str(run), "state": "complete"}):
            inference.delete_asset(run_id, 0)
            self.assertFalse(image.exists())
            inference.delete_run(run_id)
        self.assertFalse(run.exists()); self.assertFalse((self.root / ".inference" / f"{run_id}.json").exists())

    def test_inference_worker_rejects_incomplete_outputs_and_honors_cancel(self):
        for outcome in ("success", "missing", "cancel"):
            target = self.root / outcome; target.mkdir()
            data = {"id":"e"*32, "modelId":"f"*32, "directory":str(target), "state":"queued"}
            store.atomic_json(target / "request.json", {"sourceType":"folder", "files":["raw.png"]})
            event = threading.Event()
            if outcome == "cancel": event.set()
            def launch(*args, **kwargs):
                self.assertNotIn("shell", kwargs)
                (target / "output").mkdir()
                if outcome == "success": (target / "output/1.jpg").write_bytes(b"output")
                store.atomic_json(target / "result.json", {"processed":1, "objects":1, "assets":[{"path":"output/1.jpg"}]})
                process = MagicMock(); process.poll.return_value = 0; process.returncode = 0
                return process
            runtime = MagicMock(); runtime.acquire_for_worker.return_value = True
            with patch.object(inference, "runtime", runtime), patch.object(inference, "get_model", return_value={}), patch.object(inference.subprocess, "Popen", side_effect=launch):
                inference.run(data, event)
            self.assertEqual(store.read_json(target / "run.json", {})["state"], {"success":"complete","missing":"failed","cancel":"cancelled"}[outcome])
            runtime.acquire_for_worker.assert_not_called()
            runtime.release_cached_models.assert_not_called()
            runtime.inference_lock.release.assert_not_called()

    def test_video_decoder_failure_is_not_marked_complete_and_releases_resources(self):
        job = self.root / "job"; job.mkdir()
        store.atomic_json(job / "job.json", {"projectDirectory":str(self.root / "outputs")})
        store.atomic_json(job / "request.json", {"source_video":str(self.root / "missing.mp4"), "annotation_model":"sam2"})
        capture = MagicMock(); capture.isOpened.return_value = True
        capture.get.side_effect = lambda prop: {cv2.CAP_PROP_FPS:25, cv2.CAP_PROP_FRAME_COUNT:5, cv2.CAP_PROP_FRAME_WIDTH:128, cv2.CAP_PROP_FRAME_HEIGHT:96}.get(prop, 0)
        capture.read.return_value = (False, None)
        writer = MagicMock()
        with patch.object(annotation.runtime, "annotation_model", return_value=object()), patch.object(annotation.cv2, "VideoCapture", return_value=capture), patch.object(annotation, "open_browser_video_writer", return_value=writer):
            annotation._run_job(job, threading.Event())
        self.assertEqual(store.read_json(job / "progress.json", {})["state"], "failed")
        self.assertIn("0/5", store.read_json(job / "progress.json", {})["error"])
        self.assertTrue(capture.release.called); self.assertTrue(writer.release.called)


if __name__ == "__main__": unittest.main()
