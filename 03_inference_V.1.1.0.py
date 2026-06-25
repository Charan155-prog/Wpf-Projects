"""
03_inference.py
===============
Live Inference Engine for STR34 Sterilizer — Phase-Aware Edition
(with heating split by actual step name)

Key fixes in this version:
  1. HEATING lookup fix: step name is normalised (.upper().strip()) before
     lookup so casing/spacing differences between live data and training
     labels ("Heating 1" vs "HEATING 1") can never cause a miss.
  2. Case-insensitive phase_map loading: keys stored by train.py are also
     normalised on load so the two sides always match.
  3. Fallback-with-warning: if step 34 arrives but the exact name still
     can't be matched, inference warns clearly in the log (step name seen,
     available names) instead of silently falling to "monitoring only".
  4. STEP_MAPPING no longer overrides step 34's real name — the raw column
     value is always used for step 34 (it is used for both display and routing).
  5. All other fixes retained: per-phase models/scalers/buffers, per-phase
     sequence buffer reset on phase entry, dedup, forward-fill, cycle tracker.
  6. ENHANCED REASON SAVING: each anomaly reason now includes:
       - category      : TEMPERATURE / PRESSURE / F0 / LEVEL / ACTUATOR / STABILITY / DIFFERENTIAL / OTHER
       - sensor        : the primary sensor involved
       - current_value : live reading at time of anomaly
       - unit          : engineering unit (°C, bar, %)
       - detail        : human-readable explanation with actual numbers
     New output columns: pred_reason_N_category, pred_reason_N_sensor,
                         pred_reason_N_value, pred_reason_N_detail
     JSON also gets a structured `anomaly_reasons` list per record.
"""

from __future__ import annotations

import json
import logging
import time
import warnings
import argparse
import os
from collections import deque
from pathlib import Path
from datetime import datetime

import numpy as np
import pandas as pd

warnings.filterwarnings("ignore")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
)
log = logging.getLogger(__name__)


# =============================================================================
# ── CONFIG ────────────────────────────────────────────────────────────────────
# =============================================================================

SCRIPT_DIR = Path(__file__).resolve().parent
LIVE_FILE = Path(os.getenv("STERILIZATIONGENIE_LIVE_FILE", SCRIPT_DIR / "live_feed.xlsx"))
LIVE_SHEET = os.getenv("STERILIZATIONGENIE_LIVE_SHEET", "Live")
MODEL_DIR = Path(os.getenv("STERILIZATIONGENIE_MODEL_DIR", SCRIPT_DIR / "models"))
OUTPUT_DIR = Path(os.getenv("STERILIZATIONGENIE_ML_OUTPUT_DIR", SCRIPT_DIR / "output"))
POLL_INTERVAL_SEC = float(os.getenv("STERILIZATIONGENIE_POLL_SECONDS", "1.0"))

# =============================================================================
# ── SENSOR / COLUMN DEFINITIONS ───────────────────────────────────────────────
# =============================================================================

TEMP_SENSORS = [
    "STR34_T100", "STR34_T150", "STR34_T151", "STR34_T174",
    "STR34_T230", "STR34_T251", "STR34_T252", "STR34_T274",
]
PRESSURE_SENSORS = [
    "STR34_P101_P102", "STR34_P104", "STR34_P151", "STR34_P162",
    "STR34_P172",       "STR34_P250", "STR34_P251",
]
F0_SENSORS    = ["STR34_F0_1", "STR34_F0_2"]
LEVEL_SENSORS = ["STR34_L130", "STR34_L230"]
ALL_ANALOG    = TEMP_SENSORS + PRESSURE_SENSORS + F0_SENSORS + LEVEL_SENSORS

DIGITAL_SIGNALS   = ["STR34_Y103", "STR34_Y170"]
STABILITY_SENSORS = TEMP_SENSORS + ["STR34_P101_P102", "STR34_P104", "STR34_F0_1"]
STABILITY_WINDOWS = [5, 10, 20]

INT16_COLS = ["STR34_CRITICAL_ALARM", "STR34_Exp_Time", "STR34_Step", "STR34_Q151"]

DIFF_PAIRS = [
    ("STR34_T150",      "STR34_T151"),
    ("STR34_T251",      "STR34_T252"),
    ("STR34_T100",      "STR34_T150"),
    ("STR34_T174",      "STR34_T251"),
    ("STR34_P101_P102", "STR34_P104"),
    ("STR34_P151",      "STR34_P172"),
    ("STR34_P250",      "STR34_P251"),
    ("STR34_F0_1",      "STR34_F0_2"),
]

# Steps that belong to any phase (for cycle detection).
CYCLE_STEPS      = {26, 34, 44, 50, 51, 64, 65}
CYCLE_START_STEP = 26

STEP_DISPLAY: dict[int, str] = {
    10: "IDLE",
    11: "TRANSITION",
    12: "STARTING",
    13: "PRE_START",
    14: "INITIALIZATION",
    15: "WAITING",
    19: "ENDING",
    26: "FILLING CHAMBER",
    44: "EXPOSURE",
    50: "COOLING SMALL EXT. W161",
    51: "COOLING LARGE EXT. W161",
    64: "EXHAUST ON TOP 2",
    65: "DRAINING IN TANK",
}

_UNITS: dict[str, str] = {
    **{s: "°C"  for s in TEMP_SENSORS},
    **{s: " bar" for s in PRESSURE_SENSORS},
    **{s: " F0"  for s in F0_SENSORS},
    **{s: "%"    for s in LEVEL_SENSORS},
}

# ── Sensor → category mapping ──────────────────────────────────────────────
def _sensor_category(sensor: str) -> str:
    if sensor in TEMP_SENSORS or sensor.startswith("STR34_T"):
        return "TEMPERATURE"
    if sensor in PRESSURE_SENSORS or sensor.startswith("STR34_P"):
        return "PRESSURE"
    if sensor in F0_SENSORS or sensor.startswith("STR34_F0"):
        return "F0_STERILISATION"
    if sensor in LEVEL_SENSORS or sensor.startswith("STR34_L"):
        return "LEVEL"
    if any(sensor.startswith(d) for d in DIGITAL_SIGNALS):
        return "ACTUATOR"
    return "OTHER"


