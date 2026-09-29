"""SAIL API composition root."""

from __future__ import annotations

import time
import uuid

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

from core import WORK_ROOT, logger
from routers.annotation import router as annotation_router
from routers.catalog import router as catalog_router
from routers.datasets import router as datasets_router
from routers.training import router as training_router
from routers.inference import router as inference_router
from services.model_runtime import runtime


app = FastAPI(
    title="SAIL Annotation API",
    version="0.3.0",
)


# ---------------------------------------------------------------------------
# CORS
# ---------------------------------------------------------------------------

app.add_middleware(
    CORSMiddleware,
    allow_origin_regex=r"https?://(localhost|127\.0\.0\.1)(:\d+)?",
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ---------------------------------------------------------------------------
# Static files
# ---------------------------------------------------------------------------

app.mount(
    "/files",
    StaticFiles(directory=WORK_ROOT),
    name="files",
)


# ---------------------------------------------------------------------------
# ROUTERS
# ---------------------------------------------------------------------------

app.include_router(catalog_router)
app.include_router(annotation_router)
app.include_router(datasets_router)
app.include_router(training_router)
app.include_router(inference_router)


# ---------------------------------------------------------------------------
# CENTRAL HTTP REQUEST LOGGER
# ---------------------------------------------------------------------------

@app.middleware("http")
async def request_logging_middleware(
    request: Request,
    call_next,
):
    """
    Central request logger.

    Every frontend HTTP request that reaches FastAPI passes through here.

    We intentionally do NOT log request bodies because they may contain:
    - prompts
    - paths
    - large multipart payloads
    - potentially sensitive information

    Instead we log:
    - request ID
    - method
    - path
    - query presence
    - origin
    - referer
    - user agent
    - client IP
    - status code
    - duration
    """

    request_id = request.headers.get(
        "X-Request-ID"
    ) or uuid.uuid4().hex

    start_time = time.perf_counter()

    client_host = (
        request.client.host
        if request.client
        else "unknown"
    )

    # Cloudflare / reverse-proxy environments may provide these.
    forwarded_for = request.headers.get(
        "X-Forwarded-For"
    )

    real_ip = request.headers.get(
        "CF-Connecting-IP"
    )

    origin = request.headers.get(
        "Origin",
        "-",
    )

    referer = request.headers.get(
        "Referer",
        "-",
    )

    user_agent = request.headers.get(
        "User-Agent",
        "-",
    )

    # Never log the full query string.
    # It may contain paths, tokens or sensitive values.
    query_present = bool(request.url.query)

    try:
        response = await call_next(request)

        duration_ms = (
            time.perf_counter() - start_time
        ) * 1000

        response.headers["X-Request-ID"] = request_id

        logger.info(
            "HTTP "
            "request_id=%s "
            "method=%s "
            "path=%s "
            "status=%s "
            "duration_ms=%.2f "
            "client=%s "
            "cf_ip=%s "
            "forwarded_for=%s "
            "origin=%s "
            "referer=%s "
            "query=%s "
            "user_agent=%s",
            request_id,
            request.method,
            request.url.path,
            response.status_code,
            duration_ms,
            client_host,
            real_ip or "-",
            forwarded_for or "-",
            origin,
            referer,
            query_present,
            user_agent,
        )

        return response

    except Exception:
        duration_ms = (
            time.perf_counter() - start_time
        ) * 1000

        logger.exception(
            "HTTP "
            "request_id=%s "
            "method=%s "
            "path=%s "
            "status=500 "
            "duration_ms=%.2f "
            "client=%s",
            request_id,
            request.method,
            request.url.path,
            duration_ms,
            client_host,
        )

        raise


# ---------------------------------------------------------------------------
# STARTUP
# ---------------------------------------------------------------------------

@app.on_event("startup")
async def warm_models() -> None:
    from services.dataset_store import register_existing_outputs
    register_existing_outputs()
    logger.info(
        "SAIL API startup: beginning model warm-up"
    )

    # GPU warm-up happens once in the background.
    runtime.warm_sam3_async()
    runtime.warm_vlm_async()

    logger.info(
        "SAIL API startup: model warm-up tasks scheduled"
    )


# ---------------------------------------------------------------------------
# VALIDATION ERRORS
# ---------------------------------------------------------------------------

@app.exception_handler(RequestValidationError)
async def validation_error_handler(
    request: Request,
    error: RequestValidationError,
):
    logger.warning(
        "validation error "
        "method=%s "
        "path=%s "
        "errors=%s",
        request.method,
        request.url.path,
        error.errors(),
    )

    return JSONResponse(
        status_code=422,
        content={
            "detail": "Invalid request data.",
            "errors": error.errors(),
        },
    )


# ---------------------------------------------------------------------------
# UNHANDLED ERRORS
# ---------------------------------------------------------------------------

@app.exception_handler(Exception)
async def unhandled_error_handler(
    request: Request,
    error: Exception,
):
    logger.exception(
        "unhandled error "
        "method=%s "
        "path=%s",
        request.method,
        request.url.path,
    )

    return JSONResponse(
        status_code=500,
        content={
            "detail": (
                "An unexpected server error occurred. "
                "See application.log for details."
            )
        },
    )


logger.info(
    "SAIL API module loaded workspace=%s",
    WORK_ROOT,
)
