from __future__ import annotations

import asyncio
import json
import shutil
import uuid
from pathlib import Path

from fastapi import (
    APIRouter,
    File,
    Form,
    HTTPException,
    UploadFile,
    WebSocket,
    WebSocketDisconnect,
)
from fastapi.responses import FileResponse

from core import (
    VLM_MODEL,
    WORK_ROOT,
    load_prompt_library,
    logger,
    safe_name,
    save_prompt_library,
)

from services.annotation_jobs import (
    cancel,
    create_job,
    create_job_from_source,
    create_job_from_video,
    preview_image_path,
    preview_source_image,
    status,
    display_connection,
    acknowledge_display,
)

from services.model_runtime import runtime

from services.source_catalog import (
    open_folder,
    open_video,
    open_direct_video,
    direct_video_response,
    source_image,
    source_path,
)


router = APIRouter(tags=["annotation"])


def run_vlm(job_dir, payload: dict) -> None:
    try:
        from ml.run_auto_prompt import generate_prompt

        (
            job_dir / "request.json"
        ).write_text(
            json.dumps(payload),
            encoding="utf-8",
        )

        with runtime.inference_lock:
            model, processor = runtime.vlm()
            prompt = generate_prompt(model, processor, Path(payload["image"]), payload.get("roi", []), job_dir)
            del model, processor

        (
            job_dir / "result.json"
        ).write_text(
            json.dumps({"prompt": prompt}),
            encoding="utf-8",
        )

        (
            job_dir / "runner.log"
        ).write_text(
            "Generated using the persistent Qwen runtime.\n",
            encoding="utf-8",
        )

    except Exception as error:
        logger.exception(
            "VLM generation failed job=%s",
            job_dir.name,
        )

        raise HTTPException(
            500,
            f"VLM generation failed: {error}",
        ) from error


@router.post("/auto-prompt")
async def auto_prompt(
    image: UploadFile = File(...),
    roi: str = Form("[]"),
):
    if not VLM_MODEL:
        raise HTTPException(
            503,
            "Native VLM is not configured.",
        )

    try:
        roi_points = json.loads(roi)
    except json.JSONDecodeError as error:
        raise HTTPException(
            422,
            "ROI must be JSON.",
        ) from error

    job_dir = WORK_ROOT / ".jobs" / uuid.uuid4().hex

    input_dir = job_dir / "input"
    input_dir.mkdir(parents=True)

    image_path = (
        input_dir
        / safe_name(image.filename or "image.jpg")
    )

    with image_path.open("wb") as output:
        shutil.copyfileobj(
            image.file,
            output,
        )

    run_vlm(
        job_dir,
        {
            "image": str(image_path),
            "roi": roi_points,
        },
    )

    result = json.loads(
        (
            job_dir / "result.json"
        ).read_text(
            encoding="utf-8"
        )
    )

    prompt = result.get(
        "prompt",
        "",
    ).strip()

    if not prompt:
        raise HTTPException(
            500,
            "Native VLM did not return a prompt.",
        )

    prompts = save_prompt_library(
        [
            *load_prompt_library(),
            prompt,
        ]
    )

    logger.info(
        "VLM prompt generated job=%s",
        job_dir.name,
    )

    return {
        "prompt": prompt,
        "prompts": prompts,
    }


@router.post("/auto-prompt/source")
def auto_prompt_source(
    source_token: str = Form(...),
    source_index: int = Form(...),
    roi: str = Form("[]"),
):
    if not VLM_MODEL:
        raise HTTPException(
            503,
            "Native VLM is not configured.",
        )

    try:
        roi_points = json.loads(roi)
    except json.JSONDecodeError as error:
        raise HTTPException(
            422,
            "ROI must be JSON.",
        ) from error

    image_path = source_path(
        source_token,
        source_index,
    )

    job_dir = (
        WORK_ROOT
        / ".jobs"
        / uuid.uuid4().hex
    )

    job_dir.mkdir(
        parents=True,
        exist_ok=True,
    )

    run_vlm(
        job_dir,
        {
            "image": str(image_path),
            "roi": roi_points,
        },
    )

    prompt = json.loads(
        (
            job_dir / "result.json"
        ).read_text(
            encoding="utf-8"
        )
    ).get(
        "prompt",
        "",
    ).strip()

    if not prompt:
        raise HTTPException(
            500,
            "Native VLM did not return a prompt.",
        )

    return {
        "prompt": prompt,
        "prompts": save_prompt_library(
            [
                *load_prompt_library(),
                prompt,
            ]
        ),
    }


@router.post("/annotate")
async def annotate(
    project_name: str = Form(...),
    mode: str = Form(...),
    roi: str = Form("[]"),
    aoi: str = Form("[]"),
    prompts: str = Form("[]"),
    images: list[UploadFile] = File(...),
):
    if mode not in {
        "detection",
        "segmentation",
    }:
        raise HTTPException(
            422,
            "Mode must be detection or segmentation.",
        )

    try:
        roi_points = json.loads(roi)
        aoi_points = json.loads(aoi)
        prompt_values = json.loads(prompts)
    except json.JSONDecodeError as error:
        raise HTTPException(
            422,
            "ROI, AOI and prompts must be JSON.",
        ) from error

    return await create_job(
        project_name,
        mode,
        roi_points,
        aoi_points,
        prompt_values,
        images,
    )


@router.post("/sources/folder")
def inspect_source_folder(
    path: str = Form(...),
):
    return open_folder(path)


@router.post("/sources/video")
def inspect_source_video(
    path: str = Form(...),
    frame_interval_seconds: float = Form(0),
):
    return open_video(
        path,
        frame_interval_seconds,
    )


