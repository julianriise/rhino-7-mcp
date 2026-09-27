"""Vendored Planwire daylight: fixture behaviour and TS parity, headless."""

from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from daylight.compute import compute_daylight, compute_daylight_request
from daylight.elements import request_to_elements
from daylight.models import DaylightRequest
from daylight.portals import build_daylight_scene
from daylight.ramp import score_to_rgb

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "daylight"
GOLDENS = FIXTURES / "goldens"


def load(name: str) -> DaylightRequest:
    raw = json.loads((FIXTURES / name).read_text(encoding="utf-8"))
    raw.pop("comment", None)
    return DaylightRequest.model_validate(raw)


def score_near(grid, scores, x: float, y: float) -> float:
    best, best_d = 0.0, math.inf
    for row in range(grid.rows):
        for col in range(grid.cols):
            idx = row * grid.cols + col
            if not grid.usable_mask[idx]:
                continue
            d = (float(grid.x_coords[col]) - x) ** 2 + (float(grid.y_coords[row]) - y) ** 2
            if d < best_d:
                best, best_d = float(scores[idx]), d
    return best


def test_bleed_marks_outer_and_inner_portals():
    scene = build_daylight_scene(request_to_elements(load("bleed.json")))
    assert [p.kind for p in scene.portals].count("outer") == 1
    assert [p.kind for p in scene.portals].count("inner") == 1


def test_bleed_has_no_light_through_the_solid_partition():
    scores, meta, grid = compute_daylight_request(load("bleed.json"))
    assert len(scores) == meta.cellCount == grid.rows * grid.cols
    for i, score in enumerate(scores):
        if not grid.usable_mask[i]:
            assert float(score) == 0.0
    front = score_near(grid, scores, 400, 620)
    through_inner = score_near(grid, scores, 160, 180)
    behind_solid = score_near(grid, scores, 620, 180)
    front_of_solid = score_near(grid, scores, 620, 620)
    assert front > 0.35
    assert through_inner > 0.08
    assert behind_solid < front_of_solid * 0.4
    assert front > through_inner > behind_solid


@pytest.mark.parametrize("name", ["zero-windows.json", "inner-window-only.json"])
def test_no_outer_sky_source_is_dark(name):
    scores, _meta, _grid = compute_daylight_request(load(name))
    assert all(float(s) == 0.0 for s in scores)


def test_outer_only_falls_off_with_distance():
    scores, _meta, grid = compute_daylight_request(load("outer-only.json"))
    near = score_near(grid, scores, 400, 700)
    far = score_near(grid, scores, 400, 80)
    assert near > 0.45
    assert 0 < far and near > far * 1.4


def test_column_footprint_is_unusable():
    scores, _meta, grid = compute_daylight_request(load("columns.json"))
    col = int(min(range(grid.cols), key=lambda c: abs(float(grid.x_coords[c]) - 400)))
    row = int(min(range(grid.rows), key=lambda r: abs(float(grid.y_coords[r]) - 400)))
    idx = row * grid.cols + col
    assert int(grid.usable_mask[idx]) == 0
    assert float(scores[idx]) == 0.0
    assert score_near(grid, scores, 400, 700) > 0.45


@pytest.mark.parametrize("path", sorted(GOLDENS.glob("*.json")), ids=lambda p: p.name)
def test_python_matches_ts_goldens(path):
    golden = json.loads(path.read_text(encoding="utf-8"))
    scores, window_count, grid = compute_daylight(golden["elements"], float(golden["cellSizeCm"]))
    assert (grid.rows, grid.cols, window_count) == (golden["rows"], golden["cols"], golden["windowCount"])
    assert [float(s) for s in scores] == pytest.approx(golden["scores"], abs=1e-5)


def test_ramp_stops_match_planwire():
    assert score_to_rgb(0) == (8, 42, 82)
    assert score_to_rgb(0.32) == (16, 118, 128)
    assert score_to_rgb(0.68) == (232, 154, 28)
    assert score_to_rgb(1) == (255, 248, 230)
