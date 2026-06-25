"""Persistent IPC host for the unmodified 03 inference engine.

This module contains no anomaly model or scoring formula. It loads
03_inference_V.1.1.0.py once, keeps that module's preprocessing/cycle/sequence
state between requests, and exposes its exact row records to the WPF process.
"""

from __future__ import annotations

import importlib.util
import json
import math
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import numpy as np
import pandas as pd


def _load_engine(path: Path):
    spec = importlib.util.spec_from_file_location("sterilization_engine_03", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Unable to load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _safe(value):
    if isinstance(value, dict):
        return {str(k): _safe(v) for k, v in value.items() if k != "feat"}
    if isinstance(value, (list, tuple)):
        return [_safe(v) for v in value]
    if isinstance(value, (pd.Timestamp, datetime)):
        if pd.isna(value):
            return None
        return value.isoformat()
    if isinstance(value, (np.bool_, bool)):
        return bool(value)
    if value is None or value is pd.NA:
        return None
    if isinstance(value, (str, int)):
        return value
    try:
        if pd.isna(value):
            return None
    except Exception:
        pass
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, (np.floating, float)):
        number = float(value)
        return number if math.isfinite(number) else None
    return value


def _read_sheets(path: Path) -> dict[str, pd.DataFrame]:
    """Full read of every sheet. Used by `prime()`, where we genuinely need
    the whole workbook once to establish starting row-counts and step names."""
    raw = pd.read_excel(str(path), sheet_name=None)
    sheets: dict[str, pd.DataFrame] = {}
    for name, df in raw.items():
        if df.empty:
            sheets[name] = df
            continue
        if "Unnamed: 0" in df.columns:
            df.rename(columns={"Unnamed: 0": "timestamp"}, inplace=True)
        elif "timestamp" not in df.columns and len(df.columns) > 0:
            df.rename(columns={df.columns[0]: "timestamp"}, inplace=True)
        df["timestamp"] = pd.to_datetime(df["timestamp"], errors="coerce")
        sheets[name] = df
    return sheets


def _normalize_sheet_frame(name: str, df: pd.DataFrame) -> pd.DataFrame:
    if df.empty:
        return df
    if "Unnamed: 0" in df.columns:
        df = df.rename(columns={"Unnamed: 0": "timestamp"})
    elif "timestamp" not in df.columns and len(df.columns) > 0:
        df = df.rename(columns={df.columns[0]: "timestamp"})
    df["timestamp"] = pd.to_datetime(df["timestamp"], errors="coerce")
    return df


def _read_sheet_tail(path: Path, sheet_name: str, rows_seen: int) -> tuple[pd.DataFrame, int]:
    """Read only the rows of `sheet_name` past `rows_seen`, without ever
    materialising the rest of the sheet. Returns (new_rows, current_row_count)
    so the caller can also detect a truncated/restarted file (current count
    less than rows_seen) from the same workbook open.

    The previous implementation called pd.read_excel() on the *entire*
    workbook on every single score() request, which re-parses the whole
    .xlsx XML from scratch each time. During an online session that means
    every ~2-second delta tick paid a cost that grows with the total number
    of rows recorded so far, not with the (small, constant) number of new
    rows â€” which is exactly what produced the 20-30s "stuck then burst"
    behaviour the live chart and ML status showed. openpyxl's read_only
    mode streams rows instead of loading the sheet into memory, so we can
    skip straight past rows_seen and only pay for the rows that are
    actually new.
    """
    import openpyxl

    workbook = openpyxl.load_workbook(str(path), read_only=True, data_only=True)
    try:
        if sheet_name not in workbook.sheetnames:
            return pd.DataFrame(), 0
        worksheet = workbook[sheet_name]
        row_iter = worksheet.iter_rows(values_only=True)
        try:
            header = next(row_iter)
        except StopIteration:
            return pd.DataFrame(), 0

        columns = [str(c) if c is not None else f"col_{i}" for i, c in enumerate(header)]
        new_rows: list[tuple] = []
        current_row_count = 0
        # Row 1 is the header; data rows are 1-indexed from there, so the
        # first data row is absolute row index 0 in pandas terms.
        for data_index, values in enumerate(row_iter):
            current_row_count = data_index + 1
            if data_index < rows_seen:
                continue
            new_rows.append(values)

        if not new_rows:
            return pd.DataFrame(columns=columns), current_row_count

        frame = pd.DataFrame(new_rows, columns=columns)
        return _normalize_sheet_frame(sheet_name, frame), current_row_count
    finally:
        workbook.close()


