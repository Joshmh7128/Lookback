"""Evaluation metrics. Labels are soft ((stars - 1) / 4); ranking metrics use
4-5 stars as positive and 1-2 stars as negative, leaving 3 stars out."""

from __future__ import annotations

import math

import numpy as np

EPS = 1e-12


def auc(scores: np.ndarray, stars: np.ndarray) -> float:
    """Probability a 4-5 star candidate outranks a 1-2 star one (ties count half)."""
    scores = np.asarray(scores, dtype=np.float64)
    stars = np.asarray(stars)
    pos = scores[stars >= 4]
    neg = scores[stars <= 2]
    if len(pos) == 0 or len(neg) == 0:
        return math.nan
    order = np.argsort(np.concatenate([pos, neg]), kind="mergesort")
    allv = np.concatenate([pos, neg])[order]
    ranks = np.empty(len(allv), dtype=np.float64)
    i = 0
    while i < len(allv):
        j = i
        while j + 1 < len(allv) and allv[j + 1] == allv[i]:
            j += 1
        ranks[i : j + 1] = (i + j) / 2.0 + 1
        i = j + 1
    r = np.empty_like(ranks)
    r[order] = ranks
    rank_pos = r[: len(pos)].sum()
    return float((rank_pos - len(pos) * (len(pos) + 1) / 2.0) / (len(pos) * len(neg)))


def log_loss(p: np.ndarray, y: np.ndarray) -> float:
    p = np.clip(np.asarray(p, dtype=np.float64), EPS, 1 - EPS)
    y = np.asarray(y, dtype=np.float64)
    return float(-np.mean(y * np.log(p) + (1 - y) * np.log(1 - p)))


def brier(p: np.ndarray, y: np.ndarray) -> float:
    return float(np.mean((np.asarray(p, dtype=np.float64) - np.asarray(y, dtype=np.float64)) ** 2))


def ece(p: np.ndarray, y: np.ndarray, bins: int = 10) -> float:
    """Expected calibration error over equal-width bins of predicted probability."""
    p = np.asarray(p, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    if len(p) == 0:
        return math.nan
    idx = np.minimum((p * bins).astype(int), bins - 1)
    total = 0.0
    for b in range(bins):
        m = idx == b
        if m.any():
            total += m.sum() * abs(p[m].mean() - y[m].mean())
    return float(total / len(p))


def precision_at_k(scores: np.ndarray, stars: np.ndarray, sessions: np.ndarray, k: int = 3) -> float:
    """Per session, the share of its top-k candidates rated 4-5 stars; averaged over sessions."""
    scores = np.asarray(scores, dtype=np.float64)
    stars = np.asarray(stars)
    sessions = np.asarray(sessions, dtype=object)
    values = []
    for s in sorted(set(sessions.tolist())):
        m = sessions == s
        order = np.argsort(-scores[m], kind="mergesort")[:k]
        values.append(float((stars[m][order] >= 4).mean()))
    return float(np.mean(values)) if values else math.nan


def heuristic_probability(h: np.ndarray) -> np.ndarray:
    """The heuristic as a probability: as-is when already 0..1 (the clipper's), else 1 - e^-score."""
    h = np.asarray(h, dtype=np.float64)
    if len(h) and h.min() >= 0 and h.max() <= 1:
        return h
    return 1 - np.exp(-np.maximum(h, 0))


def evaluate(p: np.ndarray, rank_scores: np.ndarray, y: np.ndarray, stars: np.ndarray, sessions: np.ndarray, k: int) -> dict:
    return {
        "auc": auc(rank_scores, stars),
        "log_loss": log_loss(p, y),
        "brier": brier(p, y),
        "ece": ece(p, y),
        f"precision_at_{k}": precision_at_k(rank_scores, stars, sessions, k),
        "n": int(len(y)),
    }
