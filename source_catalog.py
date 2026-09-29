from __future__ import annotations

import mimetypes
import hashlib
from collections import Counter
import uuid
from pathlib import Path

import cv2
from fastapi import HTTPException
from fastapi.responses import FileResponse

from core import logger, WORK_ROOT
from services.video_encoding import browser_video_size, normalize_video_frame, open_browser_video_writer


IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}
VIDEO_EXTENSIONS = {
    ".mp4",
    ".avi",
    ".mov",
    ".mkv",
    ".m4v",
    ".wmv",
    ".webm",
}

CATALOGS: dict[str, list[Path]] = {}
DIRECT_VIDEOS: dict[str, Path] = {}
SOURCE_INFO: dict[str, dict] = {}


def output_names_for(files: list[Path]) -> dict[str, str]:
    counts = Counter(path.stem.casefold() for path in files)
    return {str(path): (path.name if counts[path.stem.casefold()] == 1 else
                       f"{path.stem}--{hashlib.sha256(str(path).encode()).hexdigest()[:12]}{path.suffix}")
            for path in files}


def source_info_for(token: str) -> dict:
    info = SOURCE_INFO.get(token)
    if not info:
        raise HTTPException(404, "Select the source again to restore its folder/video identity.")
    return info


def open_folder(path_value: str) -> dict:
    try:
        root = Path(path_value).expanduser().resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise HTTPException(
            422,
            f"The supplied folder path cannot be opened: {error}",
        ) from error

    if not root.is_dir():
        raise HTTPException(422, "The supplied path is not a folder.")

    try:
        files = sorted(
            path
            for path in root.rglob("*")
            if path.is_file()
            and path.suffix.lower() in IMAGE_EXTENSIONS
        )
    except PermissionError as error:
        raise HTTPException(
            403,
            f"Permission was denied while reading the folder: {error}",
        ) from error

    if not files:
        raise HTTPException(
            422,
            "No supported image files were found in this folder.",
        )

    token = uuid.uuid4().hex
    CATALOGS[token] = files
    SOURCE_INFO[token] = {"path": str(root), "name": root.name, "kind": "folder"}
    names = output_names_for(files)

    logger.info(
        "source folder selected token=%s path=%s images=%s",
        token,
        root,
        len(files),
    )

    return {
        "token": token,
        "count": len(files),
        "sourceType": "folder",
        "files": [
            {
                "name": names[str(path)],
                "index": index,
            }
            for index, path in enumerate(files)
        ],
    }


