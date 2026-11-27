import json
from pathlib import Path

import numpy as np
import pytest

from lookback_ml import SCORER_FORMAT
from lookback_ml.calibration import Platt, lookup
from lookback_ml.data import load_dataset
from lookback_ml.make_fixture import FIXTURE
from lookback_ml.schema import load_schema
from lookback_ml.synth import generate, write
from lookback_ml.train import Options, impute, main, onnx_predict, train

SCHEMA = load_schema()


@pytest.fixture(scope="module")
def trained(tmp_path_factory):
    root = tmp_path_factory.mktemp("datasets")
    write(generate(SCHEMA, games=3, sessions_per_game=6, candidates_per_session=15, seed=3), root)
    # A fourth game with only a few ratings: trained on, but keeps the heuristic.
    write(generate(SCHEMA, games=4, sessions_per_game=1, candidates_per_session=4, seed=4, retractions=0)[-4:], root)
    ds = load_dataset(root, SCHEMA)
    return root, ds, train(ds, Options())


def test_model_beats_the_heuristic_on_held_out_sessions(trained):
    _, _, result = trained
    overall = result.report["overall"]
    assert overall["model"]["auc"] > overall["heuristic"]["auc"]
    assert overall["calibrated"]["log_loss"] < overall["heuristic"]["log_loss"]
    # Per-game calibration fixes the per-game rater bias the pooled model cannot see.
    assert overall["calibrated"]["log_loss"] < overall["model"]["log_loss"]
    assert overall["calibrated"]["brier"] < overall["model"]["brier"]


def test_onnx_matches_scikit_learn(trained):
    _, ds, result = trained
    X = impute(ds.X, np.array(result.manifest["featureMeans"]))
    sk = result.pipe.predict_proba(X.astype(np.float32))[:, 1]
    assert np.max(np.abs(onnx_predict(result.onnx, X) - sk)) < 1e-5


def test_manifest_lists_only_games_with_enough_ratings(trained):
    _, _, result = trained
    m = result.manifest
    assert m["format"] == SCORER_FORMAT
    assert m["featureNames"] == list(SCHEMA.names)
    assert m["games"] == ["synth-a", "synth-b", "synth-c"]
    assert "synth-d" not in m["calibration"]["profiles"]
    assert {"synth-a", "synth-a.mode1", "synth-a.mode2"} <= set(m["calibration"]["profiles"])


def test_cli_trains_and_refuses(tmp_path, trained):
    root, _, _ = trained
    out = tmp_path / "model"
    assert main(["--datasets", str(root), "--out", str(out)]) == 0
    assert (out / "model.onnx").stat().st_size > 0
    assert json.loads((out / "model.json").read_text())["games"]
    assert "precision_at_3" in (out / "report.md").read_text()

    assert main(["--datasets", str(root), "--out", str(tmp_path / "x"), "--min-rows", "100000"]) == 2
    assert not (tmp_path / "x").exists()

    bad = tmp_path / "bad"
    write(generate(SCHEMA, games=1, sessions_per_game=4, candidates_per_session=15), bad)
    with (bad / "synth-a" / "ratings.jsonl").open("a") as fh:
        fh.write(json.dumps(dict(generate(SCHEMA, games=1, sessions_per_game=1, candidates_per_session=1)[0], retention="ephemeral")) + "\n")
    assert main(["--datasets", str(bad), "--out", str(tmp_path / "y")]) == 2
    assert not (tmp_path / "y").exists()


def test_committed_fixture_reproduces_expected_outputs():
    """The same files the C# tests read: ONNX Runtime and the Python calibration must give these numbers."""
    m = json.loads((FIXTURE / "model" / "model.json").read_text())
    expected = json.loads((FIXTURE / "expected.json").read_text())
    onnx = (FIXTURE / "model" / "model.onnx").read_bytes()
    assert m["featureNames"] == list(SCHEMA.names)
    means = np.array(m["featureMeans"])
    glob = Platt(**m["calibration"]["global"])
    table = {k: Platt(**v) for k, v in m["calibration"]["profiles"].items()}
    tol = expected["tolerance"]
    assert any(c["calibrated"] is None for c in expected["cases"])
    for case in expected["cases"]:
        if case["raw"] is None:
            assert case["profile"].split(".")[0] not in m["games"]
            continue
        x = np.array([np.nan if v is None else v for v in case["features"]])
        raw = onnx_predict(onnx, impute(x[None, :], means))[0]
        assert raw == pytest.approx(case["raw"], abs=tol)
        cal = lookup(case["profile"], glob, table).apply(np.array([raw]))[0]
        assert cal == pytest.approx(case["calibrated"], abs=tol)


def test_fixture_can_be_regenerated(tmp_path):
    from lookback_ml.make_fixture import build

    expected = build(tmp_path / "scratch", tmp_path / "fixture")
    assert (tmp_path / "fixture" / "model" / "model.onnx").exists()
    assert len(expected["cases"]) == 7
