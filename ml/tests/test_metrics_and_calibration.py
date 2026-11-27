import numpy as np
import pytest

from lookback_ml.calibration import IDENTITY, Platt, fit_platt, fit_table, logit, lookup, sigmoid
from lookback_ml.metrics import auc, brier, ece, heuristic_probability, log_loss, precision_at_k


def test_auc_perfect_reversed_and_tied():
    stars = np.array([5, 4, 2, 1, 3])
    assert auc(np.array([0.9, 0.8, 0.2, 0.1, 0.5]), stars) == 1.0
    assert auc(np.array([0.1, 0.2, 0.8, 0.9, 0.5]), stars) == 0.0
    assert auc(np.array([0.5, 0.5, 0.5, 0.5, 0.5]), stars) == 0.5
    assert np.isnan(auc(np.array([0.1, 0.2]), np.array([5, 4])))


def test_losses_and_ece():
    y = np.array([0.0, 1.0, 0.5])
    assert brier(y, y) == 0.0
    assert ece(y, y) == 0.0
    assert log_loss(np.array([0.5, 0.5, 0.5]), y) == pytest.approx(np.log(2))
    assert ece(np.array([0.9, 0.9]), np.array([0.0, 0.0])) == pytest.approx(0.9)


def test_precision_at_k_is_per_session():
    scores = np.array([0.9, 0.8, 0.1, 0.9, 0.2, 0.1])
    stars = np.array([5, 1, 5, 5, 4, 1])
    sessions = np.array(["a", "a", "a", "b", "b", "b"], dtype=object)
    assert precision_at_k(scores, stars, sessions, k=1) == 1.0
    assert precision_at_k(scores, stars, sessions, k=2) == pytest.approx((0.5 + 1.0) / 2)


def test_heuristic_probability_keeps_bounded_scores():
    assert list(heuristic_probability(np.array([0.2, 0.9]))) == [0.2, 0.9]
    assert heuristic_probability(np.array([0.0, 2.0]))[1] == pytest.approx(1 - np.exp(-2))


def test_platt_recovers_known_parameters():
    rng = np.random.default_rng(0)
    p = sigmoid(rng.normal(size=20000) * 2)
    y = sigmoid(1.7 * logit(p) - 0.6)  # soft labels from a known calibration
    fit = fit_platt(p, y, strength=1e-6)
    assert fit.a == pytest.approx(1.7, abs=1e-3)
    assert fit.b == pytest.approx(-0.6, abs=1e-3)


def test_few_ratings_stay_near_the_parent():
    p = np.array([0.2, 0.8, 0.6])
    y = np.array([1.0, 0.0, 0.0])  # contrary to the model
    prior = Platt(1.2, 0.1)
    near = fit_platt(p, y, prior, strength=100)
    far = fit_platt(p, y, prior, strength=0.01)
    assert abs(near.a - prior.a) < 0.05 and abs(near.b - prior.b) < 0.05
    assert abs(far.a - prior.a) > abs(near.a - prior.a)


def test_identity_leaves_probabilities_alone():
    p = np.array([0.1, 0.5, 0.9])
    assert IDENTITY.apply(p) == pytest.approx(p)


def test_table_lookup_falls_back_deepest_first():
    rng = np.random.default_rng(1)
    p = sigmoid(rng.normal(size=300))
    y = (rng.random(300) < p).astype(float)
    profiles = np.array(["g.m1"] * 100 + ["g"] * 100 + ["h"] * 100, dtype=object)
    glob, table = fit_table(p, y, profiles, strength=20)
    assert set(table) == {"g", "g.m1", "h"}
    assert lookup("g.m1.x", glob, table) is table["g.m1"]
    assert lookup("g.m2", glob, table) is table["g"]
    assert lookup("zzz", glob, table) is glob