def _feature_category(feat_name: str) -> str:
    """Derive a human-readable category from any feature name."""
    if feat_name in ALL_ANALOG:
        return _sensor_category(feat_name)
    if feat_name.startswith("delta_"):
        return _sensor_category(feat_name[len("delta_"):])
    if any(x in feat_name for x in ("_stability_", "_std_", "_range_")):
        # extract sensor prefix
        for sfx in ("_stability_", "_std_", "_range_"):
            if sfx in feat_name:
                sensor = feat_name.split(sfx)[0]
                return _sensor_category(sensor) + "_STABILITY"
    if feat_name.startswith("diff_"):
        inner = feat_name[len("diff_"):]
        idx   = inner.find("_STR34_", 1)
        sensor_a = inner[:idx] if idx != -1 else inner
        cat = _sensor_category(sensor_a)
        return cat + "_DIFFERENTIAL"
    if any(d in feat_name for d in DIGITAL_SIGNALS):
        return "ACTUATOR"
    return "OTHER"


OUTPUT_COLS = [
    "row_index", "timestamp", "inferred_at",
    "cycle_id", "in_cycle",
    "STR34_Step", "STR34_Step_Name",
    "active_phase",
    "STR34_T100", "STR34_T150", "STR34_P101_P102", "STR34_F0_1",
    "pred_isolation_forest_prob", "pred_mahalanobis_prob",
    "pred_lstm_ae_prob", "pred_ensemble_score",
    "pred_risk_level", "pred_anomaly",
    # ── structured reason columns (new) ──────────────────────────────────
    "pred_reason_1_category", "pred_reason_1_sensor",
    "pred_reason_1_value",    "pred_reason_1_unit",
    "pred_reason_1_detail",
    "pred_reason_2_category", "pred_reason_2_sensor",
    "pred_reason_2_value",    "pred_reason_2_unit",
    "pred_reason_2_detail",
    "pred_reason_3_category", "pred_reason_3_sensor",
    "pred_reason_3_value",    "pred_reason_3_unit",
    "pred_reason_3_detail",
    # ── summary (kept for compatibility) ─────────────────────────────────
    "pred_anomaly_summary",
]


# =============================================================================
# ── PREPROCESSING ─────────────────────────────────────────────────────────────
# =============================================================================

class _PreprocessState:
    def __init__(self):
        self.last_good: dict[str, float] = {}
        self.last_ts:   pd.Timestamp | None = None


def _preprocess_row(
    row: pd.Series, pp: _PreprocessState
) -> tuple[pd.Series | None, str, int]:
    row = row.copy()

    ts_raw = row.get("timestamp", None)
    try:
        ts = pd.to_datetime(ts_raw, errors="coerce")
    except Exception:
        ts = pd.NaT

    if pd.isna(ts):
        ts = pd.Timestamp.now()
    row["timestamp"] = ts

    if pp.last_ts is not None and ts == pp.last_ts:
        return None, "", 0
    pp.last_ts = ts

    for col in ALL_ANALOG:
        raw = row.get(col, np.nan)
        try:
            v = float(raw)
        except (TypeError, ValueError):
            v = np.nan

        if np.isnan(v) or np.isinf(v):
            v = pp.last_good.get(col, 0.0)
            log.debug(f"NaN interpolated for {col} → {v:.3f}")
        else:
            pp.last_good[col] = v
        row[col] = float(v)

    for col in INT16_COLS:
        raw = row.get(col, np.nan)
        try:
            row[col] = int(float(raw)) if pd.notna(raw) else 0
        except (TypeError, ValueError):
            row[col] = 0

    for col in DIGITAL_SIGNALS:
        raw = row.get(col, "")
        if isinstance(raw, (bool, np.bool_)):
            row[col] = bool(raw)
        else:
            row[col] = str(raw).strip().lower() in ("on", "true", "1", "yes")

    step_raw = row.get("STR34_Step", 0)
    try:
        step_int = int(float(step_raw)) if pd.notna(step_raw) else 0
    except (TypeError, ValueError):
        step_int = 0

    if step_int == 34:
        raw_name  = str(row.get("STR34_Step_Name", "")).strip()
        step_name = raw_name if raw_name and raw_name.lower() not in ("nan", "") \
                    else f"STEP_{step_int}"
    else:
        step_name = STEP_DISPLAY.get(step_int, None)
        if step_name is None:
            raw_name  = str(row.get("STR34_Step_Name", "")).strip()
            step_name = raw_name if raw_name and raw_name.lower() not in ("nan", "") \
                        else f"STEP_{step_int}"

    row["STR34_Step"]      = step_int
    row["STR34_Step_Name"] = step_name

    recipe = str(row.get("STR34_Recipe_Name", "")).strip()
    row["STR34_Recipe_Name"] = "NONE" if recipe.lower() in ("", "nan") else recipe

    return row, step_name, step_int


# =============================================================================
# ── FEATURE ENGINEERING ───────────────────────────────────────────────────────
# =============================================================================

def _safe_float(row: pd.Series, col: str, default: float = 0.0) -> float:
    if col not in row.index:
        return default
    try:
        v = float(row[col])
        return default if (np.isnan(v) or np.isinf(v)) else v
    except (TypeError, ValueError):
        return default


def _safe_bool(row: pd.Series, col: str) -> bool:
    if col not in row.index:
        return False
    val = row[col]
    if isinstance(val, (bool, np.bool_)):
        return bool(val)
    return str(val).strip().lower() in ("on", "true", "1", "yes")


class _CycleState:
    def __init__(self, seq_len: int, phase_names: list[str]):
        self.seq_len                       = seq_len
        self.prev_analog:             dict = {}
        self.window_buffers:          dict = {}
        self.phase_counter:           int  = 0
        self.last_phase_step               = None
        self.cycle_start_ts                = None
        self.actuator_last_state:     dict = {}
        self.actuator_last_change_ts: dict = {}
        self.actuator_toggle_count:   dict = {}
        self.sequence_buffers: dict[str, deque] = {
            p: deque(maxlen=seq_len) for p in phase_names
        }
        self._last_phase_seen: str | None = None

    def get_seq_buffer(self, phase_name: str) -> deque:
        return self.sequence_buffers.get(phase_name, deque(maxlen=self.seq_len))

    def maybe_reset_phase_buffer(self, phase_name: str) -> bool:
        if phase_name != self._last_phase_seen:
            if phase_name in self.sequence_buffers:
                self.sequence_buffers[phase_name].clear()
            self._last_phase_seen = phase_name
            return True
        return False


