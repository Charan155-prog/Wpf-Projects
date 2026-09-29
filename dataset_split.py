import os
import random
import shutil


# ============================================================
# CONFIGURATION
# ============================================================

SOURCE_DATASET = r"D:\YoloTraining\dataset"
OUTPUT_DATASET = r"D:\YoloTraining\dataset_split"

TRAIN_RATIO = 0.70
VAL_RATIO = 0.20
TEST_RATIO = 0.10

RANDOM_SEED = 42


# ============================================================
# SOURCE PATHS
# ============================================================

SOURCE_IMAGES = os.path.join(SOURCE_DATASET, "images")
SOURCE_LABELS = os.path.join(SOURCE_DATASET, "labels")


# ============================================================
# OUTPUT PATHS
# ============================================================

OUTPUT_IMAGES = os.path.join(OUTPUT_DATASET, "images")
OUTPUT_LABELS = os.path.join(OUTPUT_DATASET, "labels")


splits = {
    "train": os.path.join(OUTPUT_IMAGES, "train"),
    "val": os.path.join(OUTPUT_IMAGES, "val"),
    "test": os.path.join(OUTPUT_IMAGES, "test")
}

label_splits = {
    "train": os.path.join(OUTPUT_LABELS, "train"),
    "val": os.path.join(OUTPUT_LABELS, "val"),
    "test": os.path.join(OUTPUT_LABELS, "test")
}


# ============================================================
# CREATE FOLDERS
# ============================================================

for folder in splits.values():
    os.makedirs(folder, exist_ok=True)

for folder in label_splits.values():
    os.makedirs(folder, exist_ok=True)


# ============================================================
# FIND IMAGES
# ============================================================

image_extensions = (
    ".jpg",
    ".jpeg",
    ".png",
    ".bmp",
    ".webp"
)

images = [
    f for f in os.listdir(SOURCE_IMAGES)
    if f.lower().endswith(image_extensions)
    and os.path.isfile(os.path.join(SOURCE_IMAGES, f))
]


print("=" * 60)
print("DATASET SPLIT")
print("=" * 60)

print(f"Source dataset : {SOURCE_DATASET}")
print(f"Output dataset : {OUTPUT_DATASET}")
print(f"Total images   : {len(images)}")


# ============================================================
# CHECK DATASET
# ============================================================

if len(images) == 0:
    print("\nERROR: No images found!")
    print(f"Check this folder:\n{SOURCE_IMAGES}")
    exit()


# ============================================================
# SHUFFLE
# ============================================================

random.seed(RANDOM_SEED)
random.shuffle(images)


# ============================================================
# CALCULATE SPLIT
# ============================================================

total = len(images)

train_count = int(total * TRAIN_RATIO)
val_count = int(total * VAL_RATIO)

train_images = images[:train_count]
val_images = images[train_count:train_count + val_count]
test_images = images[train_count + val_count:]


# ============================================================
# PRINT SPLIT INFORMATION
# ============================================================

print("\nSplit:")
print(f"Train : {len(train_images)} images")
print(f"Val   : {len(val_images)} images")
print(f"Test  : {len(test_images)} images")


# ============================================================
# COPY FUNCTION
# ============================================================

def copy_files(image_list, split_name):

    copied_images = 0
    copied_labels = 0
    missing_labels = 0

    for image_name in image_list:

        # ----------------------------------------------------
        # Image source
        # ----------------------------------------------------

        image_source = os.path.join(
            SOURCE_IMAGES,
            image_name
        )

        # ----------------------------------------------------
        # Image destination
        # ----------------------------------------------------

        image_destination = os.path.join(
            splits[split_name],
            image_name
        )

        # ----------------------------------------------------
        # Label name
        # ----------------------------------------------------

        image_base = os.path.splitext(image_name)[0]

        label_name = image_base + ".txt"

        label_source = os.path.join(
            SOURCE_LABELS,
            label_name
        )

        label_destination = os.path.join(
            label_splits[split_name],
            label_name
        )

        # ----------------------------------------------------
        # Copy image
        # ----------------------------------------------------

        shutil.copy2(
            image_source,
            image_destination
        )

        copied_images += 1

        # ----------------------------------------------------
        # Copy label
        # ----------------------------------------------------

        if os.path.exists(label_source):

            shutil.copy2(
                label_source,
                label_destination
            )

            copied_labels += 1

        else:

            print(
                f"WARNING: Missing label -> {label_name}"
            )

            missing_labels += 1

    return copied_images, copied_labels, missing_labels


# ============================================================
# COPY TRAIN
# ============================================================

print("\nCopying TRAIN...")

train_img, train_lbl, train_missing = copy_files(
    train_images,
    "train"
)


# ============================================================
# COPY VAL
# ============================================================

print("Copying VAL...")

val_img, val_lbl, val_missing = copy_files(
    val_images,
    "val"
)


# ============================================================
# COPY TEST
# ============================================================

print("Copying TEST...")

test_img, test_lbl, test_missing = copy_files(
    test_images,
    "test"
)


# ============================================================
# FINAL SUMMARY
# ============================================================

print("\n" + "=" * 60)
print("DATASET SPLIT COMPLETE")
print("=" * 60)

print("\nTRAIN")
print(f"Images         : {train_img}")
print(f"Labels         : {train_lbl}")
print(f"Missing labels : {train_missing}")

print("\nVAL")
print(f"Images         : {val_img}")
print(f"Labels         : {val_lbl}")
print(f"Missing labels : {val_missing}")

print("\nTEST")
print(f"Images         : {test_img}")
print(f"Labels         : {test_lbl}")
print(f"Missing labels : {test_missing}")

print("\n" + "=" * 60)
print("OUTPUT FOLDER")
print("=" * 60)

print(OUTPUT_DATASET)
print("=" * 60)