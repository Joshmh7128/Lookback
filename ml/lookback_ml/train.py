"""Train, evaluate and export the learned clip scorer.

    python -m lookback_ml.train --datasets ../datasets --out out/model

Model: impute missing features with the training mean, standardise, then an
L2-regularised logistic regression on soft labels ((stars - 1) / 4, fitted
as weighted positive and negative copies of each row). The heuristic score is
one of the features, so with little data the model stays close to today's
ranking. The regularisation strength is chosen by cross-validation that holds
out whole sessions (candidates from one session are not independent). Per-game
and per-profile Platt calibration is fitted on the out-of-fold predictions and
shrunk toward the global calibration.

The evaluation report compares, on held-out sessions, the heuristic baseline,
the raw model and the calibrated model: AUC, log loss, Brier score, expected
calibration error and precision at top-k per session.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

from . import SCORER_FORMAT
from .calibration import Platt, apply_table, fit_table
from .data import Dataset, RefusedError, check_trainable, load_dataset
from .metrics import evaluate, heuristic_probability
from .schema import FeatureSchema, load_schema

REPO = Path(__file__).resolve().parents[2]
C_GRID = (0.01, 0.1, 1.0, 10.0)


@dataclass
class Options:
    min_rows: int = 40
    min_sessions: int = 3
    min_per_class: int = 5
    min_game_rows: int = 10
    strength: float = 20.0
    k: int = 3
    folds: int = 5
    c_grid: tuple[float, ...] = C_GRID


def impute_means(X: np.ndarray) -> np.ndarray:
    with np.errstate(all="ignore"):
        counts = np.sum(np.isfinite(X), axis=0)
        sums = np.nansum(np.where(np.isfinite(X), X, np.nan), axis=0)
        means = np.where(counts > 0, sums / np.maximum(counts, 1), 0.0)
    return means.astype(np.float64)


def impute(X: np.ndarray, means: np.ndarray) -> np.ndarray:
    X = np.array(X, dtype=np.float64, copy=True)
    bad = ~np.isfinite(X)
    X[bad] = np.broadcast_to(means, X.shape)[bad]
    return X


def fit_pipeline(X: np.ndarray, y: np.ndarray, C: float) -> Pipeline:
    """Logistic regression on soft labels: each row once as a positive with weight y, once as a negative with weight 1 - y."""
    n = len(y)
    X2 = np.vstack([X, X])
    t = np.concatenate([np.ones(n, dtype=np.int64), np.zeros(n, dtype=np.int64)])
    w = np.concatenate([y, 1 - y])
    keep = w > 0
    pipe = Pipeline([("scale", StandardScaler()), ("lr", LogisticRegression(C=C, max_iter=5000))])
    pipe.fit(X2[keep].astype(np.float32), t[keep], lr__sample_weight=w[keep])
    return pipe


def predict(pipe: Pipeline, X: np.ndarray) -> np.ndarray:
    return pipe.predict_proba(X.astype(np.float32))[:, 1].astype(np.float64)


def folds_for(ds: Dataset, n_folds: int) -> np.ndarray:
    groups = ds.sessions
    k = min(n_folds, len(set(groups.tolist())))
    fold = np.empty(len(ds), dtype=np.int64)
    for i, (_, test) in enumerate(GroupKFold(n_splits=k).split(ds.X, ds.y, groups)):
        fold[test] = i
    return fold


def out_of_fold(ds: Dataset, fold: np.ndarray, C: float) -> np.ndarray:
    oof = np.empty(len(ds), dtype=np.float64)
    for f in np.unique(fold):
        train, test = fold != f, fold == f
        means = impute_means(ds.X[train])
        pipe = fit_pipeline(impute(ds.X[train], means), ds.y[train], C)
        oof[test] = predict(pipe, impute(ds.X[test], means))
    return oof


def cross_fitted_calibration(ds: Dataset, fold: np.ndarray, oof: np.ndarray, strength: float) -> np.ndarray:
    """Calibrated held-out predictions: each fold calibrated with parameters fitted on the other folds."""
    out = np.empty(len(ds), dtype=np.float64)
    for f in np.unique(fold):
        train, test = fold != f, fold == f
        glob, table = fit_table(oof[train], ds.y[train], ds.profiles[train], strength)
        out[test] = apply_table(oof[test], ds.profiles[test], glob, table)
    return out


def export_onnx(pipe: Pipeline, n_features: int) -> bytes:
    from skl2onnx import convert_sklearn
    from skl2onnx.common.data_types import FloatTensorType

    lr = pipe.named_steps["lr"]
    model = convert_sklearn(
        pipe,
        initial_types=[("features", FloatTensorType([None, n_features]))],
        options={id(lr): {"zipmap": False}},
        target_opset={"": 17, "ai.onnx.ml": 3},
    )
    model.producer_name = "lookback-ml"
    return model.SerializeToString()


def onnx_predict(onnx_bytes: bytes, X: np.ndarray) -> np.ndarray:
    import onnxruntime as ort

    sess = ort.InferenceSession(onnx_bytes, providers=["CPUExecutionProvider"])
    (probs,) = sess.run(["probabilities"], {"features": X.astype(np.float32)})
    return np.asarray(probs, dtype=np.float64)[:, 1]


def _clean(obj):
    if isinstance(obj, float):
        return None if math.isnan(obj) or math.isinf(obj) else round(obj, 6)
    if isinstance(obj, dict):
        return {k: _clean(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_clean(v) for v in obj]
    return obj


@dataclass
class TrainResult:
    pipe: Pipeline
    onnx: bytes
    manifest: dict
    report: dict


def train(ds: Dataset, options: Options) -> TrainResult:
    check_trainable(ds, options.min_rows, options.min_sessions, options.min_per_class)
    fold = folds_for(ds, options.folds)

    # Regularisation by held-out log loss.
    by_c = {}
    for C in options.c_grid:
        by_c[C] = out_of_fold(ds, fold, C)
    losses = {C: evaluate(p, p, ds.y, ds.stars, ds.sessions, options.k)["log_loss"] for C, p in by_c.items()}
    best_c = min(losses, key=lambda c: (losses[c], -c))
    oof = by_c[best_c]
    calibrated = cross_fitted_calibration(ds, fold, oof, options.strength)
    h = ds.heuristic

    def block(mask: np.ndarray) -> dict:
        args = (ds.y[mask], ds.stars[mask], ds.sessions[mask], options.k)
        return {
            "heuristic": evaluate(heuristic_probability(h[mask]), h[mask], *args),
            "model": evaluate(oof[mask], oof[mask], *args),
            "calibrated": evaluate(calibrated[mask], oof[mask], *args),
        }

    everything = np.ones(len(ds), dtype=bool)
    per_game = {g: block(ds.games == g) for g in sorted(set(ds.games.tolist()))}
    game_rows = {g: int((ds.games == g).sum()) for g in per_game}
    covered = sorted(g for g, n in game_rows.items() if n >= options.min_game_rows)

    # Final model on everything; calibration from the out-of-fold predictions.
    means = impute_means(ds.X)
    Xi = impute(ds.X, means)
    pipe = fit_pipeline(Xi, ds.y, best_c)
    glob, table = fit_table(oof, ds.y, ds.profiles, options.strength)
    table = {p: c for p, c in table.items() if p.split(".")[0] in covered}

    onnx = export_onnx(pipe, len(ds.schema))
    parity = float(np.max(np.abs(onnx_predict(onnx, Xi) - predict(pipe, Xi))))
    if parity > 1e-5:
        raise RuntimeError(f"ONNX export does not match scikit-learn (max difference {parity:g})")

    lr = pipe.named_steps["lr"]
    scale = pipe.named_steps["scale"]
    coef_std = dict(zip(ds.schema.names, (float(c) for c in lr.coef_[0])))
    trained_at = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()
    report = _clean(
        {
            "trainedAt": trained_at,
            "rows": len(ds),
            "sessions": len(set(ds.sessions.tolist())),
            "gameRows": game_rows,
            "games": covered,
            "excluded": ds.excluded,
            "skippedLines": ds.skipped_lines,
            "C": best_c,
            "heldOutLogLossByC": {str(c): v for c, v in losses.items()},
            "folds": int(len(np.unique(fold))),
            "k": options.k,
            "onnxMaxDifference": parity,
            "overall": block(everything),
            "perGame": per_game,
            "coefficients": coef_std,
            "intercept": float(lr.intercept_[0]),
            "scalerMean": [float(v) for v in scale.mean_],
            "scalerScale": [float(v) for v in scale.scale_],
        }
    )
    manifest = {
        "format": SCORER_FORMAT,
        "featureSchema": ds.schema.id,
        "featureNames": list(ds.schema.names),
        "featureMeans": [float(v) for v in means],
        "input": "features",
        "output": "probabilities",
        "positiveColumn": 1,
        "games": covered,
        "calibration": {"global": glob.to_json(), "profiles": {p: c.to_json() for p, c in sorted(table.items())}},
        "training": {
            "trainedAt": trained_at,
            "rows": len(ds),
            "sessions": report["sessions"],
            "C": best_c,
            "strength": options.strength,
            "overall": report["overall"],
        },
    }
    return TrainResult(pipe, onnx, manifest, report)


def report_markdown(report: dict) -> str:
    k = report["k"]
    keys = ["auc", "log_loss", "brier", "ece", f"precision_at_{k}"]
    lines = [
        "# Learned clip scorer: evaluation",
        "",
        f"Trained {report['trainedAt']} on {report['rows']} ratings from {report['sessions']} sessions; "
        f"{report['folds']}-fold cross-validation by session; C = {report['C']}.",
        "Held-out numbers. AUC and precision@k count 4-5 stars as highlights and 1-2 stars as not.",
        "",
    ]

    def table(title: str, block: dict) -> None:
        lines.append(f"## {title}")
        lines.append("")
        lines.append("| scorer | " + " | ".join(keys) + " | n |")
        lines.append("|---" * (len(keys) + 2) + "|")
        for name in ("heuristic", "model", "calibrated"):
            m = block[name]
            cells = ["-" if m.get(key) is None else f"{m[key]:.3f}" for key in keys]
            lines.append(f"| {name} | " + " | ".join(cells) + f" | {m['n']} |")
        lines.append("")

    table("Overall", report["overall"])
    for game, block in report["perGame"].items():
        covered = "" if game in report["games"] else " (too few ratings: keeps the heuristic)"
        table(f"{game}{covered}", block)
    lines.append(f"Excluded rows: {report['excluded']}; unreadable lines: {report['skippedLines']}.")
    return "\n".join(lines) + "\n"


def write(result: TrainResult, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    (out / "model.onnx").write_bytes(result.onnx)
    (out / "model.json").write_text(json.dumps(result.manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out / "report.json").write_text(json.dumps(result.report, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out / "report.md").write_text(report_markdown(result.report), encoding="utf-8", newline="\n")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m lookback_ml.train", description=__doc__.split("\n\n")[0])
    ap.add_argument("--datasets", type=Path, default=REPO / "datasets", help="folder with <game>/ratings.jsonl")
    ap.add_argument("--out", type=Path, default=REPO / "ml" / "out" / "model", help="where model.onnx, model.json and the report go")
    ap.add_argument("--games", help="comma-separated games to train on (default: all)")
    ap.add_argument("--schema", type=Path, help="feature schema JSON (default: ml/feature_schema.json)")
    d = Options()
    ap.add_argument("--min-rows", type=int, default=d.min_rows)
    ap.add_argument("--min-sessions", type=int, default=d.min_sessions)
    ap.add_argument("--min-per-class", type=int, default=d.min_per_class)
    ap.add_argument("--min-game-rows", type=int, default=d.min_game_rows, help="games with fewer ratings keep the heuristic")
    ap.add_argument("--strength", type=float, default=d.strength, help="pseudo-ratings pulling a game's calibration toward its parent")
    ap.add_argument("--k", type=int, default=d.k, help="top-k for precision per session")
    args = ap.parse_args(argv)

    schema: FeatureSchema = load_schema(args.schema) if args.schema else load_schema()
    options = Options(
        min_rows=args.min_rows,
        min_sessions=args.min_sessions,
        min_per_class=args.min_per_class,
        min_game_rows=args.min_game_rows,
        strength=args.strength,
        k=args.k,
    )
    try:
        ds = load_dataset(args.datasets, schema, args.games.split(",") if args.games else None)
        result = train(ds, options)
    except RefusedError as e:
        print(str(e), file=sys.stderr)
        return 2
    write(result, args.out)
    print(report_markdown(result.report))
    print(f"model    {args.out / 'model.onnx'}")
    print(f"manifest {args.out / 'model.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