def _build_features(row: pd.Series, state: _CycleState) -> dict:
    feat: dict = {}
    ts = row.get("timestamp", pd.Timestamp.now())
    if pd.isna(ts):
        ts = pd.Timestamp.now()

    for s in ALL_ANALOG:
        feat[s] = _safe_float(row, s)

    for s in ALL_ANALOG:
        feat[f"delta_{s}"] = feat[s] - state.prev_analog.get(s, feat[s])

    for s in STABILITY_SENSORS:
        if s not in state.window_buffers:
            state.window_buffers[s] = deque(maxlen=STABILITY_WINDOWS[-1])
        state.window_buffers[s].append(feat[s])
        buf = list(state.window_buffers[s])
        for w in STABILITY_WINDOWS:
            if len(buf) >= w:
                win  = buf[-w:]
                mean = float(np.mean(win))
                std  = float(np.std(win))
                rng  = float(np.max(win) - np.min(win))
                stab = std / (abs(mean) + 1e-6)
            else:
                std = rng = stab = 0.0
            feat[f"{s}_std_{w}"]       = std
            feat[f"{s}_range_{w}"]     = rng
            feat[f"{s}_stability_{w}"] = stab

    for a, b in DIFF_PAIRS:
        feat[f"diff_{a}_{b}"] = _safe_float(row, a) - _safe_float(row, b)

    cur_step = row.get("STR34_Step", None)
    if state.last_phase_step != cur_step:
        state.phase_counter   = 0
        state.last_phase_step = cur_step
    else:
        state.phase_counter  += 1
    feat["time_in_phase"] = state.phase_counter

    if state.cycle_start_ts is None:
        state.cycle_start_ts = ts
    feat["time_in_cycle_min"] = (ts - state.cycle_start_ts).total_seconds() / 60.0
    feat["phase_progress_pct"] = 0.0

    for d in DIGITAL_SIGNALS:
        curr = _safe_bool(row, d)
        if d not in state.actuator_last_state:
            state.actuator_last_state[d]     = curr
            state.actuator_last_change_ts[d] = ts
            state.actuator_toggle_count[d]   = 0
            changed, time_since = 0, 0.0
        else:
            if curr != state.actuator_last_state[d]:
                changed = 1
                state.actuator_toggle_count[d]   += 1
                state.actuator_last_change_ts[d]  = ts
                state.actuator_last_state[d]      = curr
            else:
                changed = 0
            try:
                time_since = (ts - state.actuator_last_change_ts[d]).total_seconds() / 60.0
            except Exception:
                time_since = 0.0
        feat[f"{d}_changed"]           = changed
        feat[f"{d}_toggle_count"]      = state.actuator_toggle_count[d]
        feat[f"{d}_time_since_change"] = time_since

    state.prev_analog = {s: feat[s] for s in ALL_ANALOG}
    return feat


# =============================================================================
# ── OPERATOR EXPLANATION ENGINE  (ENHANCED) ───────────────────────────────────
# =============================================================================

def _unit(sensor: str) -> str:
    return _UNITS.get(sensor, "")


def _extract_primary_sensor(feat_name: str) -> str:
    """Return the most relevant raw sensor name for a feature."""
    if feat_name in ALL_ANALOG:
        return feat_name
    if feat_name.startswith("delta_"):
        return feat_name[len("delta_"):]
    for sfx in ("_stability_", "_std_", "_range_"):
        if sfx in feat_name:
            return feat_name.split(sfx)[0]
    if feat_name.startswith("diff_"):
        inner = feat_name[len("diff_"):]
        idx   = inner.find("_STR34_", 1)
        return inner[:idx] if idx != -1 else inner
    for d in DIGITAL_SIGNALS:
        if feat_name.startswith(d):
            return d
    return feat_name


