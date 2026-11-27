"""Loading ratings for training, and refusing data that must not be trained on.

Only footage the user has rights to becomes training data. The recorder's
RatingStore already refuses ephemeral sessions; this module checks again and
refuses to train at all if a ratings file holds a row whose retention does not
allow it, because such a row means something upstream went wrong.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .schema import FeatureSchema

RATINGS_FILE = "ratings.jsonl"
ALLOWED_RETENTION = {"owned", "permissioned", "open-license"}
NEEDS_PERMISSION = {"permissioned", "open-license"}


class RefusedError(Exception):
    """Training refused: the data is not allowed, or not enough to learn from."""


@dataclass
class Dataset:
    """The current ratings, one row per (session, candidate)."""

    schema: FeatureSchema
    X: np.ndarray  # float64 [n, features], NaN for missing
    y: np.ndarray  # soft labels (stars - 1) / 4
    stars: np.ndarray  # int 1..5
    heuristic: np.ndarray  # the clipper's heuristic score
    sessions: np.ndarray  # str, grouping for cross-validation
    games: np.ndarray  # str
    profiles: np.ndarray  # str
    candidates: np.ndarray  # str
    excluded: dict[str, int] = field(default_factory=dict)
    skipped_lines: int = 0

    def __len__(self) -> int:
        return len(self.y)


def label_for(stars: int) -> float:
    if not 1 <= stars <= 5:
        raise ValueError(f"stars {stars} is not 1..5")
    return (stars - 1) / 4.0


def _check_retention(row: dict, where: str) -> str | None:
    retention = row.get("retention")
    if retention not in ALLOWED_RETENTION:
        return f"{where}: retention '{retention}' may not be trained on"
    if retention in NEEDS_PERMISSION and not str(row.get("permissionRef") or "").strip():
        return f"{where}: {retention} footage without a permissionRef"
    return None


def read_rows(dataset_root: Path, games: list[str] | None = None) -> tuple[list[tuple[str, dict]], int]:
    """Every parseable line of every game's ratings file, with where it came from.

    Raises RefusedError when any line's retention does not allow training.
    Returns (rows, skipped_lines); unparseable lines (a torn last write) are skipped.
    """
    root = Path(dataset_root)
    files = sorted(root.glob(f"*/{RATINGS_FILE}"))
    if games:
        wanted = set(games)
        files = [f for f in files if f.parent.name in wanted]
    rows: list[tuple[str, dict]] = []
    violations: list[str] = []
    skipped = 0
    for path in files:
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            if not line.strip():
                continue
            where = f"{path}:{lineno}"
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                skipped += 1
                continue
            if not isinstance(row, dict):
                skipped += 1
                continue
            problem = _check_retention(row, where)
            if problem is None and row.get("game") != path.parent.name:
                problem = f"{where}: row for game '{row.get('game')}' in the '{path.parent.name}' folder"
            if problem:
                violations.append(problem)
                continue
            rows.append((where, row))
    if violations:
        shown = "\n  ".join(violations[:10])
        more = f"\n  ... and {len(violations) - 10} more" if len(violations) > 10 else ""
        raise RefusedError(
            f"refusing to train: {len(violations)} rating rows may not become training data\n  {shown}{more}"
        )
    return rows, skipped


def current(rows: list[tuple[str, dict]]) -> list[dict]:
    """The last row per (session, candidate) wins; stars 0 clears the pair."""
    last: dict[tuple[str, str], dict] = {}
    for _, row in rows:
        last[(row["sessionId"], row["candidateId"])] = row
    return [r for r in last.values() if int(r.get("stars", 0)) > 0]


def _features(row: dict, schema: FeatureSchema) -> np.ndarray | None:
    if row.get("featureSchema") != schema.id:
        return None
    values = row.get("features") or []
    if len(values) != len(schema):
        return None
    out = np.empty(len(schema), dtype=np.float64)
    for i, v in enumerate(values):
        out[i] = math.nan if v is None else float(v)
    return out


def load_dataset(dataset_root: Path, schema: FeatureSchema, games: list[str] | None = None) -> Dataset:
    rows, skipped = read_rows(dataset_root, games)
    excluded = {"wrong_or_missing_features": 0, "bad_stars": 0}
    keep: list[tuple[dict, np.ndarray]] = []
    for row in current(rows):
        stars = row.get("stars")
        if not isinstance(stars, int) or not 1 <= stars <= 5:
            excluded["bad_stars"] += 1
            continue
        f = _features(row, schema)
        if f is None:
            excluded["wrong_or_missing_features"] += 1
            continue
        keep.append((row, f))

    n = len(keep)
    X = np.array([f for _, f in keep], dtype=np.float64).reshape(n, len(schema))
    stars = np.array([r["stars"] for r, _ in keep], dtype=np.int64)
    return Dataset(
        schema=schema,
        X=X,
        y=np.array([label_for(int(s)) for s in stars], dtype=np.float64),
        stars=stars,
        heuristic=np.array([float(r.get("heuristicScore") or 0.0) for r, _ in keep], dtype=np.float64),
        sessions=np.array([r["sessionId"] for r, _ in keep], dtype=object),
        games=np.array([r["game"] for r, _ in keep], dtype=object),
        profiles=np.array([r.get("profile") or r["game"] for r, _ in keep], dtype=object),
        candidates=np.array([r["candidateId"] for r, _ in keep], dtype=object),
        excluded=excluded,
        skipped_lines=skipped,
    )


def check_trainable(ds: Dataset, min_rows: int, min_sessions: int, min_per_class: int) -> None:
    """Refuses data too small or too one-sided to learn from."""
    if len(ds) < min_rows:
        raise RefusedError(f"refusing to train: {len(ds)} usable ratings, need at least {min_rows}")
    sessions = len(set(ds.sessions.tolist()))
    if sessions < min_sessions:
        raise RefusedError(
            f"refusing to train: ratings from {sessions} sessions, need at least {min_sessions} "
            "(cross-validation holds out whole sessions)"
        )
    positives = int((ds.y > 0.5).sum())
    negatives = int((ds.y < 0.5).sum())
    if positives < min_per_class or negatives < min_per_class:
        raise RefusedError(
            f"refusing to train: {positives} ratings of 4-5 stars and {negatives} of 1-2 stars; "
            f"need at least {min_per_class} of each"
        )
