import json
from pathlib import Path

import numpy as np
import pytest

from lookback_ml.data import RefusedError, check_trainable, current, load_dataset, read_rows
from lookback_ml.schema import load_schema
from lookback_ml.synth import generate, write

SCHEMA = load_schema()
SAMPLE = Path(__file__).parent / "data" / "rating_sample.jsonl"
CHECK_SAMPLE = Path(__file__).parent / "data" / "check_rating_sample.jsonl"


def _write(root: Path, game: str, rows: list[dict]) -> None:
    p = root / game / "ratings.jsonl"
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("a", encoding="utf-8") as fh:
        for r in rows:
            fh.write(json.dumps(r) + "\n")


def _row(**over) -> dict:
    base = generate(SCHEMA, games=1, sessions_per_game=1, candidates_per_session=1, retractions=0)[0]
    base.update(over)
    return base


def test_schema_has_thirty_unique_features():
    assert SCHEMA.id == "clip-features/1"
    assert len(SCHEMA) == 30
    assert SCHEMA.names[0] == "heuristic_score"


@pytest.mark.parametrize("retention", ["ephemeral", "unspecified", "", None])
def test_refuses_rows_that_may_not_be_trained_on(tmp_path, retention):
    _write(tmp_path, "synth-a", [_row(), _row(candidateId="x", retention=retention)])
    with pytest.raises(RefusedError, match="may not become training data"):
        read_rows(tmp_path)


def test_refuses_permissioned_rows_without_a_permission_ref(tmp_path):
    _write(tmp_path, "synth-a", [_row(retention="permissioned", permissionRef="")])
    with pytest.raises(RefusedError, match="permissionRef"):
        read_rows(tmp_path)


def test_accepts_permissioned_rows_with_a_permission_ref(tmp_path):
    _write(tmp_path, "synth-a", [_row(retention="open-license", permissionRef="licences/cc-by.txt")])
    rows, _ = read_rows(tmp_path)
    assert len(rows) == 1


def test_refuses_a_row_filed_under_another_game(tmp_path):
    _write(tmp_path, "synth-b", [_row()])
    with pytest.raises(RefusedError, match="folder"):
        read_rows(tmp_path)


def test_last_line_wins_and_zero_stars_clears(tmp_path):
    a = _row(candidateId="c1", stars=2)
    _write(tmp_path, "synth-a", [a, dict(a, stars=5), _row(candidateId="c2", stars=4), _row(candidateId="c2", stars=0)])
    rows, _ = read_rows(tmp_path)
    now = current(rows)
    assert [(r["candidateId"], r["stars"]) for r in now] == [("c1", 5)]


def test_skips_torn_lines_and_excludes_rows_without_features(tmp_path):
    _write(tmp_path, "synth-a", [_row(candidateId="c1"), _row(candidateId="c2", featureSchema="", features=[])])
    with (tmp_path / "synth-a" / "ratings.jsonl").open("a", encoding="utf-8") as fh:
        fh.write('{"v":"lookback.rating/1","game":"synth-a","sess')
    ds = load_dataset(tmp_path, SCHEMA)
    assert len(ds) == 1
    assert ds.skipped_lines == 1
    assert ds.excluded["wrong_or_missing_features"] == 1


def test_missing_features_load_as_nan(tmp_path):
    feats = [None] * len(SCHEMA)
    feats[0] = 0.5
    _write(tmp_path, "synth-a", [_row(features=feats)])
    ds = load_dataset(tmp_path, SCHEMA)
    assert ds.X[0, 0] == 0.5
    assert np.isnan(ds.X[0, 1:]).all()


def test_refuses_too_little_or_one_sided_data(tmp_path):
    write(generate(SCHEMA, games=1, sessions_per_game=2, candidates_per_session=5), tmp_path)
    ds = load_dataset(tmp_path, SCHEMA)
    with pytest.raises(RefusedError, match="usable ratings"):
        check_trainable(ds, min_rows=100, min_sessions=1, min_per_class=1)
    with pytest.raises(RefusedError, match="sessions"):
        check_trainable(ds, min_rows=1, min_sessions=3, min_per_class=1)
    ds.y[:] = 1.0
    with pytest.raises(RefusedError, match="of each"):
        check_trainable(ds, min_rows=1, min_sessions=1, min_per_class=1)


def test_reads_the_row_format_csharp_writes(tmp_path):
    # rating_sample.jsonl is the exact line RatingStore writes (a C# test asserts it).
    (tmp_path / "overwatch").mkdir()
    (tmp_path / "overwatch" / "ratings.jsonl").write_bytes(SAMPLE.read_bytes())
    ds = load_dataset(tmp_path, SCHEMA)
    assert len(ds) == 1
    assert ds.stars[0] == 4
    assert ds.y[0] == 0.75
    assert ds.profiles[0] == "overwatch.push"
    assert ds.X[0, 0] == pytest.approx(0.62)
    assert np.isnan(ds.X[0, 2])


def _check_row(**over) -> dict:
    row = json.loads(CHECK_SAMPLE.read_text(encoding="utf-8"))
    row.update(over)
    return row


def _write_check(root: Path, game: str, rows: list[dict]) -> None:
    p = root / game / "check-ratings.jsonl"
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("a", encoding="utf-8") as fh:
        for r in rows:
            fh.write(json.dumps(r) + "\n")


def test_reads_the_check_row_csharp_writes_beside_your_own(tmp_path):
    # check_rating_sample.jsonl is the exact line RatingStore.AppendCheck writes (a C# test asserts it).
    (tmp_path / "cs2").mkdir()
    (tmp_path / "cs2" / "check-ratings.jsonl").write_bytes(CHECK_SAMPLE.read_bytes())
    (tmp_path / "overwatch").mkdir()
    (tmp_path / "overwatch" / "ratings.jsonl").write_bytes(SAMPLE.read_bytes())
    ds = load_dataset(tmp_path, SCHEMA)
    assert len(ds) == 2
    assert sorted(ds.origins.tolist()) == ["check", "own"]
    i = ds.origins.tolist().index("check")
    assert ds.stars[i] == 5 and ds.y[i] == 1.0
    assert ds.profiles[i] == "cs2.competitive"
    assert ds.X[i, SCHEMA.names.index("motion_z")] == pytest.approx(2.5)
    assert np.isnan(ds.X[i, SCHEMA.names.index("loudness_z")])  # no audio stream: missing, not silent

    own_only = load_dataset(tmp_path, SCHEMA, include_check=False)
    assert own_only.origins.tolist() == ["own"]


@pytest.mark.parametrize(
    "over",
    [
        {"startQpc": 123},                      # a time in the video
        {"videoRef": "abc"},                    # anything identifying the video
        {"sessionId": "official-youtube-1a2b"}, # not a random run id
        {"retention": "owned"},
        {"derivedWrite": None},
        {"platform": "vimeo"},
    ],
)
def test_refuses_check_rows_that_carry_more_than_numbers(tmp_path, over):
    _write_check(tmp_path, "cs2", [_check_row(), _check_row(candidateId="c4", **over)])
    with pytest.raises(RefusedError, match="may not become training data"):
        read_rows(tmp_path)


def test_a_check_row_in_the_own_ratings_file_is_refused(tmp_path):
    _write(tmp_path, "cs2", [_check_row()])
    with pytest.raises(RefusedError, match="retention 'ephemeral'"):
        read_rows(tmp_path)