@router.post("/sources/video-direct")
def inspect_direct_video(path: str = Form(...)):
    return open_direct_video(path)


@router.get("/sources/{token}/video")
def get_direct_video(token: str) -> FileResponse:
    return direct_video_response(token)


@router.get(
    "/sources/{token}/image/{index}"
)
def get_source_image(
    token: str,
    index: int,
) -> FileResponse:
    return source_image(
        token,
        index,
    )


@router.post(
    "/annotate/source-folder"
)
def annotate_source_folder(
    project_name: str = Form(...),
    mode: str = Form(...),
    roi: str = Form("[]"),
    aoi: str = Form("[]"),
    prompts: str = Form("[]"),
    source_token: str = Form(...),
    confidence_threshold: float = Form(0.0),
    sample_limit: int = Form(0),
    pre_annotation: bool = Form(False),
    sample_indices: str = Form("[]"),
    image_thresholds: str = Form("{}"),
    preview_index: int = Form(-1),
    roi_source_index: int = Form(-1),
    annotation_model: str = Form("sam3"),
    live_sync: bool = Form(False),
):
    if mode not in {
        "detection",
        "segmentation",
    }:
        raise HTTPException(
            422,
            "Mode must be detection or segmentation.",
        )

    try:
        roi_points = json.loads(roi)
        aoi_points = json.loads(aoi)
        prompt_values = json.loads(prompts)
        sample_index_values = json.loads(sample_indices)
        threshold_values = json.loads(image_thresholds)
        if not isinstance(threshold_values, dict) or any(
            not isinstance(v, (int, float)) or not 0 <= v <= 1 for v in threshold_values.values()
        ):
            raise HTTPException(422, "Image thresholds must be numbers between 0 and 1.")
    except json.JSONDecodeError as error:
        raise HTTPException(
            422,
            "ROI, AOI and prompts must be JSON.",
        ) from error

    return create_job_from_source(
        project_name,
        mode,
        roi_points,
        aoi_points,
        prompt_values,
        source_token,
        confidence_threshold,
        sample_limit,
        pre_annotation,
        sample_index_values,
        preview_index,
        roi_source_index,
        annotation_model,
        image_thresholds=threshold_values,
        live_sync=live_sync,
    )


@router.post("/annotate/preview")
def semi_annotation_preview(
    project_name: str = Form(...),
    mode: str = Form(...),
    roi: str = Form("[]"),
    aoi: str = Form("[]"),
    prompts: str = Form("[]"),
    source_token: str = Form(...),
    source_index: int = Form(...),
    roi_source_index: int = Form(-1),
    confidence_threshold: float = Form(0.5),
    annotation_model: str = Form("sam3"),
):
    if mode not in {"detection", "segmentation"}:
        raise HTTPException(422, "Mode must be detection or segmentation.")
    try:
        roi_points = json.loads(roi)
        aoi_points = json.loads(aoi)
        prompt_values = json.loads(prompts)
    except json.JSONDecodeError as error:
        raise HTTPException(422, "ROI, AOI and prompts must be JSON.") from error
    return preview_source_image(
        project_name, mode, roi_points, aoi_points, prompt_values,
        source_token, source_index, roi_source_index, annotation_model,
        confidence_threshold,
    )


@router.get("/annotate/previews/{preview_id}/image")
def semi_annotation_preview_image(preview_id: str) -> FileResponse:
    return FileResponse(preview_image_path(preview_id), headers={"Cache-Control": "no-store"})


@router.post("/annotate/source-video")
def annotate_source_video(
    project_name: str = Form(...), mode: str = Form(...), roi: str = Form("[]"),
    aoi: str = Form("[]"), prompts: str = Form("[]"), source_token: str = Form(...),
    confidence_threshold: float = Form(0.0),
    annotation_model: str = Form("sam3"),
):
    try:
        roi_points, aoi_points, prompt_values = json.loads(roi), json.loads(aoi), json.loads(prompts)
    except json.JSONDecodeError as error:
        raise HTTPException(422, "ROI, AOI and prompts must be JSON.") from error
    return create_job_from_video(project_name, mode, roi_points, aoi_points, prompt_values, source_token, confidence_threshold, annotation_model)


@router.get("/annotate/{job_id}")
def annotation_status(
    job_id: str,
):
    return status(job_id)


@router.post(
    "/annotate/{job_id}/cancel"
)
def cancel_annotation(
    job_id: str,
):
    return cancel(job_id)


@router.websocket(
    "/annotate/{job_id}/stream"
)
async def annotation_stream(
    websocket: WebSocket,
    job_id: str,
):
    await websocket.accept()
    display_connection(job_id, True)

    try:
        last_signature = None

        while True:
            payload = status(job_id)

            signature = (
                payload["state"],
                payload["completed"],
                len(payload["results"]),
                payload.get("error"),
            )

            if signature != last_signature:
                await websocket.send_json(
                    payload
                )

                last_signature = signature

            if payload["state"] in {
                "complete",
                "failed",
                "cancelled",
            }:
                break

            try:
                message = await asyncio.wait_for(
                    websocket.receive_text(),
                    timeout=0.35,
                )

                if message == "cancel":
                    cancel(job_id)
                elif message.startswith("displayed:"):
                    try:
                        acknowledge_display(job_id, int(message.split(":", 1)[1]))
                    except ValueError:
                        pass

            except asyncio.TimeoutError:
                pass

    except WebSocketDisconnect:
        return
    finally:
        display_connection(job_id, False)