def _explain_feature_rich(
    feat_name: str,
    feat:      dict,
    state:     _CycleState,
) -> dict:
    """
    Return a rich reason dict:
    {
        "category"     : str   e.g. "TEMPERATURE"
        "sensor"       : str   e.g. "STR34_T100"
        "current_value": float
        "unit"         : str   e.g. "°C"
        "detail"       : str   full human-readable explanation
    }
    """
    category = _feature_category(feat_name)
    sensor   = _extract_primary_sensor(feat_name)
    unit     = _unit(sensor)
    cur_val  = feat.get(sensor, float("nan"))

    # ── Raw sensor ────────────────────────────────────────────────────────────
    if feat_name in ALL_ANALOG:
        prev_val = state.prev_analog.get(sensor, cur_val)
        buf      = list(state.window_buffers.get(sensor, []))
        avg      = float(np.mean(buf)) if buf else cur_val
        delta    = cur_val - prev_val
        dev      = cur_val - avg

        if abs(delta) >= abs(dev):
            direction = "increased" if delta > 0 else "decreased"
            detail = (
                f"{sensor} {direction} sharply from {prev_val:.2f}{unit} "
                f"to {cur_val:.2f}{unit} ({delta:+.2f}{unit} change between "
                f"consecutive samples)."
            )
        else:
            direction = "above" if dev > 0 else "below"
            detail = (
                f"{sensor} is {abs(dev):.2f}{unit} {direction} its recent "
                f"average of {avg:.2f}{unit} — current reading: {cur_val:.2f}{unit}."
            )
        return dict(category=category, sensor=sensor, current_value=round(cur_val, 4),
                    unit=unit, detail=detail)

    # ── Rate-of-change (delta) ────────────────────────────────────────────────
    if feat_name.startswith("delta_"):
        delta_val = feat.get(feat_name, 0.0)
        direction = "spike" if delta_val > 0 else "drop"
        detail = (
            f"Rapid {direction} detected on {sensor}: "
            f"{delta_val:+.2f}{unit} between consecutive samples "
            f"(current value: {cur_val:.2f}{unit})."
        )
        return dict(category=category, sensor=sensor, current_value=round(cur_val, 4),
                    unit=unit, detail=detail)

    # ── Stability / rolling stats ─────────────────────────────────────────────
    for sfx in ("_stability_", "_std_", "_range_"):
        if sfx in feat_name:
            window  = feat_name.split(sfx)[1]
            val     = feat.get(feat_name, 0.0)
            buf     = list(state.window_buffers.get(sensor, []))
            avg     = float(np.mean(buf)) if buf else cur_val
            if "_stability_" in feat_name:
                detail = (
                    f"{sensor} became unstable over the last {window} samples "
                    f"(stability index: {val:.4f}, current: {cur_val:.2f}{unit}, "
                    f"recent avg: {avg:.2f}{unit})."
                )
            elif "_std_" in feat_name:
                detail = (
                    f"{sensor} shows high variability — std dev: {val:.3f}{unit} "
                    f"over last {window} samples "
                    f"(current: {cur_val:.2f}{unit}, recent avg: {avg:.2f}{unit})."
                )
            else:  # _range_
                detail = (
                    f"{sensor} has a wide operating swing of {val:.2f}{unit} "
                    f"across the last {window} samples "
                    f"(current: {cur_val:.2f}{unit}, recent avg: {avg:.2f}{unit})."
                )
            return dict(category=category, sensor=sensor, current_value=round(cur_val, 4),
                        unit=unit, detail=detail)

    # ── Differential pair ─────────────────────────────────────────────────────
    if feat_name.startswith("diff_"):
        inner = feat_name[len("diff_"):]
        idx   = inner.find("_STR34_", 1)
        if idx != -1:
            sensor_a = inner[:idx]
            sensor_b = inner[idx + 1:]
        else:
            sensor_a, sensor_b = inner, ""
        diff_val = feat.get(feat_name, 0.0)
        val_a    = feat.get(sensor_a, float("nan"))
        val_b    = feat.get(sensor_b, float("nan"))
        cat_label = (
            "Temperature imbalance" if (sensor_a in TEMP_SENSORS or sensor_b in TEMP_SENSORS)
            else "Pressure difference" if (sensor_a in PRESSURE_SENSORS or sensor_b in PRESSURE_SENSORS)
            else "Sensor differential"
        )
        detail = (
            f"{cat_label} between {sensor_a} ({val_a:.2f}{unit}) and "
            f"{sensor_b} ({val_b:.2f}{unit}) is abnormal "
            f"(differential: {diff_val:+.2f}{unit})."
        )
        return dict(category=category, sensor=sensor_a,
                    current_value=round(float(val_a) if not np.isnan(val_a) else 0.0, 4),
                    unit=unit, detail=detail)

    # ── Digital actuator ──────────────────────────────────────────────────────
    for d in DIGITAL_SIGNALS:
        if feat_name == f"{d}_toggle_count":
            count = int(feat.get(feat_name, 0))
            detail = (
                f"{d} has toggled state {count} times this cycle — "
                f"far more than expected for normal operation."
            )
            return dict(category="ACTUATOR", sensor=d, current_value=count,
                        unit=" toggles", detail=detail)
        if feat_name == f"{d}_time_since_change":
            mins = feat.get(feat_name, 0.0)
            state_now = feat.get(d, False)
            state_str = "ON" if state_now else "OFF"
            detail = (
                f"{d} has remained {state_str} for {mins:.1f} min — "
                f"unusually long without a state change."
            )
            return dict(category="ACTUATOR", sensor=d, current_value=round(mins, 2),
                        unit=" min", detail=detail)
        if feat_name == f"{d}_changed":
            detail = f"{d} changed state unexpectedly during this observation."
            return dict(category="ACTUATOR", sensor=d, current_value=1,
                        unit="", detail=detail)

    # ── Fallback ──────────────────────────────────────────────────────────────
    detail = (
        f"Abnormal behaviour detected in signal '{feat_name}' "
        f"(value: {feat.get(feat_name, 'N/A')})."
    )
    return dict(category="OTHER", sensor=feat_name,
                current_value=feat.get(feat_name, float("nan")),
                unit="", detail=detail)


def _generate_explanations(
    feat:         dict,
    state:        _CycleState,
    phase_models: dict,
    scaled:       np.ndarray | None,
    recon:        np.ndarray | None,
    seq_ready:    bool,
    is_anomaly:   bool,
) -> tuple[list[dict], str]:
    """
    Returns:
        reasons  : list of up to 3 rich reason dicts (empty list when no anomaly)
        summary  : one-line anomaly summary string
    """
    if not seq_ready:
        return [], "Waiting for sufficient sequence data for behavioural analysis."

    if not is_anomaly:
        return [], "No significant abnormal process behaviour detected."

    feature_errors = np.mean((scaled - recon) ** 2, axis=(0, 1))
    seq_names      = phase_models["seq_feature_names"]
    ranked_idx     = np.argsort(feature_errors)[::-1]
    top3_names     = [seq_names[i] for i in ranked_idx[:3]]

    reasons = [
        _explain_feature_rich(fname, feat, state)
        for fname in top3_names
    ]

    # Build concise unique clause list for summary sentence
    seen_cats: set = set()
    clauses: list[str] = []
    for r in reasons:
        cat = r["category"]
        if cat not in seen_cats:
            seen_cats.add(cat)
            label = {
                "TEMPERATURE":              "temperature deviation",
                "PRESSURE":                 "pressure deviation",
                "F0_STERILISATION":         "F0 sterilisation value anomaly",
                "LEVEL":                    "level sensor anomaly",
                "ACTUATOR":                 "actuator state anomaly",
                "TEMPERATURE_STABILITY":    "temperature instability",
                "PRESSURE_STABILITY":       "pressure instability",
                "TEMPERATURE_DIFFERENTIAL": "temperature imbalance between sensors",
                "PRESSURE_DIFFERENTIAL":    "pressure differential anomaly",
            }.get(cat, "abnormal signal behaviour")
            clauses.append(label)

    if len(clauses) == 1:
        clause_str = clauses[0]
    elif len(clauses) == 2:
        clause_str = f"{clauses[0]} and {clauses[1]}"
    else:
        clause_str = f"{clauses[0]}, {clauses[1]}, and {clauses[2]}"

    # Lead with the highest-error sensor for immediate operator context
    top = reasons[0]
    summary = (
        f"Anomaly detected: {clause_str}. "
        f"Primary signal: {top['sensor']} = {top['current_value']}{top['unit']}."
    )
    return reasons, summary


# =============================================================================
# ── MODEL LOADING ─────────────────────────────────────────────────────────────
# =============================================================================

