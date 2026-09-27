# -*- coding: utf-8 -*-
"""2D segment / ray helpers for the daylight portal engine. Units: centimetres.

1:1 with apps/demo/lib/daylight/geom.ts
"""

from __future__ import annotations

import math
from typing import NamedTuple

EPS = 1e-9
PARALLEL_EPS = 1e-8

Point = tuple[float, float]


class Segment(NamedTuple):
    ax: float
    ay: float
    bx: float
    by: float


def hypot2(dx: float, dy: float) -> float:
    return dx * dx + dy * dy


def clamp(v: float, lo: float, hi: float) -> float:
    if v < lo:
        return lo
    if v > hi:
        return hi
    return v


def js_round(n: float) -> int:
    """ES Math.round: nearest integer, ties toward +infinity."""
    return int(math.floor(n + 0.5))


def point_in_polygon(px: float, py: float, ring: list[Point]) -> bool:
    inside = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > py) != (yj > py) and px < ((xj - xi) * (py - yi)) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def point_in_any_polygon(px: float, py: float, rings: list[list[Point]]) -> bool:
    for ring in rings:
        if len(ring) >= 3 and point_in_polygon(px, py, ring):
            return True
    return False


class ProjectParam(NamedTuple):
    t_cm: float
    len: float
    ux: float
    uy: float


def project_param(
    px: float,
    py: float,
    ax: float,
    ay: float,
    bx: float,
    by: float,
) -> ProjectParam:
    """Project P onto segment AB; distance in cm from A along the segment, clamped."""
    dx = bx - ax
    dy = by - ay
    length = math.hypot(dx, dy)
    if length < 1e-6:
        return ProjectParam(t_cm=0, len=0, ux=1, uy=0)
    ux = dx / length
    uy = dy / length
    t_cm = clamp((px - ax) * ux + (py - ay) * uy, 0, length)
    return ProjectParam(t_cm=t_cm, len=length, ux=ux, uy=uy)


def point_on_wall(ax: float, ay: float, ux: float, uy: float, t_cm: float) -> Point:
    return (ax + ux * t_cm, ay + uy * t_cm)


def point_to_segment_dist(
    px: float,
    py: float,
    ax: float,
    ay: float,
    bx: float,
    by: float,
) -> float:
    dx = bx - ax
    dy = by - ay
    len_sq = dx * dx + dy * dy
    if len_sq < 1e-12:
        return math.hypot(px - ax, py - ay)
    t = clamp(((px - ax) * dx + (py - ay) * dy) / len_sq, 0, 1)
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def segment_hits(
    px: float,
    py: float,
    dx: float,
    dy: float,
    ax: float,
    ay: float,
    bx: float,
    by: float,
) -> bool:
    """Closed segment P→P+D (t in (eps, 1-eps)) vs occluder AB (u in [0, 1])."""
    ex = bx - ax
    ey = by - ay
    det = dx * ey - dy * ex
    if abs(det) < PARALLEL_EPS:
        return False

    rx = ax - px
    ry = ay - py
    t = (rx * ey - ry * ex) / det
    u = (rx * dy - ry * dx) / det
    if t <= EPS or t >= 1 - EPS:
        return False
    if u < -EPS or u > 1 + EPS:
        return False
    return True


def aabb_overlap(
    min_x1: float,
    min_y1: float,
    max_x1: float,
    max_y1: float,
    min_x2: float,
    min_y2: float,
    max_x2: float,
    max_y2: float,
    pad: float = 1,
) -> bool:
    return (
        max_x1 >= min_x2 - pad
        and min_x1 <= max_x2 + pad
        and max_y1 >= min_y2 - pad
        and min_y1 <= max_y2 + pad
    )
