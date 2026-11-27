"""Platt calibration per game and profile, shrunk toward the parent.

p = sigmoid(a * logit(p_model) + b). Each level is a MAP fit with a Gaussian
prior centred on its parent's (a, b): the global fit is pulled toward the
identity (1, 0), a game toward the global fit, a sub-game or mode toward its
game. ``strength`` acts like that many pseudo-ratings, so a game with a
handful of ratings stays close to the global calibration and one with
hundreds gets its own. Must match ``PlattCalibration`` in C#.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

EPSILON = 1e-6


@dataclass(frozen=True)
class Platt:
    a: float
    b: float
    n: int = 0

    def apply(self, p: np.ndarray) -> np.ndarray:
        return sigmoid(self.a * logit(p) + self.b)

    def to_json(self) -> dict:
        return {"a": float(self.a), "b": float(self.b), "n": int(self.n)}


IDENTITY = Platt(1.0, 0.0)


def logit(p) -> np.ndarray:
    p = np.clip(np.asarray(p, dtype=np.float64), EPSILON, 1 - EPSILON)
    return np.log(p / (1 - p))


def sigmoid(z) -> np.ndarray:
    z = np.asarray(z, dtype=np.float64)
    return 1.0 / (1.0 + np.exp(-z))


def fit_platt(p_model: np.ndarray, y: np.ndarray, prior: Platt = IDENTITY, strength: float = 1.0) -> Platt:
    """MAP fit of (a, b) with soft labels y in 0..1 and an L2 pull toward ``prior``."""
    z = logit(p_model)
    y = np.asarray(y, dtype=np.float64)
    n = len(y)
    if n == 0:
        return Platt(prior.a, prior.b, 0)
    theta = np.array([prior.a, prior.b], dtype=np.float64)
    theta0 = theta.copy()
    lam = float(strength)
    Z = np.column_stack([z, np.ones(n)])
    for _ in range(100):
        s = sigmoid(Z @ theta)
        grad = Z.T @ (s - y) + lam * (theta - theta0)
        w = s * (1 - s)
        hess = (Z * w[:, None]).T @ Z + lam * np.eye(2) + 1e-9 * np.eye(2)
        step = np.linalg.solve(hess, grad)
        theta -= step
        if np.max(np.abs(step)) < 1e-10:
            break
    return Platt(float(theta[0]), float(theta[1]), n)


def parents(profile: str) -> list[str]:
    """``a.b.c`` -> ``["a", "a.b", "a.b.c"]``."""
    parts = profile.split(".")
    return [".".join(parts[: i + 1]) for i in range(len(parts))]


def fit_table(
    p_model: np.ndarray,
    y: np.ndarray,
    profiles: np.ndarray,
    strength: float,
    min_profile_rows: int = 1,
) -> tuple[Platt, dict[str, Platt]]:
    """Global calibration plus one per game and per deeper profile seen in the data."""
    p_model = np.asarray(p_model, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    profiles = np.asarray(profiles, dtype=object)
    glob = fit_platt(p_model, y, IDENTITY, strength=1.0)
    table: dict[str, Platt] = {}
    levels = sorted({lvl for p in profiles for lvl in parents(str(p))}, key=lambda s: (s.count("."), s))
    for level in levels:
        mask = np.array([str(p) == level or str(p).startswith(level + ".") for p in profiles])
        if mask.sum() < min_profile_rows:
            continue
        parent_id = level.rsplit(".", 1)[0] if "." in level else None
        parent = table.get(parent_id, glob) if parent_id else glob
        table[level] = fit_platt(p_model[mask], y[mask], parent, strength)
    return glob, table


def lookup(profile: str, glob: Platt, table: dict[str, Platt]) -> Platt:
    """Deepest listed profile first, then the game, then global (as the C# scorer does)."""
    for level in reversed(parents(profile)):
        if level in table:
            return table[level]
    return glob


def apply_table(p_model: np.ndarray, profiles: np.ndarray, glob: Platt, table: dict[str, Platt]) -> np.ndarray:
    out = np.empty(len(p_model), dtype=np.float64)
    for i, (p, prof) in enumerate(zip(p_model, profiles)):
        out[i] = lookup(str(prof), glob, table).apply(np.array([p]))[0]
    return out