def _load_phase_models(
    model_dir: Path,
) -> tuple[dict[str, dict], dict[tuple[int, str | None], str], set[str]]:
    import joblib
    from tensorflow import keras

    phase_map_path = model_dir / "phase_map.json"
    if not phase_map_path.exists():
        raise FileNotFoundError(
            f"phase_map.json not found in {model_dir.resolve()}. "
            "Run train.py first."
        )
    with open(phase_map_path) as f:
        raw_map = json.load(f)

    step_to_phase: dict[tuple[int, str | None], str] = {}
    heating_names: set[str] = set()

    for key, phase_name in raw_map.items():
        parts      = key.split("|")
        step_int   = int(parts[0])
        raw_sname  = parts[1] if len(parts) > 1 else ""
        norm_sname = raw_sname.upper().strip() if raw_sname.strip() else None
        step_to_phase[(step_int, norm_sname)] = phase_name
        if step_int == 34 and norm_sname:
            heating_names.add(norm_sname)

    log.info(f"Phase map loaded ({len(step_to_phase)} entries): {step_to_phase}")
    if heating_names:
        log.info(f"Known HEATING step names (normalised): {sorted(heating_names)}")

    required_files = [
        "isolation_forest.joblib",
        "mahalanobis.joblib",
        "lstm_autoencoder.keras",
        "lstm_ae_threshold.joblib",
        "scaler.joblib",
        "scaler_seq.joblib",
        "feature_columns.joblib",
        "seq_feature_names.joblib",
        "ensemble_weights.joblib",
    ]

    phase_names  = sorted(set(step_to_phase.values()))
    phase_models: dict[str, dict] = {}

    for phase_name in phase_names:
        phase_dir = model_dir / f"phase_{phase_name}"
        if not phase_dir.exists():
            log.warning(f"Phase dir not found: {phase_dir} — skipping {phase_name}.")
            continue

        missing = [f for f in required_files if not (phase_dir / f).exists()]
        if missing:
            log.warning(f"Phase {phase_name}: missing {missing} — skipping.")
            continue

        log.info(f"Loading models for phase: {phase_name} ← {phase_dir.name}")

        if_data   = joblib.load(phase_dir / "isolation_forest.joblib")
        maha_data = joblib.load(phase_dir / "mahalanobis.joblib")
        ae_data   = joblib.load(phase_dir / "lstm_ae_threshold.joblib")
        lstm_ae   = keras.models.load_model(phase_dir / "lstm_autoencoder.keras")
        seq_len   = lstm_ae.input_shape[1]

        phase_models[phase_name] = {
            "iso_forest":        if_data["model"],
            "if_min":            if_data["min_score"],
            "if_max":            if_data["max_score"],
            "if_thresh":         if_data["threshold"],
            "maha_center":       maha_data["center"],
            "maha_cov_inv":      maha_data["cov_inv"],
            "maha_min":          maha_data["min_dist"],
            "maha_max":          maha_data["max_dist"],
            "maha_thresh":       maha_data["threshold"],
            "lstm_ae":           lstm_ae,
            "ae_min":            ae_data["min_mse"],
            "ae_max":            ae_data["max_mse"],
            "ae_thresh":         ae_data["threshold"],
            "scaler":            joblib.load(phase_dir / "scaler.joblib"),
            "feature_cols":      joblib.load(phase_dir / "feature_columns.joblib"),
            "scaler_seq":        joblib.load(phase_dir / "scaler_seq.joblib"),
            "seq_feature_names": joblib.load(phase_dir / "seq_feature_names.joblib"),
            "weights":           joblib.load(phase_dir / "ensemble_weights.joblib"),
            "seq_len":           seq_len,
        }
        log.info(
            f"  [{phase_name}] feats={len(phase_models[phase_name]['feature_cols'])}  "
            f"seq-feats={len(phase_models[phase_name]['seq_feature_names'])}  "
            f"seq_len={seq_len}  "
            f"thresholds: IF={if_data['threshold']:.4f}  "
            f"maha={maha_data['threshold']:.4f}  "
            f"AE={ae_data['threshold']:.4f}"
        )

    if not phase_models:
        raise RuntimeError(
            f"No phase models loaded from {model_dir.resolve()}. "
            "Check that train.py ran successfully."
        )

    loaded     = list(phase_models.keys())
    missing_ph = [p for p in phase_names if p not in phase_models]
    log.info(
        f"Phase models ready: {loaded}"
        + (f"  |  MISSING: {missing_ph}" if missing_ph else "")
    )
    return phase_models, step_to_phase, heating_names


# =============================================================================
# ── PHASE ROUTING ─────────────────────────────────────────────────────────────
# =============================================================================

def _resolve_phase(
    step_int:      int,
    step_name:     str,
    step_to_phase: dict[tuple[int, str | None], str],
    phase_models:  dict[str, dict],
    heating_names: set[str],
) -> tuple[str | None, str | None]:
    if step_int == 34:
        norm       = step_name.upper().strip()
        lookup_key = (step_int, norm)
        phase      = step_to_phase.get(lookup_key, None)
        if phase is None:
            warn = (
                f"Step 34 step-name '{step_name}' (normalised: '{norm}') "
                f"not found in phase map.  "
                f"Known HEATING names: {sorted(heating_names)}.  "
                f"Row scored as monitoring-only."
            )
            return None, warn
        return phase, None
    else:
        lookup_key = (step_int, None)
        phase      = step_to_phase.get(lookup_key, None)
        return phase, None


# =============================================================================
# ── PREDICT ONE ROW ───────────────────────────────────────────────────────────
# =============================================================================

