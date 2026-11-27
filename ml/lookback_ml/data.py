"""Loading ratings for training, and refusing data that must not be trained on.

Only footage the user has rights to becomes training data. The recorder's
RatingStore already refuses ephemeral sessions; this module checks again and
refuses to train at all if a ratings file holds a row whose retention does not
allow it, because such a row means something upstream went wrong.

The one exception is the user's own ratings from the clipper check, of videos
they only watched (`<game>/check-ratings.jsonl`): the rating is theirs, the
video is not, so a row may hold only the stars and the candidate's numbers. A
row with anything more (a time, a key we do not know, a session id that is not
a random run id) refuses training the same way.
"""

from __future__ import annotations

import json
import math
import re
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .schema import FeatureSchema

RATINGS_FILE = "ratings.jsonl"
CHECK_RATINGS_FILE = "check-ratings.jsonl"
ALLOWED_RETENTION = {"owned", "permissioned", "open-license"}
NEEDS_PERMISSION = {"permissioned", "open-license"}

# A clipper-check row: exactly these keys, times 0, a random run id.
CHECK_DERIVED_WRITE = "user-rating-with-features"
CHECK_KEYS = frozenset({
    "v", "game", "profile", "sessionId", "candidateId", "stars", "label", "retention", "sourceKind",
    "permissionRef", "startQpc", "endQpc", "peakQpc", "qpcFrequency", "heuristicScore",
    "highlightProbability", "featureSchema", "features", "ratedAt", "source", "derivedWrite", "platform",
})
CHECK_TIMES = ("startQpc", "endQpc", "peakQpc", "qpcFrequency")
RUN_ID = re.compile(r"^check-[0-9a-f]{32}$")


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
    origins: np.ndarray = field(default_factory=lambda: np.array([], dtype=object))  # "own" or "check"
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


def _check_check_row(row: dict, where: str) -> str | None:
    """A clipper-check rating may hold the rating and the candidate's numbers, nothing else."""
    extra = sorted(set(row) - CHECK_KEYS)
    if extra:
        return f"{where}: check rating carries {extra}"
    if row.get("derivedWrite") != CHECK_DERIVED_WRITE or row.get("source") != "clipper-check":
        return f"{where}: not a clipper-check rating"
    if row.get("retention") != "ephemeral" or row.get("sourceKind") != "official-player":
        return f"{where}: check rating from a '{row.get('sourceKind')}' / '{row.get('retention')}' session"
    if any(row.get(k) not in (0, None) for k in CHECK_TIMES):
        return f"{where}: check rating carries times"
    if not RUN_ID.match(str(row.get("sessionId") or "")):
        return f"{where}: check rating's session is not a random run id"
    if row.get("platform") not in ("youtube", "twitch"):
        return f"{where}: check rating from platform '{row.get('platform')}'"
    return None


def read_rows(
    dataset_root: Path, games: list[str] | None = None, include_check: bool = True
) -> tuple[list[tuple[str, dict]], int]:
    """Every parseable line of every game's ratings file, with where it came from.

    Reads `<game>/ratings.jsonl` and, unless include_check is False, the clipper
    check's `<game>/check-ratings.jsonl`; each row gets "_origin" "own" or "check".
    Raises RefusedError when any line may not become training data.
    Returns (rows, skipped_lines); unparseable lines (a torn last write) are skipped.
    """
    root = Path(dataset_root)
    files = [(p, False) for p in sorted(root.glob(f"*/{RATINGS_FILE}"))]
    if include_check:
        files += [(p, True) for p in sorted(root.glob(f"*/{CHECK_RATINGS_FILE}"))]
    if games:
        wanted = set(games)
        files = [(f, c) for f, c in files if f.parent.name in wanted]
    rows: list[tuple[str, dict]] = []
    violations: list[str] = []
    skipped = 0
    for path, is_check in files:
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
            problem = _check_check_row(row, where) if is_check else _check_retention(row, where)
            if problem is None and row.get("game") != path.parent.name:
                problem = f"{where}: row for game '{row.get('game')}' in the '{path.parent.name}' folder"
            if problem:
                violations.append(problem)
                continue
            rows.append((where, {**row, "_origin": "check" if is_check else "own"}))
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


def load_dataset(
    dataset_root: Path, schema: FeatureSchema, games: list[str] | None = None, include_check: bool = True
) -> Dataset:
    rows, skipped = read_rows(dataset_root, games, include_check)
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
        origins=np.array([r.get("_origin", "own") for r, _ in keep], dtype=object),
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
