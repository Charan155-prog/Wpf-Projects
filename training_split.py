"""Validated pairs -> isolated YOLO split. No input files are modified."""
import json
import random
import shutil
from pathlib import Path

from services.dataset_store import contained, file_hash, validate_labels


def split_dataset(snapshot, destination, ratios=(70, 20, 10), seed=42, cancel=None):
    if len(ratios) != 3 or any(r <= 0 for r in ratios) or sum(ratios) != 100:
        raise ValueError("Train, validation and test percentages must be positive and total 100.")
    root, destination = Path(snapshot["directory"]), Path(destination)
    groups = {}
    stems = set()
    for pair in sorted(snapshot["pairs"], key=lambda p: p["image"]):
        if cancel and cancel.is_set(): raise ValueError("Split cancelled.")
        raw, label = contained(root, pair["image"]), contained(root, pair["label"])
        if raw.stem.casefold() in stems:
            raise ValueError("Duplicate image stems are not safe for YOLO labels.")
        stems.add(raw.stem.casefold())
        for path, key in ((raw, "imageSha256"), (label, "labelSha256")):
            if not path.is_file() or file_hash(path) != pair[key]:
                raise ValueError(f"Validated file changed or missing: {path.name}. Revalidate first.")
        validate_labels(label, snapshot["mode"], len(snapshot["classes"]))
        if raw.stem != label.stem:
            raise ValueError("Each validated raw image must have a matching label filename.")
        group = groups.setdefault(pair["imageSha256"], [])
        if group and group[0]["labelSha256"] != pair["labelSha256"]:
            raise ValueError("Identical raw images have conflicting annotations. Correct them in Validation before training.")
        group.append(pair)
    groups = list(groups.values())
    if len(groups) < 3:
        raise ValueError("At least three distinct images are required for non-empty train/validation/test splits.")
    # Exact duplicates stay together. Nearby video frames still require careful dataset selection.
    random.Random(seed).shuffle(groups)
    n = len(groups)
    a = max(1, min(n - 2, int(n * ratios[0] / 100)))
    b = max(1, min(n - a - 1, int(n * ratios[1] / 100)))
    assigned = dict(zip(("train", "val", "test"), (groups[:a], groups[a:a+b], groups[a+b:])))
    destination.mkdir(parents=True, exist_ok=False)
    manifest = {"seed": seed, "requestedPercentages": ratios, "counts": {}, "pairs": []}
    for split, group_list in assigned.items():
        (destination / "images" / split).mkdir(parents=True)
        (destination / "labels" / split).mkdir(parents=True)
        pairs = [pair for group in group_list for pair in group]
        manifest["counts"][split] = len(pairs)
        for pair in pairs:
            if cancel and cancel.is_set(): raise ValueError("Split cancelled.")
            raw = contained(root, pair["image"])
            for key, target in (("image", destination / "images" / split / raw.name),
                                ("label", destination / "labels" / split / (raw.stem + ".txt"))):
                shutil.copy2(contained(root, pair[key]), target)
                if file_hash(target) != pair[key + "Sha256"]:
                    raise ValueError("Source changed while copying; split was not accepted.")
            manifest["pairs"].append({**pair, "split": split})
    # JSON values are YAML-compatible; never interpolate unquoted class names or Windows paths.
    yaml = f"path: {json.dumps(destination.resolve().as_posix())}\ntrain: images/train\nval: images/val\ntest: images/test\nnames: {json.dumps(snapshot['classes'])}\n"
    (destination / "data.yaml").write_text(yaml, encoding="utf-8")
    (destination / "split.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest
