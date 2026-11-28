"""Which trained scorer the recorder loads: a run is promoted only when it beats
the heuristic on held-out sessions, earlier models are kept, and the last
promotion can be rolled back.

    python -m lookback_ml.models status   [--out out/model]
    python -m lookback_ml.models rollback [--out out/model]

Layout around --out (default ml/out/model):

    out/model/            the model the recorder loads (model.onnx, model.json, report.json, report.md)
    out/history/<stamp>/  models it replaced, newest last; at most KEEP
    out/rolled-back/      the model the last rollback took out
    out/runs/latest/      the last training run, promoted or not: its model, report and verdict.json

`python -m lookback_ml.train --promote` writes the run and promotes it.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_OUT = REPO / "ml" / "out" / "model"
KEEP = 5
VERDICT = "verdict.json"


def history_dir(out: Path) -> Path:
    return out.parent / "history"


def runs_dir(out: Path) -> Path:
    return out.parent / "runs" / "latest"


def stamp(trained_at: str) -> str:
    """'2026-11-28T07:00:00+00:00' -> '20261128-070000'."""
    digits = "".join(ch for ch in trained_at[:19] if ch.isdigit())
    return f"{digits[:8]}-{digits[8:14]}" if len(digits) >= 14 else "unknown"


def verdict(report: dict) -> tuple[bool, str]:
    """Promote only a model that ranks at least as well as the heuristic and is no worse calibrated, on held-out sessions."""
    overall = report["overall"]
    h, c = overall["heuristic"], overall["calibrated"]
    if c.get("auc") is None or h.get("auc") is None:
        return False, "the held-out ratings are all highlights or all not, so the ranking cannot be compared; rate more varied moments"
    if c["auc"] < h["auc"]:
        return False, f"it ranks worse than the heuristic on held-out sessions (AUC {c['auc']:.3f} vs {h['auc']:.3f})"
    if c["log_loss"] > h["log_loss"]:
        return False, f"its probabilities are less reliable than the heuristic's (log loss {c['log_loss']:.3f} vs {h['log_loss']:.3f})"
    return True, (
        f"it beats the heuristic on held-out sessions: AUC {c['auc']:.3f} vs {h['auc']:.3f}, "
        f"log loss {c['log_loss']:.3f} vs {h['log_loss']:.3f}"
    )


def _trained_at(model: Path) -> str:
    try:
        return json.loads((model / "model.json").read_text(encoding="utf-8"))["training"]["trainedAt"]
    except (OSError, ValueError, KeyError, TypeError):
        return ""


def _unique(path: Path) -> Path:
    candidate, n = path, 1
    while candidate.exists():
        n += 1
        candidate = path.with_name(f"{path.name}-{n}")
    return candidate


def promote(run: Path, out: Path) -> Path | None:
    """Make the model in `run` the one the recorder loads; the one it replaces goes to history. Returns where that went."""
    replaced = None
    if (out / "model.json").exists():
        history = history_dir(out)
        history.mkdir(parents=True, exist_ok=True)
        replaced = _unique(history / stamp(_trained_at(out)))
        shutil.move(str(out), str(replaced))
    elif out.exists():
        shutil.rmtree(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(run, out, ignore=shutil.ignore_patterns(VERDICT))
    prune(out)
    return replaced


def prune(out: Path) -> None:
    history = history_dir(out)
    if not history.exists():
        return
    entries = sorted((p for p in history.iterdir() if p.is_dir()), key=lambda p: p.name)
    for old in entries[: max(0, len(entries) - KEEP)]:
        shutil.rmtree(old)


def rollback(out: Path) -> Path:
    """Puts the newest model in history back; the current one goes to out/rolled-back. Returns the restored model's folder."""
    history = history_dir(out)
    entries = sorted((p for p in history.iterdir() if p.is_dir()), key=lambda p: p.name) if history.exists() else []
    if not entries:
        raise FileNotFoundError("there is no earlier model to roll back to")
    previous = entries[-1]
    if out.exists():
        aside = out.parent / "rolled-back"
        if aside.exists():
            shutil.rmtree(aside)
        shutil.move(str(out), str(aside))
    shutil.move(str(previous), str(out))
    return out


def _summary(model: Path) -> dict | None:
    try:
        manifest = json.loads((model / "model.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    training = manifest.get("training", {})
    return {
        "trainedAt": training.get("trainedAt"),
        "rows": training.get("rows"),
        "sessions": training.get("sessions"),
        "games": manifest.get("games", []),
        "overall": training.get("overall"),
    }


def status(out: Path) -> dict:
    history = history_dir(out)
    entries = sorted((p for p in history.iterdir() if p.is_dir()), key=lambda p: p.name) if history.exists() else []
    last = None
    try:
        last = json.loads((runs_dir(out) / VERDICT).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        pass
    return {
        "current": _summary(out),
        "history": [{"name": p.name, **(_summary(p) or {})} for p in reversed(entries)],
        "lastRun": last,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m lookback_ml.models", description=__doc__.split("\n\n")[0])
    ap.add_argument("action", choices=["status", "rollback"])
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="the folder the recorder loads the model from")
    args = ap.parse_args(argv)
    if args.action == "rollback":
        try:
            rollback(args.out)
        except FileNotFoundError as e:
            print(str(e), file=sys.stderr)
            return 2
    print(json.dumps(status(args.out)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
