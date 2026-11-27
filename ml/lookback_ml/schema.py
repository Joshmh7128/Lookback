"""The clip feature schema, read from ml/feature_schema.json (the source of truth)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

SCHEMA_PATH = Path(__file__).resolve().parent.parent / "feature_schema.json"


@dataclass(frozen=True)
class FeatureSchema:
    id: str
    names: tuple[str, ...]

    def __len__(self) -> int:
        return len(self.names)

    def index(self, name: str) -> int:
        return self.names.index(name)


def load_schema(path: Path = SCHEMA_PATH) -> FeatureSchema:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    names = tuple(f["name"] for f in data["features"])
    if len(set(names)) != len(names):
        raise ValueError(f"{path}: duplicate feature names")
    return FeatureSchema(id=data["id"], names=names)
