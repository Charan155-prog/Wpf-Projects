$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:SAIL_CONDA_ENV = "$projectRoot\.conda\sail"
$env:SAIL_CONDA_PYTHON = "$env:SAIL_CONDA_ENV\python.exe"
$env:SAIL_SAM3_CHECKPOINT = "$projectRoot\models\sam3\sam3.pt"
$env:SAIL_ENABLE_LOCAL_SAM3 = "1"
$env:SAIL_VLM_MODEL = "Qwen/Qwen3-VL-2B-Instruct"
# Jobs stream until the selected source is exhausted; there is no application
# image-count cap. Throughput is governed by the GPU and source storage.
$env:SAIL_MAX_BATCH_IMAGES = ""

if (-not (Test-Path $env:SAIL_CONDA_PYTHON)) {
    throw "Conda Python was not found at $env:SAIL_CONDA_PYTHON. Create the sail environment before starting the backend."
}
if (-not (Test-Path $env:SAIL_SAM3_CHECKPOINT)) {
    throw "SAM3 checkpoint was not found at $env:SAIL_SAM3_CHECKPOINT."
}

# Fail before opening the API if this supplied SAM3 build is missing one of
# its non-declared runtime imports. This avoids a misleading "Streaming 0"
# state in the browser.
& $env:SAIL_CONDA_PYTHON -c "import einops, pycocotools; print('SAM3 Python dependencies are ready')"
if ($LASTEXITCODE -ne 0) {
    throw "SAM3 dependencies are missing. Run: & `"$env:SAIL_CONDA_PYTHON`" -m pip install -r `"$PSScriptRoot\requirements-inference.txt`""
}

# Do not use --reload on the GPU host. It can restart the API while a long
# annotation is active. Set SAIL_RELOAD=1 only while editing backend files.
$uvicornArgs = @("-m", "uvicorn", "main:app", "--app-dir", $PSScriptRoot, "--host", "127.0.0.1", "--port", "8000")
if ($env:SAIL_RELOAD -eq "1") { $uvicornArgs += "--reload" }
& $env:SAIL_CONDA_PYTHON @uvicornArgs