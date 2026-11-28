import json
from pathlib import Path

import pytest

from lookback_ml import models
from lookback_ml.models import KEEP, history_dir, promote, rollback, runs_dir, stamp, status, verdict


def _report(h_auc, c_auc, h_loss=0.6, c_loss=0.5):
    return {"overall": {"heuristic": {"auc": h_auc, "log_loss": h_loss}, "calibrated": {"auc": c_auc, "log_loss": c_loss}}}


def _model(folder: Path, trained_at: str, rows: int = 50) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    manifest = {"games": ["cs2"], "training": {"trainedAt": trained_at, "rows": rows, "sessions": 4, "overall": {}}}
    (folder / "model.json").write_text(json.dumps(manifest), encoding="utf-8")
    (folder / "model.onnx").write_bytes(b"onnx")
    return folder


def test_a_model_is_promoted_only_when_it_ranks_and_calibrates_at_least_as_well_as_the_heuristic():
    assert verdict(_report(0.70, 0.75))[0]
    assert verdict(_report(0.70, 0.70))[0]
    assert not verdict(_report(0.70, 0.69))[0]
    assert not verdict(_report(0.70, 0.80, h_loss=0.5, c_loss=0.6))[0]
    ok, reason = verdict(_report(None, 0.8))
    assert not ok and "all highlights or all not" in reason


def test_stamps_sort_by_training_time():
    assert stamp("2026-11-28T07:05:09+00:00") == "20261128-070509"
    assert stamp("") == "unknown"


def test_promotion_keeps_the_replaced_model_and_rollback_restores_it(tmp_path):
    out = tmp_path / "out" / "model"
    first = _model(tmp_path / "run1", "2026-11-28T07:00:00+00:00", rows=40)
    (first / models.VERDICT).write_text("{}", encoding="utf-8")
    assert promote(first, out) is None
    assert not (out / models.VERDICT).exists()  # the verdict stays with the run

    second = _model(tmp_path / "run2", "2026-11-29T07:00:00+00:00", rows=60)
    replaced = promote(second, out)
    assert replaced == history_dir(out) / "20261128-070000"
    assert status(out)["current"]["rows"] == 60
    assert [h["rows"] for h in status(out)["history"]] == [40]

    rollback(out)
    assert status(out)["current"]["rows"] == 40
    assert status(out)["history"] == []
    assert (out.parent / "rolled-back" / "model.json").exists()
    with pytest.raises(FileNotFoundError):
        rollback(out)


def test_history_keeps_the_newest_few(tmp_path):
    out = tmp_path / "out" / "model"
    for day in range(1, KEEP + 4):
        promote(_model(tmp_path / f"run{day}", f"2026-11-{day:02d}T00:00:00+00:00"), out)
    names = sorted(p.name for p in history_dir(out).iterdir())
    assert len(names) == KEEP
    assert names[-1] == f"2026-11-{KEEP + 2:02d}".replace("-", "") + "-000000"


def test_status_of_an_empty_folder(tmp_path):
    out = tmp_path / "out" / "model"
    assert status(out) == {"current": None, "history": [], "lastRun": None}
    assert runs_dir(out) == tmp_path / "out" / "runs" / "latest"