def _predict_row(
    row:          pd.Series,
    phase_name:   str,
    phase_models: dict[str, dict],
    state:        _CycleState,
) -> dict:
    from scipy.spatial.distance import mahalanobis

    models  = phase_models[phase_name]
    seq_len = models["seq_len"]

    feat = _build_features(row, state)

    X_row    = np.array(
        [feat.get(c, 0.0) for c in models["feature_cols"]], dtype="float32"
    ).reshape(1, -1)
    X_scaled = models["scaler"].transform(X_row)

    seq_vec = np.array(
        [feat.get(n, 0.0) for n in models["seq_feature_names"]], dtype="float32"
    )
    seq_buf = state.get_seq_buffer(phase_name)
    seq_buf.append(seq_vec)

    # Isolation Forest
    if_raw  = -float(models["iso_forest"].decision_function(X_scaled)[0])
    if_norm = (if_raw - models["if_min"]) / (models["if_max"] - models["if_min"] + 1e-9)
    if_prob = 1.0 / (1.0 + np.exp(-(if_norm - models["if_thresh"])))

    # Mahalanobis
    md_dist = mahalanobis(X_scaled[0], models["maha_center"], models["maha_cov_inv"])
    md_norm = (md_dist - models["maha_min"]) / (models["maha_max"] - models["maha_min"] + 1e-9)
    md_prob = 1.0 / (1.0 + np.exp(-(md_norm - models["maha_thresh"])))

    # LSTM Autoencoder
    ae_prob    = 0.0
    seq_ready  = len(seq_buf) == seq_len
    scaled_seq: np.ndarray | None = None
    recon_seq:  np.ndarray | None = None

    if seq_ready:
        seq_in     = np.array(seq_buf, dtype="float32").reshape(1, seq_len, -1)
        flat       = seq_in.reshape(-1, seq_in.shape[-1])
        scaled_seq = models["scaler_seq"].transform(flat).reshape(1, seq_len, -1)
        scaled_seq = np.nan_to_num(scaled_seq)
        recon_seq  = models["lstm_ae"].predict(scaled_seq, verbose=0)
        mse        = float(np.mean((scaled_seq - recon_seq) ** 2))
        ae_norm    = (mse - models["ae_min"]) / (models["ae_max"] - models["ae_min"] + 1e-9)
        ae_prob    = 1.0 / (1.0 + np.exp(-(ae_norm - models["ae_thresh"])))

    # Ensemble
    w       = models["weights"]
    ens     = w["if"] * if_prob + w["maha"] * md_prob + w["ae"] * ae_prob
    anomaly = 1 if ens >= 0.75 else 0
    risk    = "ANOMALY" if anomaly else "NORMAL"

    reasons, summary = _generate_explanations(
        feat=feat, state=state, phase_models=models,
        scaled=scaled_seq, recon=recon_seq,
        seq_ready=seq_ready, is_anomaly=bool(anomaly),
    )

    return {
        "feat":                  feat,
        "isolation_forest_prob": round(float(if_prob), 4),
        "mahalanobis_prob":      round(float(md_prob), 4),
        "lstm_ae_prob":          round(float(ae_prob), 4),
        "ensemble_score":        round(float(ens),     4),
        "risk_level":            risk,
        "anomaly":               anomaly,
        "reasons":               reasons,   # list of rich reason dicts
        "anomaly_summary":       summary,
    }


# =============================================================================
# ── LIVE FILE READER ──────────────────────────────────────────────────────────
# =============================================================================

def _read_live_sheet(path: Path, sheet: str) -> pd.DataFrame | None:
    try:
        df = pd.read_excel(str(path), sheet_name=sheet)
        if "Unnamed: 0" in df.columns:
            df.rename(columns={"Unnamed: 0": "timestamp"}, inplace=True)
        elif "timestamp" not in df.columns and len(df.columns) > 0:
            df.rename(columns={df.columns[0]: "timestamp"}, inplace=True)
        df["timestamp"] = pd.to_datetime(df["timestamp"], errors="coerce")
        return df
    except Exception as exc:
        log.debug(f"Could not read live file (will retry): {exc}")
        return None


# =============================================================================
# ── HELPERS: flatten reasons into record dict ─────────────────────────────────
# =============================================================================

def _empty_reason_cols(n: int) -> dict:
    """Return blank reason columns for reason index n (1-based)."""
    return {
        f"pred_reason_{n}_category": "",
        f"pred_reason_{n}_sensor":   "",
        f"pred_reason_{n}_value":    None,
        f"pred_reason_{n}_unit":     "",
        f"pred_reason_{n}_detail":   "",
    }


def _reason_to_cols(r: dict, n: int) -> dict:
    """Flatten a rich reason dict into output column names."""
    return {
        f"pred_reason_{n}_category": r.get("category", ""),
        f"pred_reason_{n}_sensor":   r.get("sensor",   ""),
        f"pred_reason_{n}_value":    r.get("current_value", None),
        f"pred_reason_{n}_unit":     r.get("unit",     ""),
        f"pred_reason_{n}_detail":   r.get("detail",   ""),
    }


def _pack_reason_cols(reasons: list[dict]) -> dict:
    """Build columns for up to 3 reasons, padding with blanks."""
    cols = {}
    for n in range(1, 4):
        if n - 1 < len(reasons):
            cols.update(_reason_to_cols(reasons[n - 1], n))
        else:
            cols.update(_empty_reason_cols(n))
    return cols


# =============================================================================
# ── OUTPUT HELPERS ────────────────────────────────────────────────────────────
# =============================================================================

class ResultStore:

    def __init__(self, output_dir: Path):
        self.output_dir = output_dir
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.excel_path = output_dir / "live_predictions.xlsx"
        self.json_path  = output_dir / "live_predictions.json"
        self.records:    list[dict] = []
        self._json_dict: dict       = {}

    def add(self, record: dict) -> None:
        self.records.append(record)
        key = str(record.get("timestamp", record.get("row_index", len(self.records))))
        self._json_dict[key] = record

    def flush(self) -> None:
        if not self.records:
            return

        df   = pd.DataFrame(self.records)
        cols = [c for c in OUTPUT_COLS if c in df.columns] + \
               [c for c in df.columns  if c not in OUTPUT_COLS]
        df   = df[[c for c in cols if c in df.columns]]

        try:
            df.to_excel(str(self.excel_path), index=False, sheet_name="LivePredictions")
        except Exception as exc:
            log.warning(f"Excel flush failed: {exc}")

        try:
            safe: dict = {}
            for k, v in self._json_dict.items():
                row_safe: dict = {}
                for kk, vv in v.items():
                    if kk == "feat":
                        continue
                    if kk == "anomaly_reasons":
                        # Keep structured list as-is (already JSON-serialisable)
                        row_safe[kk] = vv
                    elif isinstance(vv, (pd.Timestamp, datetime)):
                        row_safe[kk] = vv.isoformat()
                    elif isinstance(vv, np.integer):
                        row_safe[kk] = int(vv)
                    elif isinstance(vv, np.floating):
                        row_safe[kk] = float(vv)
                    else:
                        row_safe[kk] = vv
                safe[k] = row_safe
            with open(self.json_path, "w") as fh:
                json.dump(safe, fh, indent=2, default=str)
        except Exception as exc:
            log.warning(f"JSON flush failed: {exc}")


