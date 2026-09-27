# -*- coding: utf-8 -*-
"""Adapt the cm DaylightRequest contract onto the TS element records the tracer expects."""

from __future__ import annotations

import math
from typing import Any

from daylight.models import DaylightRequest, Opening, Wall


def _polyline_length(points: list[tuple[float, float]]) -> float:
    total = 0.0
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        total += math.hypot(x1 - x0, y1 - y0)
    return total


def _point_on_polyline(
    points: list[tuple[float, float]],
    dist_cm: float,
) -> tuple[tuple[float, float], int]:
    """Point at dist_cm along the polyline, plus the segment index it lands on."""
    if len(points) < 2:
        p = points[0] if points else (0.0, 0.0)
        return p, 0
    remaining = dist_cm
    last = len(points) - 2
    for i, ((x0, y0), (x1, y1)) in enumerate(zip(points, points[1:])):
        seg = math.hypot(x1 - x0, y1 - y0)
        if i == last or remaining <= seg:
            u = remaining / seg if seg > 1e-12 else 0.0
            return (x0 + (x1 - x0) * u, y0 + (y1 - y0) * u), i
        remaining -= seg
    return points[-1], last


def _wall_points(wall: Wall) -> list[tuple[float, float]]:
    return [(float(p[0]), float(p[1])) for p in wall.segments]


def _opening_position(wall: Wall, opening: Opening) -> tuple[tuple[float, float], str]:
    pts = _wall_points(wall)
    total = _polyline_length(pts)
    t = 0.5 if opening.t is None else float(opening.t)
    pos, seg_i = _point_on_polyline(pts, t * total)
    if len(pts) == 2:
        return pos, wall.id
    return pos, f"{wall.id}#{seg_i}"


def request_to_elements(req: DaylightRequest) -> list[dict[str, Any]]:
    """TS-shaped element list. Polyline walls explode to one segment per edge."""
    elements: list[dict[str, Any]] = []
    walls_by_id = {wall.id: wall for wall in req.walls}

    for wall in req.walls:
        pts = _wall_points(wall)
        if len(pts) == 2:
            elements.append(
                {
                    "id": wall.id,
                    "_class": "element:wall",
                    "geometry": {
                        "start": [pts[0][0], pts[0][1]],
                        "end": [pts[1][0], pts[1][1]],
                        "thickness": float(wall.thickness),
                    },
                }
            )
            continue
        for i, ((x0, y0), (x1, y1)) in enumerate(zip(pts, pts[1:])):
            elements.append(
                {
                    "id": f"{wall.id}#{i}",
                    "_class": "element:wall",
                    "geometry": {
                        "start": [x0, y0],
                        "end": [x1, y1],
                        "thickness": float(wall.thickness),
                    },
                }
            )

    for opening in req.openings:
        wall = walls_by_id[opening.wallRef]
        pos, host_id = _opening_position(wall, opening)
        elements.append(
            {
                "id": opening.id,
                "_class": "element:opening",
                "type": opening.kind,
                "geometry": {
                    "position": [pos[0], pos[1]],
                    "dimensions": [float(opening.width), 20],
                },
                "wall": {"id": host_id},
            }
        )

    for space in req.spaces:
        floor = [[float(p[0]), float(p[1])] for p in space.floor]
        elements.append(
            {
                "id": space.id,
                "_class": "element:space",
                "geometry": {"polygon": {"coordinates": [floor]}},
            }
        )

    for col in req.columns:
        elements.append(
            {
                "id": col.id,
                "_class": "element:column",
                "geometry": {
                    "position": [float(col.center[0]), float(col.center[1])],
                    "dimensions": [float(col.size[0]), float(col.size[1])],
                },
            }
        )

    return elements