def _read_workbook_sheet_names(path: Path) -> list[str]:
    import openpyxl

    workbook = openpyxl.load_workbook(str(path), read_only=True, data_only=True)
    try:
        return list(workbook.sheetnames)
    finally:
        workbook.close()


def _step_names(sheets: dict[str, pd.DataFrame]) -> list[str]:
    result: set[str] = set()
    for df in sheets.values():
        if "STR34_Step_Name" not in df.columns:
            continue
        for raw in df["STR34_Step_Name"].dropna().astype(str):
            name = raw.strip()
            if name and name.upper() != "STR34_STEP_NAME":
                result.add(name)
    return sorted(result, key=str.upper)


@dataclass
class SheetSession:
    engine: object
    phase_names: list[str]
    sequence_length: int
    rows_seen: int = 0
    previous_in_cycle: bool = False
    state: object = field(init=False)
    preprocess: object = field(init=False)
    tracker: object = field(init=False)

    def __post_init__(self):
        self.reset()

    def reset(self):
        self.rows_seen = 0
        self.previous_in_cycle = False
        self.state = self.engine._CycleState(self.sequence_length, self.phase_names)
        self.preprocess = self.engine._PreprocessState()
        self.tracker = self.engine._CycleTracker()


@dataclass
class WorkbookSession:
    sheets: dict[str, SheetSession] = field(default_factory=dict)
    records: list[dict] = field(default_factory=list)


