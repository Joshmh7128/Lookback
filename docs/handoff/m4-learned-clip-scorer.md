# Handoff: M4 learned clip scorer (paused)

Status: paused before any code was written. Design only. Nothing here changes behaviour.

## Done
- Read the current scoring path: `CandidateBuilder` (placeholder probability 1 − e^(−score)),
  `ProfileRunner`, `DatasetRunner`/`CandidateScoring`, `RetentionRules`, `events.proto`.
- Found a leftover merge-conflict marker block in `README.md` (Build and test section) on main. Fix it.

## Planned design
1. **New project `src/Lookback.Clipper`** (named in docs/architecture.md):
   - `ClipFeatures`: versioned (`clip-features/1`), game-agnostic vector per candidate: heuristic score,
     duration, event count/kinds, max and mean contribution, density, peak position, session-z-scored
     motion/flash/red-edge/HUD activity/loudness, onset rate, scene cuts, black/frozen/silent fractions,
     video/audio coverage. Schema committed as `ml/feature_schema.json`; a test asserts C# matches it.
   - `SignalWindow`: preallocated rings over a few signals plus Welford baselines (no per-frame allocations).
   - `ClipScoringStage`: bus component subscribing to signals, `game.event`, `clip.candidate`; scores a
     candidate once signal watermarks pass its end; publishes `clip.scored` and `qa.report`.
   - `IClipScorer`: `HeuristicClipScorer` (today's formula, default) and `OnnxClipScorer`
     (Microsoft.ML.OnnxRuntime, `model.onnx` + `model.json` with feature names, version, per-game Platt
     calibration looked up deepest profile first, falling back to the game, then global).
   - `ClipQa`: per-game checks (duration, coverage, black, frozen, silence, peak margin, highlight
     probability ≥ game threshold) from an optional profile `qa` block (thresholds over existing signals only).
2. **Schema** (new field numbers only): `ClipCandidate.features`/`feature_schema`, `QaReport.highlight_probability`,
   new `ClipRating` message as Envelope field 20, topic `clip.rating`. Check other branches for field-number clashes first.
3. **Ratings**: `RatingStore` appends JSON lines to `datasets/<game>/ratings.jsonl` (gitignored), refusing
   anything `RetentionRules.CanPersist` rejects, so official-player sessions never become training data.
   `RatingSink` persists `clip.rating` from the bus (for the Electron rating UI). Testbench `rate <manifest>`
   is an interactive terminal path: 1 to 5 stars per candidate. Stars become soft labels ((stars − 1) / 4).
4. **Training (`ml/`, Python)**: StandardScaler + L2 logistic regression (heuristic score is a feature, so the
   prior is today's ranking), grouped CV by session, per-game Platt calibration shrunk toward global, export with
   skl2onnx. Refuses ephemeral rows and too-small or one-class data. Reports AUC, log loss, Brier, ECE and
   precision at top-k against the heuristic baseline. `dataset` also writes `candidates.jsonl` with features and
   labels from manifest `clips` (IoU ≥ 0.3).
5. **Fixtures and tests**: a synthetic feature generator, plus a tiny ONNX model and expected outputs under
   `tests/Lookback.Clipper.Tests/Fixtures/`, to check that C# inference and calibration match Python. Add an `ml` CI job.
6. **Docs**: a wiki page `Clip-Scoring.md`, then update the Local-Datasets "Planned" table.

## Environment notes
- In a cloud container, `dotnet-install.sh` is blocked. `apt-get install dotnet-sdk-10.0` works on Ubuntu 24.04.
- Python deps: `numpy scikit-learn onnx onnxruntime skl2onnx`.
