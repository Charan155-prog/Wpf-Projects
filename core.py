from __future__ import annotations

import json
import logging
import os
import re
from logging.handlers import RotatingFileHandler
from pathlib import Path

from fastapi import HTTPException


APP_ROOT = Path(__file__).resolve().parent

WORK_ROOT = Path(
    os.getenv(
        "SAIL_WORK_ROOT",
        APP_ROOT / "workspace",
    )
)

PROMPTS_FILE = WORK_ROOT / "prompt-library.json"
PROJECTS_FILE = WORK_ROOT / "projects.json"
SETTINGS_FILE = WORK_ROOT / "settings.json"

CONDA_ENV = Path(
    os.getenv(
        "SAIL_CONDA_ENV",
        APP_ROOT.parent / ".conda" / "sail",
    )
)

CONDA_PYTHON = Path(
    os.getenv(
        "SAIL_CONDA_PYTHON",
        CONDA_ENV
        / (
            "python.exe"
            if os.name == "nt"
            else "bin/python"
        ),
    )
)

ANNOTATION_RUNNER = (
    APP_ROOT / "ml" / "run_annotation.py"
)

VLM_RUNNER = (
    APP_ROOT / "ml" / "run_auto_prompt.py"
)

SAM3_CHECKPOINT = os.getenv(
    "SAIL_SAM3_CHECKPOINT",
    str(
        APP_ROOT.parent
        / "models"
        / "sam3"
        / "sam3.pt"
    ),
)

VLM_MODEL = os.getenv(
    "SAIL_VLM_MODEL",
    "",
)

LOCAL_SAM3_ENABLED = (
    os.getenv(
        "SAIL_ENABLE_LOCAL_SAM3",
        "",
    ).strip()
    == "1"
)


# ---------------------------------------------------------------------------
# WORKSPACE
# ---------------------------------------------------------------------------

WORK_ROOT.mkdir(
    parents=True,
    exist_ok=True,
)

LOG_DIRECTORY = (
    WORK_ROOT / "logs"
)

LOG_DIRECTORY.mkdir(
    parents=True,
    exist_ok=True,
)


# ---------------------------------------------------------------------------
# LOGGING
# ---------------------------------------------------------------------------

def configure_logging() -> logging.Logger:
    logger = logging.getLogger("sail")

    logger.setLevel(
        logging.INFO
    )

    logger.propagate = False

    if logger.handlers:
        return logger

    formatter = logging.Formatter(
        "%(asctime)s | "
        "%(levelname)s | "
        "%(name)s | "
        "process=%(process)d | "
        "thread=%(threadName)s | "
        "%(message)s"
    )

    file_handler = RotatingFileHandler(
        LOG_DIRECTORY / "application.log",
        maxBytes=10_000_000,
        backupCount=10,
        encoding="utf-8",
    )

    file_handler.setLevel(
        logging.INFO
    )

    file_handler.setFormatter(
        formatter
    )

    logger.addHandler(
        file_handler
    )

    return logger


logger = configure_logging()


# ---------------------------------------------------------------------------
# HELPERS
# ---------------------------------------------------------------------------

def safe_name(value: str) -> str:
    cleaned = re.sub(
        r"[^A-Za-z0-9._-]+",
        "_",
        value,
    ).strip("._")

    if not cleaned:
        raise HTTPException(
            422,
            "Project name is required.",
        )

    return cleaned


def read_json(
    path: Path,
    fallback,
):
    if not path.exists():
        return fallback

    try:
        return json.loads(
            path.read_text(
                encoding="utf-8-sig"
            )
        )

    except (
        OSError,
        UnicodeDecodeError,
        json.JSONDecodeError,
    ) as error:

        logger.exception(
            "could not read JSON file=%s error=%s",
            path,
            error,
        )

        return fallback


def write_json(
    path: Path,
    value,
) -> None:
    path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    path.write_text(
        json.dumps(
            value,
            indent=2,
        ),
        encoding="utf-8",
    )


def load_prompt_library() -> list[str]:
    return read_json(
        PROMPTS_FILE,
        [],
    )


def save_prompt_library(
    prompts: list[str],
) -> list[str]:

    unique = list(
        dict.fromkeys(
            prompt.strip()
            for prompt in prompts
            if prompt.strip()
        )
    )

    write_json(
        PROMPTS_FILE,
        unique,
    )

    logger.info(
        "prompt library updated count=%s",
        len(unique),
    )

    return unique


def dataset_project_directory(
    project_name: str,
    mode: str,
) -> Path:

    settings = read_json(
        SETTINGS_FILE,
        {},
    )

    configured_root = str(
        settings.get(
            "datasetRoot",
            "",
        )
    ).strip()

    dataset_root = (
        Path(
            os.path.expandvars(
                os.path.expanduser(
                    configured_root
                )
            )
        )
        if configured_root
        else WORK_ROOT / "datasets"
    )

    return (
        dataset_root
        / safe_name(project_name)
        / mode.capitalize()
    )


def source_output_directory(project_name: str, mode: str, source_name: str) -> Path:
    """All new workflow outputs share the configurable dataset root."""
    return (dataset_project_directory(project_name, mode) / safe_name(source_name)).resolve()
