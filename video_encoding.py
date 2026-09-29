from __future__ import annotations

import os
from pathlib import Path

import cv2


def browser_video_size(width: int, height: int, max_dimension: int | None = None) -> tuple[int, int]:
    """Web video codecs require positive, even frame dimensions."""
    if max_dimension and max(width, height) > max_dimension:
        scale = max_dimension / max(width, height)
        width, height = round(width * scale), round(height * scale)
    return max(2, width - width % 2), max(2, height - height % 2)


def _quiet_writer(path: Path, codec: str, fps: float, size: tuple[int, int]):
    """OpenCV prints harmless FFmpeg codec-tag diagnostics directly to stderr."""
    saved_stderr = os.dup(2)
    try:
        with open(os.devnull, "w", encoding="utf-8") as sink:
            os.dup2(sink.fileno(), 2)
            return cv2.VideoWriter(
                str(path),
                cv2.CAP_FFMPEG,
                cv2.VideoWriter_fourcc(*codec),
                fps,
                size,
            )
    finally:
        os.dup2(saved_stderr, 2)
        os.close(saved_stderr)


def open_browser_video_writer(path: Path, fps: float, size: tuple[int, int]):
    """Create a browser-playable WebM writer using codecs available in this OpenCV build."""
    normalized_fps = fps if 0 < fps <= 240 else 25.0
    # VP8 is materially faster for preview/output generation; VP9 is the
    # compatibility fallback when a particular backend build lacks VP8.
    for codec in ("VP80", "VP90"):
        writer = _quiet_writer(path, codec, normalized_fps, size)
        if writer.isOpened():
            return writer
        writer.release()
    raise RuntimeError(
        "This OpenCV installation cannot encode VP8/VP9 WebM. "
        "Install an OpenCV build with FFmpeg WebM support."
    )


def normalize_video_frame(frame, size: tuple[int, int]):
    width, height = size
    if frame.shape[1] == width and frame.shape[0] == height:
        return frame
    return cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)
