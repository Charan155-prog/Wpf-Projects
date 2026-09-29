"""Persistent cancellable background review; originals are never mutated."""
import copy
import os
import threading
import uuid
from pathlib import Path

from fastapi import HTTPException
from services import dataset_store as store
from services.workflow_guard import guarded_start

THREADS = {}
CANCEL = {}


def location(data, report_id):
    import re
    if not re.fullmatch(r"[a-f0-9]{32}", report_id):
        raise HTTPException(404, "Automatic review not found.")
    return store.contained(Path(data["directory"]), f".automatic/{report_id}")


def status(dataset_id, page=1, filter_status="all"):
    with store.LOCK:
        data = store.get_dataset(dataset_id)
        pointer = store.read_json(Path(data["directory"]) / ".automatic/latest.json", {})
        if not pointer:
            return {"report": None, "items": [], "total": 0}
        root = location(data, pointer["id"])
        report = store.read_json(root / "status.json", {})
        if report.get("state") == "running" and not THREADS.get(report["id"], threading.Thread()).is_alive():
            report["state"] = "interrupted"
            store.atomic_json(root / "status.json", report)
        paths = sorted((root / "items").glob(f"{'*' if filter_status == 'all' else filter_status}/*.json"))
        page = max(1, page)
        results = [store.read_json(path, {}) for path in paths[(page-1)*24:page*24]]
        index = {item["id"]: item for item in data["items"]}
        entries = [{**value, "image": store.public_item(data, index[value["itemId"]])} for value in results if value.get("itemId") in index]
        return {"report": {**report, "stale": data["revision"] != report["revision"]}, "items": entries, "total": len(paths)}


@guarded_start("validation")
def start(dataset_id, revision):
    from services.annotation_jobs import active_job
    if not os.getenv("SAIL_VLM_MODEL"):
        raise HTTPException(503, "Configure the local Qwen SAIL_VLM_MODEL on the backend before Automatic validation.")
    with store.LOCK:
        data = store.get_dataset(dataset_id)
        store.require_editable(data, revision)
        if data["state"] != "complete" or not data["classes"]:
            raise HTTPException(409, "A completed dataset with its class mapping is required.")
        items = [item for item in data["items"] if item["status"] == "Pass" and not item.get("deleted")]
        if not items:
            raise HTTPException(422, "No included Pass images to review.")
        report_id = uuid.uuid4().hex
        root = location(data, report_id)
        (root / "items").mkdir(parents=True)
        report = {"id": report_id, "revision": revision, "state": "running", "total": len(items), "completed": 0,
                  "candidate": 0, "review": 0, "createdAt": store.now(), "method": "Qwen VLM + geometry v1",
                  "model": os.getenv("SAIL_VLM_MODEL"), "rubricVersion": 1, "minimumRating": 4}
        store.atomic_json(root / "status.json", report)
        store.atomic_json(root.parent / "latest.json", {"id": report_id})
        event = threading.Event(); CANCEL[report_id] = event
        thread = threading.Thread(target=run, args=(copy.deepcopy(data), items, root, report, event), daemon=True)
        THREADS[report_id] = thread; thread.start()
        return report


def run(data, items, root, report, event):
    try:
        from ml.automatic_review import review_image
        from services.model_runtime import runtime
        # Fail once on missing/unloadable models instead of retrying for 10,000 images.
        with runtime.inference_lock:
            runtime.vlm()
        for item in items:
            if event.is_set():
                report["state"] = "cancelled"; break
            with store.LOCK:
                current = store.get_dataset(data["id"])
                if current["revision"] != data["revision"] or current.get("archived"):
                    report["state"] = "stale"; break
            try:
                result = review_image(data, item)
            except Exception as error:
                result = {"status": "review", "reason": f"Check failed; manual review required: {str(error)[:500]}", "rating": None}
            store.atomic_json(root / "items" / result["status"] / f"{item['id']}.json", {**result, "itemId": item["id"], "filename": item["filename"]})
            report["completed"] += 1; report[result["status"]] += 1
            store.atomic_json(root / "status.json", report)
        else:
            report["state"] = "complete"
    except Exception as error:
        report.update(state="failed", error=str(error)[:500])
    finally:
        report["finishedAt"] = store.now()
        store.atomic_json(root / "status.json", report)


def cancel(dataset_id):
    report = status(dataset_id)["report"]
    if report and report["id"] in CANCEL:
        CANCEL[report["id"]].set()
    return {"cancelRequested": True}


def export(dataset_id, revision, report_id):
    with store.LOCK:
        data = store.get_dataset(dataset_id)
        store.require_editable(data, revision)
        root = location(data, report_id)
        report = store.read_json(root / "status.json", {})
        if report.get("state") != "complete" or report.get("revision") != revision:
            raise HTTPException(409, "Finish a fresh automatic review before exporting candidates.")
        accepted = set()
        for item in data["items"]:
            result = store.read_json(root / "items/candidate" / f"{item['id']}.json", {})
            if result.get("status") != "candidate" or item.get("deleted") or item["status"] != "Pass":
                continue
            if result.get("referenceId"):
                from services.ground_truth import verify_result_reference
                verify_result_reference(data, result)
            elif result.get("rawHash"):
                from services.ground_truth import lookup
                if lookup(data, result["rawHash"]):
                    raise HTTPException(409, "A ground-truth reference was added after model review. Run automatic checks again.")
            for field, hash_key in (("raw", "rawHash"), ("label", "labelHash")):
                path = store.contained(Path(data["directory"]), item[field])
                if not path.is_file() or store.file_hash(path) != result.get(hash_key):
                    raise HTTPException(409, "An image or label changed since automatic review. Run the checks again.")
            accepted.add(item["id"])
        if not accepted:
            raise HTTPException(422, "No automatic candidates are available. Review flagged images manually.")
        return store.validate_dataset(dataset_id, revision, accepted, report_id)