def open_video(
    path_value: str,
    frame_interval_seconds: float = 0,
) -> dict:
    """
    Extract EVERY decoded frame from a backend-hosted video.

    Important:
    - frame_interval_seconds=0 means every frame.
    - This is now the default.
    - The number of generated images therefore depends entirely
      on the actual video.
    - Example:
        895-frame video -> approximately 895 extracted frames.
        420-frame video -> approximately 420 extracted frames.
        2,000-frame video -> approximately 2,000 extracted frames.

    The extracted frames are then exposed through the same image-source
    pipeline used by SAM3.
    """

    try:
        video_path = Path(path_value).expanduser().resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise HTTPException(
            422,
            f"The supplied video path cannot be opened: {error}",
        ) from error

    if not video_path.is_file():
        raise HTTPException(
            422,
            "The supplied path is not a video file.",
        )

    if video_path.suffix.lower() not in VIDEO_EXTENSIONS:
        raise HTTPException(
            422,
            "Unsupported video format.",
        )

    capture = None

    try:
        capture = cv2.VideoCapture(str(video_path))

        if not capture.isOpened():
            raise RuntimeError(
                "OpenCV could not open this video. "
                "Use a standard MP4, AVI, MOV, MKV, or supported video file."
            )

        # Metadata reported by the video container.
        fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0)

        reported_frame_count = int(
            capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0
        )

        width = int(
            capture.get(cv2.CAP_PROP_FRAME_WIDTH) or 0
        )

        height = int(
            capture.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0
        )

        if fps <= 0:
            logger.warning(
                "video has invalid FPS metadata video=%s fps=%s",
                video_path,
                fps,
            )

        if width <= 0 or height <= 0:
            raise RuntimeError(
                "The video does not contain a readable frame size."
            )

        # ------------------------------------------------------------------
        # IMPORTANT
        # ------------------------------------------------------------------
        # 0 or negative means:
        #       SAVE EVERY DECODED FRAME
        #
        # We intentionally do NOT calculate:
        #
        #     fps * seconds
        #
        # because that was causing a 28-second video to become ~28 frames.
        # ------------------------------------------------------------------

        save_every_n_frames = 1

        if frame_interval_seconds and frame_interval_seconds > 0:
            # Optional compatibility mode.
            #
            # This is NOT used by the current frontend.
            # It is kept only so older API clients don't completely break.
            save_every_n_frames = max(
                1,
                round(fps * frame_interval_seconds),
            )

        token = uuid.uuid4().hex

        cache_directory = (
            WORK_ROOT
            / "source-videos"
            / token
        )

        cache_directory.mkdir(
            parents=True,
            exist_ok=True,
        )

        files: list[Path] = []

        decoded_frame_index = 0
        saved_frame_index = 0

        logger.info(
            "video extraction started "
            "video=%s token=%s "
            "reported_frames=%s fps=%.3f "
            "resolution=%sx%s save_every=%s",
            video_path,
            token,
            reported_frame_count,
            fps,
            width,
            height,
            save_every_n_frames,
        )

        while True:
            ok, frame = capture.read()

            if not ok:
                break

            # Default behaviour:
            # frame 0, frame 1, frame 2, frame 3...
            if decoded_frame_index % save_every_n_frames == 0:
                target = (
                    cache_directory
                    / f"frame_{saved_frame_index:06d}.jpg"
                )

                if not cv2.imwrite(
                    str(target),
                    frame,
                ):
                    raise RuntimeError(
                        f"Could not write extracted frame "
                        f"{saved_frame_index}."
                    )

                files.append(target)
                saved_frame_index += 1

            decoded_frame_index += 1

        if capture is not None:
            capture.release()
            capture = None

    except HTTPException:
        if capture is not None:
            capture.release()
        raise

    except Exception as error:
        if capture is not None:
            capture.release()

        logger.exception(
            "video extraction failed video=%s",
            video_path,
        )

        raise HTTPException(
            422,
            f"Video extraction failed: {error}",
        ) from error

    if not files:
        raise HTTPException(
            422,
            "No frames could be extracted from the video.",
        )

    CATALOGS[token] = files

    SOURCE_INFO[token] = {"path": str(video_path), "name": video_path.name, "kind": "video"}

    # This is useful because it lets you compare the container's
    # reported frame count against what OpenCV actually decoded.
    logger.info(
        "video extraction completed "
        "token=%s video=%s "
        "reported_frames=%s decoded_frames=%s "
        "saved_frames=%s fps=%.3f",
        token,
        video_path,
        reported_frame_count,
        decoded_frame_index,
        len(files),
        fps,
    )

    return {
        "token": token,
        "count": len(files),
        "sourceType": "video",

        # Useful metadata for debugging and monitoring.
        "fps": fps,
        "reportedFrameCount": reported_frame_count,
        "decodedFrameCount": decoded_frame_index,
        "extractedFrameCount": len(files),
        "width": width,
        "height": height,

        "files": [
            {
                "name": path.name,
                "index": index,
            }
            for index, path in enumerate(files)
        ],
    }


def _fourcc_string(value: float) -> str:
    code = int(value)
    chars = "".join(chr((code >> (8 * i)) & 0xFF) for i in range(4))
    return chars.strip().lower()


# Containers/codecs an HTML5 <video> tag can play directly. Kept conservative
# (H.264-in-MP4, VP8/VP9-in-WebM) so anything outside this list still gets the
# safe transcoded preview below rather than a guess that fails to play.
_BROWSER_SAFE_VIDEO_CODECS = {
    ".mp4": {"avc1", "h264", "x264", "avc3"},
    ".m4v": {"avc1", "h264", "x264", "avc3"},
    ".webm": {"vp80", "vp90", "vp8", "vp9"},
}