# =============================================================================
# ── CYCLE TRACKER ─────────────────────────────────────────────────────────────
# =============================================================================

class _CycleTracker:
    def __init__(self):
        self._in_cycle    = False
        self._cycle_count = 0
        self._current_id  = None

    def update(self, step: int) -> tuple[str, bool]:
        if not self._in_cycle:
            if step == CYCLE_START_STEP:
                self._in_cycle    = True
                self._cycle_count += 1
                self._current_id  = f"LIVE_C{self._cycle_count:03d}"
        else:
            if step not in CYCLE_STEPS:
                self._in_cycle   = False
                self._current_id = None

        if self._in_cycle:
            return self._current_id, True
        return "NO_CYCLE", False


# =============================================================================
# ── MAIN LIVE LOOP ────────────────────────────────────────────────────────────
# =============================================================================

def run_live_inference(
    live_file:     Path,
    live_sheet:    str,
    model_dir:     Path,
    output_dir:    Path,
    poll_interval: float = POLL_INTERVAL_SEC,
) -> None:

    log.info("=" * 65)
    log.info("  STR34 LIVE INFERENCE ENGINE  (Phase-Aware, Heating-Name Routing)")
    log.info("=" * 65)
    log.info(f"  Live file  : {live_file}")
    log.info(f"  Sheet      : {live_sheet}")
    log.info(f"  Model dir  : {model_dir}")
    log.info(f"  Output dir : {output_dir}")
    log.info(f"  Poll every : {poll_interval}s")
    log.info("=" * 65)

    log.info("Waiting for live file ...")
    while not live_file.exists():
        time.sleep(poll_interval)
    log.info(f"Live file found: {live_file}")

    phase_models, step_to_phase, heating_names = _load_phase_models(model_dir)
    phase_names    = list(phase_models.keys())
    global_seq_len = max(m["seq_len"] for m in phase_models.values())

    rows_seen      = 0
    global_state   = _CycleState(global_seq_len, phase_names)
    pp_state       = _PreprocessState()
    tracker        = _CycleTracker()
    store          = ResultStore(output_dir)
    _prev_in_cycle = False

    log.info(f"Inference started — {len(phase_models)} phase models: {phase_names}")
    log.info("Press Ctrl-C to stop.\n")

    try:
        while True:
            df_live = _read_live_sheet(live_file, live_sheet)

            if df_live is None or len(df_live) == 0:
                time.sleep(poll_interval)
                continue

            if len(df_live) < rows_seen:
                log.info("Simulator reset detected — resetting all state.")
                rows_seen      = 0
                global_state   = _CycleState(global_seq_len, phase_names)
                pp_state       = _PreprocessState()
                tracker        = _CycleTracker()
                _prev_in_cycle = False

            new_rows = df_live.iloc[rows_seen:]
            if new_rows.empty:
                time.sleep(poll_interval)
                continue

            for local_idx, (_, raw_row) in enumerate(new_rows.iterrows()):
                global_row_idx = rows_seen + local_idx

                row, step_name, step_int = _preprocess_row(raw_row, pp_state)
                if row is None:
                    log.debug(f"Row {global_row_idx}: duplicate timestamp, skipped.")
                    continue

                cycle_id, in_cycle = tracker.update(step_int)

                if step_int == CYCLE_START_STEP and not _prev_in_cycle:
                    global_state.cycle_start_ts = row.get("timestamp", pd.Timestamp.now())
                    log.info(f"New cycle {cycle_id} started.")

                active_phase, routing_warn = _resolve_phase(
                    step_int, step_name, step_to_phase, phase_models, heating_names
                )

                ts_val  = row.get("timestamp", None)
                cyc_tag = f"[{cycle_id}]" if in_cycle else "[NO_CYCLE]"

                if active_phase is None or active_phase not in phase_models:
                    if routing_warn:
                        log.warning(f"{cyc_tag} row={global_row_idx:>5}  {routing_warn}")
                    reason_msg = (
                        "Row outside active sterilization cycle — no prediction made."
                        if not in_cycle else
                        (routing_warn or f"Step {step_int} has no trained phase model.")
                    )
                    record = {
                        "row_index":                  global_row_idx,
                        "inferred_at":                datetime.now().isoformat(timespec="seconds"),
                        "timestamp":                  ts_val,
                        "cycle_id":                   cycle_id,
                        "in_cycle":                   int(in_cycle),
                        "STR34_Step":                 step_int,
                        "STR34_Step_Name":            step_name,
                        "active_phase":               active_phase or "NONE",
                        "STR34_T100":                 float(row.get("STR34_T100",      np.nan)),
                        "STR34_T150":                 float(row.get("STR34_T150",      np.nan)),
                        "STR34_P101_P102":            float(row.get("STR34_P101_P102", np.nan)),
                        "STR34_F0_1":                 float(row.get("STR34_F0_1",      np.nan)),
                        "pred_isolation_forest_prob": None,
                        "pred_mahalanobis_prob":      None,
                        "pred_lstm_ae_prob":          None,
                        "pred_ensemble_score":        None,
                        "pred_risk_level":            "NOT_IN_CYCLE" if not in_cycle else "NO_MODEL",
                        "pred_anomaly":               None,
                        "anomaly_reasons":            [],   # structured JSON field
                        "pred_anomaly_summary":       reason_msg,
                        **_pack_reason_cols([]),            # blank reason columns
                    }
                    store.add(record)
                    log.info(
                        f"{cyc_tag} row={global_row_idx:>5}  "
                        f"step={step_int:<4} {step_name:<28}  ── monitoring only"
                    )

                else:
                    phase_reset = global_state.maybe_reset_phase_buffer(active_phase)
                    if phase_reset:
                        log.info(
                            f"{cyc_tag} Entering phase [{active_phase}] — "
                            f"sequence buffer cleared."
                        )

                    pred = _predict_row(row, active_phase, phase_models, global_state)

                    reasons: list[dict] = pred["reasons"]

                    record = {
                        "row_index":                  global_row_idx,
                        "inferred_at":                datetime.now().isoformat(timespec="seconds"),
                        "timestamp":                  ts_val,
                        "cycle_id":                   cycle_id,
                        "in_cycle":                   1,
                        "STR34_Step":                 step_int,
                        "STR34_Step_Name":            step_name,
                        "active_phase":               active_phase,
                        "STR34_T100":                 float(row.get("STR34_T100",      np.nan)),
                        "STR34_T150":                 float(row.get("STR34_T150",      np.nan)),
                        "STR34_P101_P102":            float(row.get("STR34_P101_P102", np.nan)),
                        "STR34_F0_1":                 float(row.get("STR34_F0_1",      np.nan)),
                        "pred_isolation_forest_prob": pred["isolation_forest_prob"],
                        "pred_mahalanobis_prob":      pred["mahalanobis_prob"],
                        "pred_lstm_ae_prob":          pred["lstm_ae_prob"],
                        "pred_ensemble_score":        pred["ensemble_score"],
                        "pred_risk_level":            pred["risk_level"],
                        "pred_anomaly":               pred["anomaly"],
                        # ── structured reasons (JSON) ──────────────────────
                        "anomaly_reasons":            reasons,
                        # ── flat columns (Excel/CSV) ───────────────────────
                        **_pack_reason_cols(reasons),
                        "pred_anomaly_summary":       pred["anomaly_summary"],
                    }
                    store.add(record)

                    flag = "🔴 ANOMALY" if pred["anomaly"] else "🟢 NORMAL "
                    log.info(
                        f"{cyc_tag} row={global_row_idx:>5}  "
                        f"step={step_int:<4} {step_name:<28}  "
                        f"phase=[{active_phase:<15}]  "
                        f"ens={pred['ensemble_score']:.3f}  {flag}"
                    )
                    if pred["anomaly"] and reasons:
                        for i, r in enumerate(reasons, 1):
                            log.info(
                                f"    ↳ Reason {i} [{r['category']}] "
                                f"{r['sensor']}={r['current_value']}{r['unit']}  "
                                f"→ {r['detail']}"
                            )

                _prev_in_cycle = in_cycle

            rows_seen = len(df_live)
            store.flush()
            time.sleep(poll_interval)

    except KeyboardInterrupt:
        log.info("\nCtrl-C received — shutting down.")
        store.flush()
        log.info(f"  Excel → {store.excel_path}")
        log.info(f"  JSON  → {store.json_path}")
        _print_summary(store.records)


