# ml: the learned clip scorer

Trains the model `Lookback.Scoring.LearnedClipScorer` runs: L2 logistic
regression on clip features and 1-5 star content ratings, per-game Platt
calibration, ONNX export, and a held-out evaluation against the heuristic.
Everything runs locally on ratings stored under `datasets/<game>/ratings.jsonl`
(your footage) and `datasets/<game>/check-ratings.jsonl` (your clipper-check
ratings of watched videos: the stars and the candidate's numbers, nothing of
the video; `--no-check-ratings` leaves them out).

```powershell
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m pytest
.venv\Scripts\python -m lookback_ml.train            # --datasets ..\datasets --out out\model
.venv\Scripts\python -m lookback_ml.train --promote  # install it only if it beats the heuristic; keeps the old one
.venv\Scripts\python -m lookback_ml.models rollback  # the previous model again
```

The desktop app's Training tab runs the same commands (set up, train with
`--promote`, roll back) and has the recorder reload the model afterwards.

Full guide: the wiki page `docs/wiki/Clip-Scoring.md`. Training refuses
ratings from sessions that may not be stored (official players, other
ephemeral sessions), check ratings that carry anything but numbers, and data
too small or one-sided to learn from.