def _is_browser_playable(video_path: Path, capture) -> bool:
    codecs = _BROWSER_SAFE_VIDEO_CODECS.get(video_path.suffix.lower())
    if not codecs:
        return False
    return _fourcc_string(capture.get(cv2.CAP_PROP_FOURCC)) in codecs


def open_direct_video(path_value: str) -> dict:
    """Register a video without creating a user-visible frame catalogue."""
    try:
        video_path = Path(path_value).expanduser().resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise HTTPException(422, f"The supplied video path cannot be opened: {error}") from error
    if not video_path.is_file():
        raise HTTPException(422, "The supplied path is not a file.")
    capture = cv2.VideoCapture(str(video_path), cv2.CAP_FFMPEG)
    if not capture.isOpened():
        capture.release()
        capture = cv2.VideoCapture(str(video_path))
    if not capture.isOpened():
        raise HTTPException(422, "The backend video decoder could not open this file. The format or codec is unsupported by the installed OpenCV/FFmpeg build.")
    fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0)
    frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
    if width <= 0 or height <= 0:
        capture.release()
        raise HTTPException(422, "The video does not report a valid frame size.")
    token = uuid.uuid4().hex
    DIRECT_VIDEOS[token] = video_path
    SOURCE_INFO[token] = {"path": str(video_path), "name": video_path.name, "kind": "video"}
    # Most exported footage (H.264 MP4, VP8/VP9 WebM) already plays natively
    # in the browser. Only pay for a full decode+re-encode preview pass when
    # the source actually needs it (e.g. AVI/exotic codecs); otherwise
    # "Load direct video" just registers the file and returns immediately,
    # and playback below falls back to serving the original directly.
    if _is_browser_playable(video_path, capture):
        capture.release()
    else:
        preview_directory = WORK_ROOT / "source-videos" / token
        preview_directory.mkdir(parents=True, exist_ok=True)
        preview_path = preview_directory / "preview.webm"
        # A bounded preview starts faster over the Cloudflare tunnel; annotation
        # still reads the original video at its full resolution.
        output_size = browser_video_size(width, height, max_dimension=640)
        try:
            writer = open_browser_video_writer(preview_path, fps, output_size)
        except RuntimeError as error:
            capture.release()
            raise HTTPException(500, str(error)) from error
        while True:
            ok, frame = capture.read()
            if not ok:
                break
            writer.write(normalize_video_frame(frame, output_size))
        capture.release()
        writer.release()
        DIRECT_VIDEOS[f"{token}:preview"] = preview_path
    return {
        "token": token,
        "count": frame_count,
        "sourceType": "direct-video",
        "name": video_path.name,
        "fps": fps,
        "width": width,
        "height": height,
        "videoUrl": f"/sources/{token}/video",
    }


def direct_video_for(token: str) -> Path:
    path = DIRECT_VIDEOS.get(token)
    if not path or not path.is_file():
        raise HTTPException(404, "The selected direct video is no longer available. Select it again.")
    return path


def direct_video_response(token: str) -> FileResponse:
    path = DIRECT_VIDEOS.get(f"{token}:preview") or direct_video_for(token)
    return FileResponse(path, media_type="video/webm" if path.suffix.lower() == ".webm" else (mimetypes.guess_type(path.name)[0] or "video/mp4"))


def files_for(token: str) -> list[Path]:
    files = CATALOGS.get(token)

    if not files:
        raise HTTPException(
            404,
            "The selected source folder is no longer available. "
            "Select it again.",
        )

    return files


def source_image(
    token: str,
    index: int,
) -> FileResponse:
    files = files_for(token)

    if index < 0 or index >= len(files):
        raise HTTPException(
            404,
            "Source image was not found.",
        )

    path = files[index]

    if not path.is_file():
        raise HTTPException(
            410,
            "Source image no longer exists.",
        )

    return FileResponse(
        path,
        media_type=(
            mimetypes.guess_type(path.name)[0]
            or "application/octet-stream"
        ),
    )


def source_path(
    token: str,
    index: int,
) -> Path:
    files = files_for(token)

    if (
        index < 0
        or index >= len(files)
        or not files[index].is_file()
    ):
        raise HTTPException(
            404,
            "Source image was not found.",
        )

    return files[index]