# =============================================================================
# ── SUMMARY ───────────────────────────────────────────────────────────────────
# =============================================================================

def _print_summary(records: list[dict]) -> None:
    if not records:
        log.info("No predictions were made.")
        return

    df        = pd.DataFrame(records)
    total     = len(df)
    anomalies = int(df["pred_anomaly"].sum())

    print("\n" + "=" * 65)
    print("  LIVE INFERENCE SESSION SUMMARY")
    print("=" * 65)
    print(f"  Total rows        : {total}")
    print(f"  Cycle rows        : {int(df['in_cycle'].sum())}")
    print(f"  Non-cycle rows    : {int((df['in_cycle'] == 0).sum())}")
    print(f"  Anomaly rows      : {anomalies}  ({100*anomalies/total:.1f}%)")
    print(f"  Normal rows       : {total - anomalies}")

    phase_df = df[df["active_phase"].notna() & (df["active_phase"] != "NONE")]
    if not phase_df.empty:
        phase_summary = (
            phase_df.groupby("active_phase")
            .agg(
                rows         = ("pred_anomaly", "count"),
                anomaly_rows = ("pred_anomaly", "sum"),
                max_ens      = ("pred_ensemble_score", "max"),
            )
            .reset_index()
        )
        print("\n  Per-Phase Breakdown:")
        print("  " + phase_summary.to_string(index=False).replace("\n", "\n  "))

    cyc = (
        df[df["in_cycle"] == 1]
        .groupby("cycle_id")
        .agg(
            rows         = ("pred_anomaly", "count"),
            anomaly_rows = ("pred_anomaly", "sum"),
            max_ens      = ("pred_ensemble_score", "max"),
            final_risk   = ("pred_risk_level", lambda x: x.iloc[-1]),
        )
        .reset_index()
    )
    if not cyc.empty:
        print("\n  Per-Cycle Breakdown:")
        print("  " + cyc.to_string(index=False).replace("\n", "\n  "))

    anomaly_df = df[df["pred_anomaly"] == 1]
    if not anomaly_df.empty:
        print("\n  Anomaly Detail:")
        for _, r in anomaly_df.iterrows():
            print(
                f"\n  [{r['cycle_id']}] row={r['row_index']}  "
                f"step={r['STR34_Step']} {r.get('STR34_Step_Name', '')}  "
                f"phase=[{r.get('active_phase', '?')}]  "
                f"ens={r['pred_ensemble_score']:.3f}"
            )
            print(f"    Summary : {r.get('pred_anomaly_summary', '')}")
            for n in range(1, 4):
                cat    = r.get(f"pred_reason_{n}_category", "")
                sensor = r.get(f"pred_reason_{n}_sensor",   "")
                val    = r.get(f"pred_reason_{n}_value",    "")
                unit   = r.get(f"pred_reason_{n}_unit",     "")
                detail = r.get(f"pred_reason_{n}_detail",   "")
                if detail:
                    print(f"    Reason {n} [{cat}] {sensor}={val}{unit}")
                    print(f"             {detail}")
    print("=" * 65)


# =============================================================================
# ── ENTRY POINT ───────────────────────────────────────────────────────────────
# =============================================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Monitor a sterilizer workbook and score appended rows.")
    parser.add_argument("--input", default=str(LIVE_FILE), help="Live .xlsx workbook")
    parser.add_argument("--sheet", default=LIVE_SHEET, help="Sheet to monitor")
    parser.add_argument("--model-dir", default=str(MODEL_DIR), help="Phase-model directory")
    parser.add_argument("--output-dir", default=str(OUTPUT_DIR), help="CSV/JSON output directory")
    parser.add_argument("--poll-seconds", type=float, default=POLL_INTERVAL_SEC)
    args = parser.parse_args()
    run_live_inference(
        live_file     = Path(args.input),
        live_sheet    = args.sheet,
        model_dir     = Path(args.model_dir),
        output_dir    = Path(args.output_dir),
        poll_interval = args.poll_seconds,
    )
