# -*- coding: utf-8 -*-
"""Instant Daylight compute. Same pipeline as apps/demo/lib/daylight/index.ts."""

from __future__ import annotations

from collections.abc import Sequence

import numpy as np

from daylight.elements import request_to_elements
from daylight.grid import GridResult, create_grid
from daylight.models import DaylightRequest, GridMeta
from daylight.portals import build_daylight_scene
from daylight.score import normalize_daylight_scores
from daylight.trace import trace_sky_visibility


def grid_to_meta(grid: GridResult) -> GridMeta:
    return GridMeta(
        rows=grid.rows,
        cols=grid.cols,
        cellSizeCm=grid.cell_size_cm,
        originCm=[grid.min_x, grid.min_y],
        maxCm=[grid.max_x, grid.max_y],
        cellCount=grid.rows * grid.cols,
    )


def compute_daylight(elements: Sequence[object], cell_size_cm: float) -> tuple[np.ndarray, int, GridResult]:
    """Trace + normalize. windowCount is portals with source window (same as the demo)."""
    grid = create_grid(elements, cell_size_cm=cell_size_cm)
    scene = build_daylight_scene(elements)
    scores = normalize_daylight_scores(trace_sky_visibility(grid, scene))
    window_count = sum(1 for portal in scene.portals if portal.source == "window")
    return scores, window_count, grid


def compute_daylight_request(req: DaylightRequest) -> tuple[np.ndarray, GridMeta, GridResult]:
    elements = request_to_elements(req)
    scores, _portal_windows, grid = compute_daylight(elements, req.grid.cellSizeCm)
    return scores, grid_to_meta(grid), grid
