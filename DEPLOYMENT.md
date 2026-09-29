# SAIL Deployment on an RTX Test PC

## Hardware target

Use an NVIDIA RTX 30, 40, or 50 series GPU with current Studio drivers. Use 24 GB VRAM or more for reliable local SAM3 and Qwen3-VL-2B inference. The system runs one annotation worker at a time, but that worker processes all selected images sequentially.

## Copy these project assets

Copy the complete project folder, including `ML-Inference-files`, `models/sam3/sam3.pt`, `backend`, `src`, and the Node project files. Do not copy `.next`, `node_modules`, `.conda`, or `backend/workspace/.jobs`.

## Backend setup

Open PowerShell at the project root and run:

```powershell
conda create --prefix .conda/sail python=3.12 -y
conda activate ./.conda/sail
pip install torch==2.10.0 torchvision --index-url https://download.pytorch.org/whl/cu128
pip install -r backend/requirements-inference.txt
```

Place the approved SAM3 checkpoint at `models/sam3/sam3.pt`. To use auto-prompting, download an approved local Qwen3-VL model and set `SAIL_VLM_MODEL` to its full directory path before starting the backend.

Start the backend:

```powershell
$env:SAIL_VLM_MODEL = "Qwen/Qwen3-VL-2B-Instruct" # omit when using manual prompts only
.\backend\run-backend.ps1
```

## Frontend setup

In a second terminal at the project root:

```powershell
npm ci
npm run dev
```

Open `http://localhost:3000`.

## Dataset output

In Settings, type the absolute `Dataset Root Directory` path, for example `D:\SAIL-Datasets`, then choose **Save Changes**. Browser security prevents a normal web browser from returning a full selected folder path, so use the typed path field. After an annotation completes, files are published to:

```text
<Dataset Root Directory>/<Project Name>/<Detection or Segmentation>/<Source Folder or Video Name>/Raw
<Dataset Root Directory>/<Project Name>/<Detection or Segmentation>/<Source Folder or Video Name>/Annotated/Pass
<Dataset Root Directory>/<Project Name>/<Detection or Segmentation>/<Source Folder or Video Name>/Annotated/Fail
<Dataset Root Directory>/<Project Name>/<Detection or Segmentation>/<Source Folder or Video Name>/Labels
```

Detection labels are YOLO bounding-box labels. Segmentation labels are YOLO polygon labels.

Each source appears as a Datasets card. Different sources never replace each other.
Rerunning the same source archives its old directory under the mode's `.history`
directory before creating the new current source folder. Previously validated
snapshots remain available in Training. Equal names from different source paths
receive a source-identity suffix to avoid collisions. Keep `dataset.json` and
`classes.txt` with copied outputs so review state and class IDs remain intact.

Datasets displays annotated Pass images. Edit uses a manually drawn ROI and a
SAM3 correction preview; delete excludes an image reversibly. Validate copies
only included raw images and matching labels into the Settings validated-dataset
location. Annotated overlays are never used as training inputs.

## Updating an existing remote backend

Stop annotation before updating. Copy the **complete `backend` folder**, including
`main.py`, `routers/datasets.py`, `services/dataset_store.py`,
`services/dataset_validation.py`, `services/annotation_jobs.py`, and `ml` modules.
Preserve the server's workspace, settings, output folders, environments and models.
Restart `backend/run-backend.ps1` on the machine running SAM3. Updating the frontend
alone, or copying only `run_annotation.py`, does not update API routes or storage.

Check `http://127.0.0.1:8000/datasets` on that machine: it must return JSON with a
`datasets` list, not `404 Not Found`. Then check the same `/datasets` endpoint
and confirm `/health` contains `"outputLayout": "source-folder-v2"`
through the Cloudflare URL. Set `NEXT_PUBLIC_SAIL_API_URL` to that backend URL
and restart/rebuild Next.js. A tunnel connection/TLS error is separate from a
missing API route. Settings paths refer to the backend machine, not the browser PC.

Older project-wide overwritten files cannot be reconstructed automatically.
Surviving legacy outputs are indexed from job metadata at backend startup;
source-folder outputs are rediscovered when Datasets is refreshed.
