# ml: the learned clip scorer

Trains the model `Lookback.Scoring.LearnedClipScorer` runs: L2 logistic
regression on clip features and 1-5 star content ratings, per-game Platt
calibration, ONNX export, and a held-out evaluation against the heuristic.
Everything runs locally on ratings stored under `datasets/<game>/ratings.jsonl`.

```powershell
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m pytest
.venv\Scripts\python -m lookback_ml.train            # --datasets ..\datasets --out out\model
```

Full guide: the wiki page `docs/wiki/Clip-Scoring.md`. Training refuses
ratings from sessions that may not be stored (official players, other
ephemeral sessions) and data too small or one-sided to learn from.
