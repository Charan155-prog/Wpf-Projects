"""CPU regression tests. All data is temporary; no user dataset/model is changed.

Run: .conda/sail/python.exe -m unittest discover -s backend -p test_dataset_validation.py -v
"""
import contextlib
import logging
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch, MagicMock

BOOT = tempfile.TemporaryDirectory(prefix="sail-test-bootstrap-")
os.environ["SAIL_WORK_ROOT"] = BOOT.name
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient
from PIL import Image
import core
from services import dataset_store as store, dataset_validation as validation, annotation_jobs as jobs, source_catalog as sources
from routers.datasets import router


def tearDownModule():
    logging.shutdown()
    BOOT.cleanup()


class DatasetTests(unittest.TestCase):
    def test_project_delete_rejects_live_worker_before_deleting_files(self):
        from routers import catalog
        data = self.make_dataset()
        projects_file = self.root / "projects.json"
        core.write_json(projects_file, [{"id": "one", "name": "Inspection"}])
        worker = MagicMock(); worker.is_alive.return_value = True
        with patch.object(catalog, "PROJECTS_FILE", projects_file), patch.dict(jobs.THREADS, {"busy": worker}):
            with self.assertRaises(HTTPException) as error:
                catalog.delete_project("one")
        self.assertEqual(error.exception.status_code, 409)
        self.assertTrue(Path(data["directory"]).is_dir())
        self.assertEqual(len(core.read_json(projects_file, [])), 1)

    def test_project_delete_removes_owned_outputs_but_preserves_input_and_other_project(self):
        from routers import catalog
        from services import training_jobs as training, inference_jobs as inference
        data = self.make_dataset()
        self.add_image(data)
        store.finish_dataset(data["id"], "complete")
        snapshot = store.validate_dataset(data["id"], 1)
        original = self.root / "input/phones"
        original.mkdir(parents=True)
        (original / "keep.txt").write_text("original")
        other = store.create_dataset("Other", "detection", {"name": "other", "kind": "folder", "path": str(self.root / "other-input")}, ["Phone"])
        projects_file = self.root / "projects.json"
        core.write_json(projects_file, [{"id": "one", "name": "Inspection"}, {"id": "two", "name": "Other"}])
        runs = []
        for kind, registry, ident in (("Trained Models", ".training", "a" * 32), ("Inference Outputs", ".inference", "b" * 32)):
            directory = Path(data["sourceDirectory"]) / kind / ident
            store.atomic_json(directory / "run.json", {"id": ident, "directory": str(directory), "projectName": "Inspection", "createdAt": "2026-01-01", "state": "failed"})
            store.atomic_json(self.root / registry / f"{ident}.json", {"directory": str(directory)})
            runs.append(directory)
        with patch.object(catalog, "PROJECTS_FILE", projects_file), patch.object(catalog, "WORK_ROOT", self.root), patch.object(training, "WORK_ROOT", self.root), patch.object(training, "SETTINGS_FILE", self.root / "settings.json"), patch.object(inference, "WORK_ROOT", self.root):
            result = catalog.delete_project("one")
        self.assertEqual([p["id"] for p in result["projects"]], ["two"])
        for directory in [Path(data["directory"]), Path(snapshot["directory"]), *runs]:
            self.assertFalse(directory.exists())
        self.assertTrue((original / "keep.txt").is_file())
        self.assertTrue(Path(other["directory"]).is_dir())

    def test_atomic_json_retries_windows_lock_without_losing_previous_data(self):
        target = self.root / "coverage.json"
        store.atomic_json(target, {"attempted": 104})
        replace = Path.replace
        attempts = []

        def locked_then_replace(source, destination):
            attempts.append(source)
            self.assertEqual(core.read_json(target, {}), {"attempted": 104})
            if len(attempts) < 4:
                raise PermissionError(13, "Access is denied")
            return replace(source, destination)

        with patch.object(Path, "replace", locked_then_replace), patch.object(store.time, "sleep") as sleep:
            store.atomic_json(target, {"attempted": 105})
        self.assertEqual(sleep.call_count, 3)
        self.assertEqual(core.read_json(target, {}), {"attempted": 105})
        self.assertEqual(list(self.root.glob("*.tmp")), [])

    def test_atomic_json_permanent_lock_preserves_data_and_reports_failure(self):
        target = self.root / "coverage.json"
        store.atomic_json(target, {"attempted": 104})
        with patch.object(Path, "replace", side_effect=PermissionError("locked")) as replace, patch.object(store.time, "sleep"):
            with self.assertRaises(PermissionError):
                store.atomic_json(target, {"attempted": 105})
        self.assertEqual(replace.call_count, 9)
        self.assertEqual(core.read_json(target, {}), {"attempted": 104})
        self.assertEqual(list(self.root.glob("*.tmp")), [])

    def test_atomic_json_does_not_retry_unrelated_io_errors(self):
        target = self.root / "coverage.json"
        with patch.object(Path, "replace", side_effect=OSError("disk failure")), patch.object(store.time, "sleep") as sleep:
            with self.assertRaises(OSError):
                store.atomic_json(target, {"attempted": 105})
        sleep.assert_not_called()
        self.assertEqual(list(self.root.glob("*.tmp")), [])

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sail-validation-test-")
        self.root = Path(self.temp.name).resolve()
        self.patches = [patch.object(core, "WORK_ROOT", self.root), patch.object(core, "SETTINGS_FILE", self.root / "settings.json"),
                        patch.object(store, "WORK_ROOT", self.root), patch.object(store, "SETTINGS_FILE", self.root / "settings.json"),
                        patch.object(jobs, "WORK_ROOT", self.root)]
        for value in self.patches: value.start()
        app = FastAPI(); app.include_router(router)
        self.client = TestClient(app)

    def tearDown(self):
        self.client.close()
        for value in reversed(self.patches): value.stop()
        self.temp.cleanup()

    def make_dataset(self, mode="detection", source="input/phones", kind="folder"):
        path = self.root / source
        return store.create_dataset("Inspection", mode, {"name": path.name, "path": str(path), "kind": kind}, ["Phone", "Laptop"])

    def add_image(self, data, name="image.png", passed=True):
        root = Path(data["directory"]); status = "Pass" if passed else "Fail"
        raw = root / "Raw" / name; raw.parent.mkdir(parents=True, exist_ok=True)
        Image.new("RGB", (100, 80), (20, 30, 40)).save(raw)
        annotated = root / "Annotated" / status / f"{raw.stem}_annotated.jpg"
        annotated.parent.mkdir(parents=True, exist_ok=True)
        Image.new("RGB", (100, 80), (200, 30, 40)).save(annotated)
        label = root / "Labels" / f"{raw.stem}.txt"; label.parent.mkdir(parents=True, exist_ok=True)
        content = "0 0.5 0.5 0.4 0.4" if data["mode"] == "detection" else "0 0.2 0.2 0.7 0.2 0.7 0.7 0.2 0.7"
        label.write_text(content if passed else "", encoding="utf-8")
        store.record_item(data["id"], {"filename": name, "annotated": str(annotated), "label": str(label), "detections": int(passed)})
        return store.get_dataset(data["id"])["items"][-1]

    def test_distinct_sources_revisions_and_modes_never_overwrite(self):
        first = self.make_dataset(); item = self.add_image(first)
        old_hash = store.file_hash(Path(first["directory"]) / item["raw"])
        store.finish_dataset(first["id"], "complete")
        same = self.make_dataset(); other = self.make_dataset(source="different/phones"); segmentation = self.make_dataset(mode="segmentation")
        self.assertEqual(same["runNumber"], 2)
        self.assertEqual(first["sourceKey"], same["sourceKey"])
        self.assertNotEqual(first["sourceKey"], other["sourceKey"])
        self.assertEqual(len({value["directory"] for value in (first, same, other, segmentation)}), 3)
        archived = store.get_dataset(first["id"])
        self.assertTrue(archived["archived"])
        self.assertEqual(store.file_hash(Path(archived["directory"]) / item["raw"]), old_hash)
        self.assertEqual(Path(same["directory"]), self.root / "datasets/Inspection/Detection/phones/Annotation")
        self.assertEqual(len(store.list_datasets()), 3)

    def test_pass_only_pairs_delete_restore_and_immutable_revalidation(self):
        data = self.make_dataset(); keep = self.add_image(data, "keep.png"); removed = self.add_image(data, "removed.png"); self.add_image(data, "fail.png", False)
        store.finish_dataset(data["id"], "complete")
        store.set_deleted(data["id"], removed["id"], True, 3)
        snapshot = store.validate_dataset(data["id"], 4); root = Path(snapshot["directory"])
        self.assertEqual(snapshot["imageCount"], 1)
        self.assertEqual(store.file_hash(root / "images/keep.png"), store.file_hash(Path(data["directory"]) / keep["raw"]))
        self.assertFalse((root / "images/removed.png").exists()); self.assertFalse((root / "images/fail.png").exists())
        self.assertEqual(store.validate_dataset(data["id"], 4)["id"], snapshot["id"])
        store.set_deleted(data["id"], removed["id"], False, 4)
        second = store.validate_dataset(data["id"], 5)
        self.assertEqual(second["imageCount"], 2); self.assertNotEqual(snapshot["id"], second["id"])
        self.assertEqual(len(list((root / "images").iterdir())), 1)

    def test_requested_source_layout_discovery_and_archive_snapshots(self):
        data = self.make_dataset(source="input/Dailies")
        item = self.add_image(data)
        store.finish_dataset(data["id"], "complete")
        snapshot = store.validate_dataset(data["id"], 1)
        other = self.make_dataset(source="input/Cortons")
        self.add_image(other)
        checksum = store.file_hash(Path(other["directory"]) / "Raw/image.png")
        repeated = self.make_dataset(source="input/Dailies")
        self.assertEqual(Path(repeated["directory"]).parts[-4:], ("Inspection", "Detection", "Dailies", "Annotation"))
        self.assertEqual(store.file_hash(Path(other["directory"]) / "Raw/image.png"), checksum)
        self.assertTrue(store.check_snapshot(snapshot["id"])["verified"])
        with self.assertRaises(HTTPException):
            store.set_deleted(data["id"], item["id"], True, 1)
        for pointer in store.registry().glob("*.json"):
            pointer.unlink()  # only ephemeral test fixtures
        response = self.client.get("/datasets")
        self.assertEqual(response.status_code, 200)
        self.assertEqual({entry["name"] for entry in response.json()["datasets"]}, {"Dailies", "Cortons"})
        self.assertTrue(store.check_snapshot(snapshot["id"])["verified"])
        with self.assertRaises(HTTPException):
            self.make_dataset(source="input/Dailies")

    def test_discover_plain_source_folder_without_registry(self):
        data = self.make_dataset(source="input/Cortons"); self.add_image(data)
        root = Path(data["directory"])
        (root / "dataset.json").unlink()
        (store.registry() / f"{data['id']}.json").unlink()
        listed = self.client.get("/datasets").json()["datasets"]
        self.assertEqual(len(listed), 1)
        self.assertEqual(listed[0]["name"], "Cortons")
        self.assertEqual(listed[0]["passCount"], 1)
        detail = self.client.get(f"/datasets/{listed[0]['id']}").json()
        self.assertEqual(self.client.get(detail["items"][0]["annotatedUrl"]).status_code, 200)
        store.validate_dataset(listed[0]["id"], 0)

    def test_automatic_candidates_only_export_and_stale_guards(self):
        from services import automatic_validation as automatic
        from services.model_runtime import runtime
        data = self.make_dataset(); good = self.add_image(data, "good.png"); bad = self.add_image(data, "bad.png")
        store.finish_dataset(data["id"], "complete")
        data = store.get_dataset(data["id"])
        report_id = "b" * 32; root = automatic.location(data, report_id)
        report = {"id": report_id, "revision": 2, "state": "running", "total": 2, "completed": 0, "candidate": 0, "review": 0}
        store.atomic_json(root.parent / "latest.json", {"id": report_id})
        def review(current, item):
            directory = Path(current["directory"])
            return {"status": "candidate" if item["id"] == good["id"] else "review", "reason": "test judgment", "rating": 4,
                    "rawHash": store.file_hash(directory / item["raw"]), "labelHash": store.file_hash(directory / item["label"])}
        with patch("ml.automatic_review.review_image", side_effect=review), patch.object(runtime, "vlm", return_value=(None, None)):
            automatic.run(data, data["items"], root, report, threading.Event())
        response = self.client.get(f"/datasets/{data['id']}/automatic?status=candidate").json()
        self.assertEqual(response["total"], 1); self.assertEqual(response["report"]["state"], "complete")
        snapshot = automatic.export(data["id"], 2, report_id)
        self.assertEqual(snapshot["imageCount"], 1); self.assertEqual(snapshot["validationMethod"], "automatic")
        self.assertFalse((Path(snapshot["directory"]) / "images/bad.png").exists())
        manual = store.validate_dataset(data["id"], 2)
        self.assertEqual(manual["imageCount"], 2); self.assertNotEqual(manual["id"], snapshot["id"])
        (Path(data["directory"]) / good["label"]).write_text("0 0.5 0.5 0.3 0.3")
        with self.assertRaises(HTTPException): automatic.export(data["id"], 2, report_id)
        store.set_deleted(data["id"], bad["id"], True, 2)
        with self.assertRaises(HTTPException): automatic.export(data["id"], 3, report_id)

    def test_automatic_strict_judge_schema_geometry_and_cancel(self):
        from ml.automatic_review import parse_verdict, review_image
        from services import automatic_validation as automatic
        from services.model_runtime import runtime
        import json
        verdict = dict(correct_objects=True, complete_objects=True, tight_boundaries=True, no_extra_objects=True, rating=4, reason="clear")
        self.assertEqual(parse_verdict(json.dumps(verdict))["status"], "candidate")
        verdict["tight_boundaries"] = False
        self.assertEqual(parse_verdict(json.dumps(verdict))["status"], "review")
        verdict["correct_objects"] = "true"
        with self.assertRaises(ValueError): parse_verdict(json.dumps(verdict))
        for mode in ("detection", "segmentation"):
            data = self.make_dataset(mode=mode); item = self.add_image(data)
            with patch("ml.automatic_review.judge", return_value={"status": "review", "reason": "wrong object"}) as judge:
                self.assertEqual(review_image(data, item)["status"], "review"); judge.assert_called_once()
        data = store.get_dataset(data["id"])
        root = automatic.location(data, "c"*32); event = threading.Event(); event.set()
        report = {"id": "c"*32, "state": "running", "completed": 0}
        with patch.object(runtime, "vlm", return_value=(None, None)):
            automatic.run(data, data["items"], root, report, event)
        self.assertEqual(report["state"], "cancelled"); self.assertEqual(report["completed"], 0)

    def test_automatic_model_errors_fail_closed(self):
        from services import automatic_validation as automatic
        from services.model_runtime import runtime
        data = self.make_dataset(); self.add_image(data); store.finish_dataset(data["id"], "complete")
        data = store.get_dataset(data["id"])
        report_id = "d"*32; root = automatic.location(data, report_id)
        report = {"id": report_id, "revision": 1, "state": "running", "completed": 0, "candidate": 0, "review": 0}
        with patch.object(runtime, "vlm", return_value=(None, None)), patch("ml.automatic_review.review_image", side_effect=RuntimeError("bad model output")):
            automatic.run(data, data["items"], root, report, threading.Event())
        self.assertEqual(report["candidate"], 0); self.assertEqual(report["review"], 1)
        with self.assertRaises(HTTPException): automatic.export(data["id"], 1, report_id)
        report["state"] = "running"
        with patch.object(runtime, "vlm", side_effect=RuntimeError("CUDA unavailable")):
            automatic.run(data, data["items"], root, report, threading.Event())
        self.assertEqual(report["state"], "failed")

    def test_bad_labels_missing_pairs_and_stale_revision(self):
        data = self.make_dataset(mode="segmentation"); item = self.add_image(data); store.finish_dataset(data["id"], "complete")
        label = Path(data["directory"]) / item["label"]
        for content in ("0 0.5 0.5 0.4 0.4", "9 0 0 1 0 1 1", "0 nan 0 1 0 1 1", ""):
            label.write_text(content)
            with self.assertRaises(HTTPException) as caught: store.validate_dataset(data["id"], 1)
            self.assertEqual(caught.exception.status_code, 422)
        label.unlink()
        with self.assertRaises(HTTPException): store.validate_dataset(data["id"], 1)
        with self.assertRaises(HTTPException) as caught: store.set_deleted(data["id"], item["id"], True, 0)
        self.assertEqual(caught.exception.status_code, 409)

    def test_empty_or_incomplete_validation_rejected(self):
        data = self.make_dataset(); self.add_image(data, passed=False)
        with self.assertRaises(HTTPException): store.validate_dataset(data["id"], 1)
        store.finish_dataset(data["id"], "complete")
        with self.assertRaises(HTTPException): store.validate_dataset(data["id"], 1)

    def test_custom_storage_and_checksum_verification(self):
        core.write_json(self.root / "settings.json", {"datasetRoot": str(self.root / "custom"), "validatedRoot": str(self.root / "approved")})
        data = self.make_dataset(); self.add_image(data); store.finish_dataset(data["id"], "complete")
        snapshot = store.validate_dataset(data["id"], 1)
        self.assertTrue(Path(data["directory"]).is_relative_to(self.root / "custom"))
        self.assertEqual(Path(snapshot["directory"]).parent, Path(data["directory"]).parent / "Validated Datasets")
        core.write_json(self.root / "settings.json", {"datasetRoot": str(self.root / "new-location")})
        self.assertEqual(store.get_dataset(data["id"])["directory"], data["directory"])
        self.assertTrue(store.check_snapshot(snapshot["id"])["verified"])
        (Path(snapshot["directory"]) / "labels/image.txt").write_text("tampered")
        with self.assertRaises(HTTPException) as caught: store.check_snapshot(snapshot["id"])
        self.assertEqual(caught.exception.status_code, 409)

    def test_api_pagination_assets_validation_training(self):
        data = self.make_dataset(); item = self.add_image(data); store.finish_dataset(data["id"], "complete")
        base = f"/datasets/{data['id']}"; detail = self.client.get(base).json()
        self.assertEqual(detail["total"], 1)
        self.assertEqual(self.client.get(detail["items"][0]["annotatedUrl"]).status_code, 200)
        self.assertEqual(self.client.get(base + "?page=2").json()["items"], [])
        self.assertEqual(self.client.patch(base + f"/images/{item['id']}", json={"revision": 0}).status_code, 409)
        response = self.client.post(base + "/validate", json={"revision": 1}); self.assertEqual(response.status_code, 200, response.text)
        snapshot = response.json()["snapshot"]
        self.assertEqual(self.client.get("/training/datasets").json()["datasets"][0]["id"], snapshot["id"])
        self.assertEqual(self.client.post(f"/training/datasets/{snapshot['id']}/verify").status_code, 200)
        self.assertEqual(self.client.get(f"/training/datasets/{snapshot['id']}/images/0").status_code, 200)
        response = self.client.post(base + f"/images/{item['id']}/preview", json={"revision": 1, "regions": [{"classId": 0, "points": [[-1, 0], [1, 0], [1, 1]]}]})
        self.assertEqual(response.status_code, 422)

    def test_path_escape_and_name_collisions(self):
        with self.assertRaises(HTTPException): store.contained(self.root, "../outside.txt")
        self.assertEqual(self.client.get("/datasets/invalid").status_code, 404)
        paths = [self.root / "one/a.jpg", self.root / "two/a.jpg", self.root / "one/a.png", self.root / "unique.jpg"]
        names = sources.output_names_for(paths)
        self.assertEqual(len({Path(value).stem for value in names.values()}), 4)
        self.assertEqual(names[str(paths[-1])], "unique.jpg")

    def test_job_source_identity_before_subsetting(self):
        folder = self.root / "camera-input"; folder.mkdir()
        for name in ("a.png", "b.png"): Image.new("RGB", (2, 2)).save(folder / name)
        opened = sources.open_folder(str(folder))
        with patch.object(jobs, "LOCAL_SAM3_ENABLED", True), patch.object(jobs, "active_job", return_value=None), patch.object(jobs, "_start_job", side_effect=lambda directory, prompts: directory):
            job = jobs.create_job_from_source("Inspection", "detection", [[0, 0], [1, 0], [1, 1]], [], ["Phone"], opened["token"], roi_source_index=1)
        request = core.read_json(job / "request.json", {}); metadata = core.read_json(job / "job.json", {})
        self.assertEqual(request["roi_source_file"], str(folder / "b.png"))
        self.assertIn("camera-input", metadata["projectDirectory"]); self.assertIsNotNone(metadata["datasetId"])

    def test_legacy_import_idempotent_non_destructive(self):
        data = self.make_dataset(); item = self.add_image(data); root = Path(data["directory"])
        (store.registry() / f"{data['id']}.json").unlink(); (root / "dataset.json").unlink()
        job = self.root / ".jobs/legacy"
        core.write_json(job / "job.json", {"projectDirectory": str(root), "mode": "detection", "prompts": ["Phone", "Laptop"]})
        core.write_json(job / "request.json", {"source_files": [str(self.root / "legacy-camera/image.png")]})
        core.write_json(job / "progress.json", {"state": "complete"})
        checksum = store.file_hash(root / item["raw"])
        store.register_existing_outputs(); store.register_existing_outputs()
        self.assertEqual(len(store.list_datasets()), 1); self.assertEqual(store.list_datasets()[0]["name"], "legacy-camera")
        self.assertEqual(store.file_hash(root / item["raw"]), checksum)

    def test_roi_correction_classes_and_snapshot_pair_names(self):
        import torch
        import numpy as np
        from ml import run_validation
        for mode in ("detection", "segmentation"):
            data = self.make_dataset(mode=mode); item = self.add_image(data); store.finish_dataset(data["id"], "complete")
            processor = MagicMock(); processor.device = "cpu"; processor.set_image.return_value = {}
            masks = np.zeros((2, 1, 80, 100), dtype=bool)
            masks[0, :, :, :] = True  # unwanted laptop occupying entire frame
            masks[1, :, 20:50, 20:60] = True  # desired phone fitting ROI
            processor.set_text_prompt.return_value = {"masks": torch.from_numpy(masks), "scores": torch.tensor([0.99, 0.8])}
            regions = [{"classId": 1, "points": [[0.18, 0.2], [0.62, 0.2], [0.62, 0.65], [0.18, 0.65]]}]
            with patch.object(run_validation.runtime, "sam3", return_value=processor), patch.object(run_validation.torch, "autocast", return_value=contextlib.nullcontext()), patch.object(run_validation.torch.cuda, "get_device_properties", return_value=type("Device", (), {"major": 8})()):
                preview = validation.preview_correction(data["id"], item["id"], regions, 1)
            validation.commit_correction(data["id"], item["id"], preview["previewId"], 1)
            updated = store.get_dataset(data["id"]); corrected = updated["items"][0]; label = Path(updated["directory"]) / corrected["label"]
            self.assertEqual(label.read_text().split()[0], "1"); store.validate_labels(label, mode, 2)
            self.assertTrue((Path(data["directory"]) / item["annotated"]).is_file())
            snapshot = store.validate_dataset(data["id"], updated["revision"])
            self.assertTrue((Path(snapshot["directory"]) / "labels/image.txt").is_file())
            with self.assertRaises(HTTPException): validation.commit_correction(data["id"], item["id"], preview["previewId"], updated["revision"])

    def test_direct_video_publishes_frame_dataset_and_keeps_video(self):
        import numpy as np
        from ml import run_annotation
        video = self.root / "source-video.mp4"; video.write_bytes(b"test-video")
        data = self.make_dataset(source="source-video.mp4", kind="video")
        job = self.root / ".jobs/video-run"
        core.write_json(job / "job.json", {"projectDirectory": data["directory"], "datasetId": data["id"]})
        core.write_json(job / "request.json", {"source_video": str(video), "mode": "detection", "prompts": ["Phone"]})
        capture = MagicMock(); capture.isOpened.return_value = True
        capture.get.side_effect = lambda key: {jobs.cv2.CAP_PROP_FPS: 25, jobs.cv2.CAP_PROP_FRAME_COUNT: 2, jobs.cv2.CAP_PROP_FRAME_WIDTH: 100, jobs.cv2.CAP_PROP_FRAME_HEIGHT: 80}.get(key, 0)
        capture.read.side_effect = [(True, np.zeros((80, 100, 3), dtype=np.uint8)), (True, np.zeros((80, 100, 3), dtype=np.uint8)), (False, None)]
        def writer(target, *_): target.write_bytes(b"annotated-video"); return MagicMock()
        def annotate(processor, path, request, output, **kwargs):
            annotated = output / f"{path.stem}_annotated.jpg"; label = output / f"{path.stem}.txt"
            Image.new("RGB", (100, 80)).save(annotated); label.write_text("0 0.5 0.5 0.4 0.4")
            return {"filename": path.name, "annotated": str(annotated.relative_to(job)), "label": str(label.relative_to(job)), "detections": 1}
        with patch.object(jobs.cv2, "VideoCapture", return_value=capture), patch.object(jobs, "open_browser_video_writer", side_effect=writer), patch.object(jobs.runtime, "sam3", return_value=object()), patch.object(run_annotation, "annotate_image", side_effect=annotate):
            jobs._run_job(job, threading.Event())
        completed = store.get_dataset(data["id"])
        self.assertEqual(completed["state"], "complete", core.read_json(job / "progress.json", {}))
        self.assertEqual(len(completed["items"]), 2)
        self.assertTrue((Path(data["directory"]) / "raw/source-video.mp4").is_file())
        snapshot = store.validate_dataset(data["id"], completed["revision"])
        self.assertEqual(snapshot["imageCount"], 2)
        self.assertFalse((Path(snapshot["directory"]) / "images/source-video.mp4").exists())


if __name__ == "__main__": unittest.main()
