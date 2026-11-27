"""Training for Lookback's learned clip scorer.

Reads the ratings the recorder stored under ``datasets/<game>/ratings.jsonl``,
trains a small logistic regression on the clip features, calibrates it per
game, and exports ``model.onnx`` plus ``model.json`` for
``Lookback.Scoring.LearnedClipScorer``. Everything runs locally.
"""

SCORER_FORMAT = "lookback.scorer/1"
RATING_VERSION = "lookback.rating/1"
