# -*- coding: utf-8 -*-
"""Occupiable daylight grid. 1:1 with apps/demo/lib/grid-engine.ts createGrid."""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any, NamedTuple

import numpy as np

from daylight.geom import Point, point_in_polygon, point_to_segment_dist

DEFAULT_CELL_SIZE_CM = 40.0
ALWAYS_BLOCKED_USAGES = (
    "STAIRWAY_ELEVATOR",
    "TECHNICAL_ROOM",
    "WET_ROOM",
    "LOCKER_ROOM",
)


class GridResult(NamedTuple):
    rows: int
    cols: int
    cell_size_cm: float
    min_x: float
    min_y: float
    max_x: float
    max_y: float
    x_coords: np.ndarray
    y_coords: np.ndarray
    usable_mask: np.ndarray
    usable_cell_count: int


def _as_record(el: object) -> Mapping[str, Any] | None:
    if not isinstance(el, Mapping):
        return None
    return el


def _extract_space_polygon(geo: Mapping[str, Any] | None) -> list[Point] | None:
    if not geo:
        return None
    poly = geo.get("polygon")
    if isinstance(poly, Mapping):
        coords = poly.get("coordinates")
        if isinstance(coords, Sequence) and coords and len(coords[0]) >= 3:
            return [(float(c[0]), float(c[1])) for c in coords[0]]
    if geo.get("type") == "Polygon":
        coords = geo.get("coordinates")
        if isinstance(coords, Sequence) and coords and len(coords[0]) >= 3:
            return [(float(c[0]), float(c[1])) for c in coords[0]]
    return None


def _point_in_rect(px: float, py: float, cx: float, cy: float, half_w: float, half_h: float) -> bool:
    return abs(px - cx) <= half_w and abs(py - cy) <= half_h


def create_grid(
    elements: Sequence[object],
    cell_size_cm: float = DEFAULT_CELL_SIZE_CM,
) -> GridResult:
    walls: list[tuple[float, float, float, float, float]] = []
    columns: list[tuple[float, float, float, float]] = []
    space_polygons: list[list[Point]] = []
    blocked_space_polygons: list[list[Point]] = []

    for el in elements:
        rec = _as_record(el)
        if rec is None:
            continue
        cls = rec.get("_class")
        if cls == "element:wall":
            geo = rec.get("geometry")
            if not isinstance(geo, Mapping) or not geo.get("start") or not geo.get("end"):
                continue
            start = geo["start"]
            end = geo["end"]
            thickness = geo.get("thickness")
            thick = float(thickness) if isinstance(thickness, (int, float)) else 25.0
            walls.append((float(start[0]), float(start[1]), float(end[0]), float(end[1]), thick))
        elif cls == "element:column":
            geo = rec.get("geometry")
            if not isinstance(geo, Mapping):
                continue
            pos = geo.get("position")
            dims = geo.get("dimensions")
            if not pos or not dims or len(pos) < 2 or len(dims) < 2:
                continue
            columns.append((float(pos[0]), float(pos[1]), float(dims[0]), float(dims[1])))
        elif cls == "element:space":
            geo = rec.get("geometry")
            poly = _extract_space_polygon(geo if isinstance(geo, Mapping) else None)
            if not poly or len(poly) < 3:
                continue
            usage_id = str(rec.get("usageId") or rec.get("room_type") or "")
            is_blocked = any(u in usage_id for u in ALWAYS_BLOCKED_USAGES)
            if is_blocked:
                blocked_space_polygons.append(poly)
            space_polygons.append(poly)

    min_x = float("inf")
    min_y = float("inf")
    max_x = float("-inf")
    max_y = float("-inf")
    for poly in space_polygons:
        for x, y in poly:
            if x < min_x:
                min_x = x
            if y < min_y:
                min_y = y
            if x > max_x:
                max_x = x
            if y > max_y:
                max_y = y
    for ax, ay, bx, by, _thick in walls:
        for x, y in ((ax, ay), (bx, by)):
            if x < min_x:
                min_x = x
            if y < min_y:
                min_y = y
            if x > max_x:
                max_x = x
            if y > max_y:
                max_y = y

    if min_x == float("inf"):
        min_x = min_y = 0.0
        max_x = max_y = cell_size_cm

    max_half_thickness = max((w[4] for w in walls), default=0.0) / 2.0
    min_x -= max_half_thickness
    min_y -= max_half_thickness
    max_x += max_half_thickness
    max_y += max_half_thickness

    half = cell_size_cm / 2.0
    x_list: list[float] = []
    y_list: list[float] = []
    x = min_x + half
    while x < max_x:
        x_list.append(x)
        x += cell_size_cm
    y = min_y + half
    while y < max_y:
        y_list.append(y)
        y += cell_size_cm

    cols = len(x_list)
    rows = len(y_list)
    x_coords = np.asarray(x_list, dtype=np.float64)
    y_coords = np.asarray(y_list, dtype=np.float64)
    usable_mask = np.zeros(rows * cols, dtype=np.uint8)
    usable_cell_count = 0

    for row in range(rows):
        py = float(y_coords[row])
        for col in range(cols):
            px = float(x_coords[col])
            inside_floor = False
            for poly in space_polygons:
                if point_in_polygon(px, py, poly):
                    inside_floor = True
                    break
            if not inside_floor:
                continue

            blocked = False
            for ax, ay, bx, by, thick in walls:
                if point_to_segment_dist(px, py, ax, ay, bx, by) <= thick / 2.0:
                    blocked = True
                    break
            if not blocked:
                for cx, cy, w, h in columns:
                    if _point_in_rect(px, py, cx, cy, w / 2.0, h / 2.0):
                        blocked = True
                        break
            if not blocked:
                for poly in blocked_space_polygons:
                    if point_in_polygon(px, py, poly):
                        blocked = True
                        break

            if inside_floor and not blocked:
                usable_mask[row * cols + col] = 1
                usable_cell_count += 1

    return GridResult(
        rows=rows,
        cols=cols,
        cell_size_cm=cell_size_cm,
        min_x=min_x,
        min_y=min_y,
        max_x=max_x,
        max_y=max_y,
        x_coords=x_coords,
        y_coords=y_coords,
        usable_mask=usable_mask,
        usable_cell_count=usable_cell_count,
    )
