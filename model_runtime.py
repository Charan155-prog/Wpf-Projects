"""Long-lived, thread-safe SAM3 and Qwen model cache for the API process."""
from __future__ import annotations

import threading

from core import logger


class ModelRuntime:
    def __init__(self) -> None:
        self.inference_lock = threading.RLock()
        self._sam3 = None
        self._sam2 = None
        self._vlm = None
        self._sam3_lock = threading.RLock()
        self._vlm_lock = threading.RLock()
        self._sam3_error: str | None = None
        self._vlm_error: str | None = None

    def sam3(self):
        with self.inference_lock, self._sam3_lock:
            if self._sam3 is None:
                from ml.run_annotation import load_sam3
                logger.info("loading SAM3 into the persistent API runtime")
                self._sam3 = load_sam3()
                self._sam3_error = None
                logger.info("SAM3 is warm and ready")
            return self._sam3

    def vlm(self):
        with self.inference_lock, self._vlm_lock:
            if self._vlm is None:
                from ml.run_auto_prompt import load_vlm
                logger.info("loading Qwen VLM into the persistent API runtime")
                self._vlm = load_vlm()
                self._vlm_error = None
                logger.info("Qwen VLM is warm and ready")
            return self._vlm

    def sam2(self):
        with self.inference_lock:
            if self._sam2 is None:
                from ml.sam2_backend import Sam2Backend
                self._sam2 = Sam2Backend()
            return self._sam2

    def annotation_model(self, name):
        if name == "sam2": return self.sam2()
        if name == "sam3": return self.sam3()
        raise ValueError("Choose SAM2 or SAM3.")

    def warm_sam3_async(self) -> None:
        def warm() -> None:
            try:
                self.sam3()
            except Exception as error:
                self._sam3_error = str(error)
                logger.exception("SAM3 warm-up failed")
        threading.Thread(target=warm, name="sail-sam3-warmup", daemon=True).start()

    def warm_vlm_async(self) -> None:
        def warm() -> None:
            try:
                self.vlm()
            except Exception as error:
                self._vlm_error = str(error)
                logger.exception("VLM warm-up failed")
        threading.Thread(target=warm, name="sail-vlm-warmup", daemon=True).start()

    def release_cached_models(self):
        """Caller owns inference_lock; prevent training competing with cached GPU models."""
        import gc
        with self._sam3_lock, self._vlm_lock:
            self._sam3 = None
            self._sam2 = None
            self._vlm = None
            gc.collect()
            try:
                import torch
                if torch.cuda.is_available(): torch.cuda.empty_cache()
            except ImportError:
                pass

    def acquire_for_worker(self, cancel):
        """Wait for batch holders to release their SAM references before clearing VRAM."""
        from services.annotation_jobs import active_job
        from services.automatic_validation import THREADS
        while not cancel.is_set():
            if not self.inference_lock.acquire(timeout=.25):
                continue
            if not active_job() and not any(t.is_alive() for t in THREADS.values()):
                return True
            self.inference_lock.release()
            cancel.wait(.25)
        return False

    def state(self) -> dict:
        return {
            "sam2": "ready" if self._sam2 is not None else "not loaded",
            "sam3": "ready" if self._sam3 is not None else ("error" if self._sam3_error else "warming"),
            "vlm": "ready" if self._vlm is not None else ("error" if self._vlm_error else "warming"),
        }


runtime = ModelRuntime()
