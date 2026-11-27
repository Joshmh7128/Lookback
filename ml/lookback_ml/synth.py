"""Synthetic clip features and star ratings, for tests and dry runs before real ratings exist.

    python -m lookback_ml.synth --out <scratch>/datasets --games 3

Each candidate has a hidden "highlight quality" q. Some features track q
(heuristic score, loudness, motion, event counts), some are noise, and a few
values are missing. Stars come from q plus a per-game bias, so a pooled
model ranks well but each game needs its own calibration. Rows use exactly the
format the recorder's RatingStore writes (see RatingRecord in C#), with
retention "owned".
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path

import numpy as np

from . import RATING_VERSION
from .data import RATINGS_FILE
from .schema import FeatureSchema, load_schema

FREQUENCY = 10_000_000


def _sigmoid(x):
    return 1 / (1 + np.exp(-x))


def generate(
    schema: FeatureSchema,
    games: int = 3,
    sessions_per_game: int = 6,
    candidates_per_session: int = 15,
    seed: int = 0,
    missing: float = 0.05,
    retractions: int = 2,
) -> list[dict]:
    rng = np.random.default_rng(seed)
    rows: list[dict] = []
    t0 = dt.datetime(2026, 11, 1, tzinfo=dt.timezone.utc)
    for g in range(games):
        game = f"synth-{chr(ord('a') + g)}"
        bias = (-0.8, 0.6, 0.0, 1.0, -0.4)[g % 5]  # how generous the rater is for this game
        for s in range(sessions_per_game):
            session = f"{game}-s{s:02d}"
            offset = 0
            for c in range(candidates_per_session):
                q = rng.normal()
                duration = 6 + 6 * rng.random() + 2 * max(q, 0)
                count = int(rng.poisson(1 + 2 * _sigmoid(q)))
                kinds = max(1, min(count, 1 + int(rng.poisson(0.5)))) if count else 0
                maxc = max(0.0, 0.4 + 0.5 * q + 0.3 * rng.normal())
                f = {
                    "heuristic_score": float(np.clip(_sigmoid(0.9 * q + 0.7 * rng.normal()), 0, 1)),
                    "duration_s": duration,
                    "event_count": count,
                    "event_kinds": kinds,
                    "max_contribution": maxc,
                    "mean_contribution": maxc * (0.6 + 0.4 * rng.random()),
                    "event_density": count / duration,
                    "peak_position": rng.random(),
                    "motion_z": 0.7 * q + rng.normal(),
                    "flash_z": rng.normal(),
                    "red_edge_z": 0.4 * q + rng.normal(),
                    "hud_activity_z": 0.6 * q + rng.normal(),
                    "loudness_z": 0.8 * q + 0.8 * rng.normal(),
                    "onset_rate": max(0.0, 2 + 0.8 * q + rng.normal()),
                    "scene_cuts": max(0.0, 0.1 + 0.05 * rng.normal()),
                    "black_fraction": float(np.clip(0.02 * rng.random(), 0, 1)),
                    "frozen_fraction": float(np.clip(0.03 * rng.random() - 0.05 * q * 0.1, 0, 1)),
                    "silent_fraction": float(np.clip(0.2 - 0.1 * q + 0.1 * rng.normal(), 0, 1)),
                    "video_coverage": 1.0,
                    "audio_coverage": 1.0 if rng.random() > 0.05 else 0.0,
                }
                values = [float(f[name]) for name in schema.names]
                values = [None if rng.random() < missing else round(v, 6) for v in values]
                stars = int(np.clip(np.round(3 + 1.3 * (q + bias) + 0.6 * rng.normal()), 1, 5))
                start = offset + int(rng.integers(5, 60)) * FREQUENCY
                end = start + int(duration * FREQUENCY)
                offset = end
                profile = f"{game}.mode{1 + (c % 2)}" if g % 2 == 0 else game
                rows.append(
                    {
                        "v": RATING_VERSION,
                        "game": game,
                        "profile": profile,
                        "sessionId": session,
                        "candidateId": f"{session}-{c + 1:04d}",
                        "stars": stars,
                        "label": (stars - 1) / 4,
                        "retention": "owned",
                        "sourceKind": "file",
                        "permissionRef": "",
                        "startQpc": start,
                        "endQpc": end,
                        "peakQpc": start + (end - start) // 2,
                        "qpcFrequency": FREQUENCY,
                        "heuristicScore": values[0] if values[0] is not None else f["heuristic_score"],
                        "highlightProbability": f["heuristic_score"],
                        "featureSchema": schema.id,
                        "features": values,
                        "ratedAt": (t0 + dt.timedelta(minutes=len(rows))).isoformat(),
                        "source": "synthetic",
                    }
                )
    # A few changed minds: re-rate then clear, to exercise "last line wins".
    for i in range(min(retractions, len(rows))):
        cleared = dict(rows[i], stars=0, label=None)
        rows.append(cleared)
    return rows


def write(rows: list[dict], datasets: Path) -> list[Path]:
    paths: dict[str, list[dict]] = {}
    for r in rows:
        paths.setdefault(r["game"], []).append(r)
    written = []
    for game, rs in paths.items():
        p = Path(datasets) / game / RATINGS_FILE
        p.parent.mkdir(parents=True, exist_ok=True)
        with p.open("w", encoding="utf-8", newline="\n") as fh:
            for r in rs:
                fh.write(json.dumps(r, separators=(",", ":")) + "\n")
        written.append(p)
    return written


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m lookback_ml.synth", description=__doc__.split("\n\n")[0])
    ap.add_argument("--out", type=Path, required=True, help="a scratch datasets folder (never the real one)")
    ap.add_argument("--games", type=int, default=3)
    ap.add_argument("--sessions", type=int, default=6)
    ap.add_argument("--candidates", type=int, default=15)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args(argv)
    rows = generate(load_schema(), args.games, args.sessions, args.candidates, args.seed)
    for p in write(rows, args.out):
        print(p)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