class Host:
    def __init__(self, engine_path: Path, model_dir: Path):
        self.engine = _load_engine(engine_path)
        loaded = self.engine._load_phase_models(model_dir)
        self.phase_models, self.step_to_phase, self.heating_names = loaded
        self.phase_names = list(self.phase_models.keys())
        self.sequence_length = max(m["seq_len"] for m in self.phase_models.values())
        self.sessions: dict[str, WorkbookSession] = {}

    def _sheet(self, workbook: WorkbookSession, name: str) -> SheetSession:
        if name not in workbook.sheets:
            workbook.sheets[name] = SheetSession(
                self.engine, self.phase_names, self.sequence_length
            )
        return workbook.sheets[name]

    def prime(self, key: str, path: Path) -> dict:
        sheets = _read_sheets(path)
        workbook = WorkbookSession()
        for name, df in sheets.items():
            state = self._sheet(workbook, name)
            state.rows_seen = len(df)
        self.sessions[key] = workbook
        return {"ok": True, "step_names": _step_names(sheets)}

    def score(self, key: str, path: Path, reset: bool, output_json: Path, output_csv: Path,
              input_rows: list[dict] | None = None) -> dict:
        if reset or key not in self.sessions:
            # First touch of this session (or an explicit reset, e.g. offline
            # re-import) â€” we don't yet know how many rows exist per sheet,
            # so do one full read here. Every subsequent score() call for
            # this same session only streams the tail.
            sheets = _read_sheets(path)
            self.sessions[key] = WorkbookSession()
            workbook = self.sessions[key]
            produced: list[dict] = []
            for sheet_name, df in sheets.items():
                sheet = self._sheet(workbook, sheet_name)
                for local_index, (_, raw_row) in enumerate(df.iterrows()):
                    record = self._score_row(sheet, raw_row, local_index)
                    if record is not None:
                        produced.append(record)
                        workbook.records.append(record)
                sheet.rows_seen = len(df)
            self._flush(workbook.records, output_json, output_csv)
            return {
                "ok": True,
                "records": _safe(produced),
                "step_names": _step_names(sheets),
                "total_records": len(workbook.records),
            }

        # Steady-state online tick: stream only the rows past what we've
        # already scored for each sheet, instead of re-parsing the whole
        # workbook. Cost now scales with new-row count, not total row count.
        workbook = self.sessions[key]
        produced: list[dict] = []
        seen_step_names: set[str] = set()

        if input_rows is not None:
            for payload in input_rows:
                values = dict(payload)
                sheet_name = str(values.pop("__sheet_name", "") or "Live")
                sheet = self._sheet(workbook, sheet_name)
                raw_row = pd.Series(values)
                if "timestamp" in raw_row:
                    raw_row["timestamp"] = pd.to_datetime(raw_row["timestamp"], errors="coerce")
                step_name = str(raw_row.get("STR34_Step_Name", "") or "").strip()
                if step_name and step_name.upper() != "STR34_STEP_NAME":
                    seen_step_names.add(step_name)
                record = self._score_row(sheet, raw_row, sheet.rows_seen)
                sheet.rows_seen += 1
                if record is not None:
                    produced.append(record)
                    workbook.records.append(record)
            self._flush(workbook.records, output_json, output_csv)
            return {
                "ok": True,
                "records": _safe(produced),
                "step_names": sorted(seen_step_names, key=str.upper),
                "total_records": len(workbook.records),
            }

        for sheet_name in _read_workbook_sheet_names(path):
            sheet = self._sheet(workbook, sheet_name)
            new_rows, current_row_count = _read_sheet_tail(path, sheet_name, sheet.rows_seen)

            if current_row_count < sheet.rows_seen:
                # The sheet has fewer rows than we previously scored â€” the
                # workbook was truncated or a new recording session reused
                # this filename. Reset and re-read this sheet from scratch
                # so we don't silently skip rows that are "new" relative to
                # the file but look "old" relative to our stale rows_seen.
                sheet.reset()
                new_rows, current_row_count = _read_sheet_tail(path, sheet_name, 0)

            if "STR34_Step_Name" in new_rows.columns:
                for raw in new_rows["STR34_Step_Name"].dropna().astype(str):
                    cleaned = raw.strip()
                    if cleaned and cleaned.upper() != "STR34_STEP_NAME":
                        seen_step_names.add(cleaned)

            if new_rows.empty:
                continue

            for local_index, (_, raw_row) in enumerate(new_rows.iterrows()):
                row_index = sheet.rows_seen + local_index
                record = self._score_row(sheet, raw_row, row_index)
                if record is not None:
                    produced.append(record)
                    workbook.records.append(record)
            sheet.rows_seen = current_row_count

        self._flush(workbook.records, output_json, output_csv)
        return {
            "ok": True,
            "records": _safe(produced),
            "step_names": sorted(seen_step_names, key=str.upper),
            "total_records": len(workbook.records),
        }

    def _score_row(self, session: SheetSession, raw_row: pd.Series, row_index: int):
        e = self.engine
        row, step_name, step_int = e._preprocess_row(raw_row, session.preprocess)
        if row is None:
            return None

        cycle_id, in_cycle = session.tracker.update(step_int)
        if step_int == e.CYCLE_START_STEP and not session.previous_in_cycle:
            session.state.cycle_start_ts = row.get("timestamp", pd.Timestamp.now())

        active_phase, routing_warning = e._resolve_phase(
            step_int, step_name, self.step_to_phase, self.phase_models, self.heating_names
        )
        timestamp = row.get("timestamp", None)
        common = {
            "row_index": row_index,
            "inferred_at": datetime.now().isoformat(timespec="seconds"),
            "timestamp": timestamp,
            "cycle_id": cycle_id,
            "in_cycle": int(in_cycle),
            "STR34_Step": step_int,
            "STR34_Step_Name": step_name,
            "active_phase": active_phase or "NONE",
            "STR34_T100": float(row.get("STR34_T100", np.nan)),
            "STR34_T150": float(row.get("STR34_T150", np.nan)),
            "STR34_P101_P102": float(row.get("STR34_P101_P102", np.nan)),
            "STR34_F0_1": float(row.get("STR34_F0_1", np.nan)),
        }

        if active_phase is None or active_phase not in self.phase_models:
            summary = (
                "Row outside active sterilization cycle â€” no prediction made."
                if not in_cycle
                else (routing_warning or f"Step {step_int} has no trained phase model.")
            )
            record = {
                **common,
                "pred_isolation_forest_prob": None,
                "pred_mahalanobis_prob": None,
                "pred_lstm_ae_prob": None,
                "pred_ensemble_score": None,
                "pred_risk_level": "NOT_IN_CYCLE" if not in_cycle else "NO_MODEL",
                "pred_anomaly": None,
                "anomaly_reasons": [],
                **e._pack_reason_cols([]),
                "pred_anomaly_summary": summary,
            }
        else:
            session.state.maybe_reset_phase_buffer(active_phase)
            prediction = e._predict_row(row, active_phase, self.phase_models, session.state)
            reasons = prediction["reasons"]
            record = {
                **common,
                "pred_isolation_forest_prob": prediction["isolation_forest_prob"],
                "pred_mahalanobis_prob": prediction["mahalanobis_prob"],
                "pred_lstm_ae_prob": prediction["lstm_ae_prob"],
                "pred_ensemble_score": prediction["ensemble_score"],
                "pred_risk_level": prediction["risk_level"],
                "pred_anomaly": prediction["anomaly"],
                "anomaly_reasons": reasons,
                **e._pack_reason_cols(reasons),
                "pred_anomaly_summary": prediction["anomaly_summary"],
            }
        session.previous_in_cycle = in_cycle
        return record

    def _flush(self, records: list[dict], json_path: Path, csv_path: Path):
        json_path.parent.mkdir(parents=True, exist_ok=True)
        csv_path.parent.mkdir(parents=True, exist_ok=True)
        safe_records = _safe(records)
        keyed_records = {}
        for record in safe_records:
            key = record.get("timestamp") or str(record.get("row_index", len(keyed_records)))
            keyed_records[str(key).replace("T", " ")] = record
        json_path.write_text(json.dumps(keyed_records, indent=2, ensure_ascii=False), encoding="utf-8")
        frame = pd.DataFrame(safe_records)
        ordered = [c for c in self.engine.OUTPUT_COLS if c in frame.columns]
        extras = [c for c in frame.columns if c not in ordered and c != "anomaly_reasons"]
        frame[ordered + extras].to_csv(csv_path, index=False)


def _send(payload: dict):
    sys.stdout.write(json.dumps(_safe(payload), ensure_ascii=False, allow_nan=False) + "\n")
    sys.stdout.flush()


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: inference_session_worker.py <03_inference.py> <models>")
    host = Host(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
    _send({"ok": True, "type": "ready"})
    for line in sys.stdin:
        try:
            command = json.loads(line)
            action = command.get("action")
            if action == "prime":
                result = host.prime(command["session"], Path(command["path"]))
            elif action == "score":
                result = host.score(
                    command["session"], Path(command["path"]), bool(command.get("reset")),
                    Path(command["output_json"]), Path(command["output_csv"]),
                    command.get("rows"),
                )
            elif action == "stop":
                _send({"ok": True})
                return
            else:
                raise ValueError(f"Unknown action: {action}")
            _send(result)
        except Exception as exc:
            _send({"ok": False, "error": f"{type(exc).__name__}: {exc}"})


if __name__ == "__main__":
    main()

