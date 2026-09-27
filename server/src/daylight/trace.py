# -*- coding: utf-8 -*-
"""Direct sky-vis only (no wall bounces). 1:1 with apps/demo/lib/daylight/trace.ts."""

from __future__ import annotations

import math
from typing import NamedTuple

import numpy as np

from daylight.geom import aabb_overlap, point_to_segment_dist, segment_hits
from daylight.grid import GridResult
from daylight.portals import DaylightScene, Portal

HOST_CLEAR_CM = 4
INTERIOR_SIDE_EPS = 2
# 90° of visible sky saturates the 0–1 score (open-plan facade reads bright).
REF_ANGLE = math.pi / 2
INNER_HOP_ATTENUATION = 0.85


class SkySource(NamedTuple):
    portal: Portal
    strength: float
    sky_dir_x: float
    sky_dir_y: float


def _pack_occluders(scene: DaylightScene) -> np.ndarray:
    n = len(scene.occluders)
    out = np.empty(n * 4, dtype=np.float64)
    for i, occ in enumerate(scene.occluders):
        out[i * 4] = occ.ax
        out[i * 4 + 1] = occ.ay
        out[i * 4 + 2] = occ.bx
        out[i * 4 + 3] = occ.by
    return out


def sample_visible(
    px: float,
    py: float,
    sx: float,
    sy: float,
    occ: np.ndarray,
    n_occ: int,
) -> bool:
    dx = sx - px
    dy = sy - py
    min_x = px if px < sx else sx
    min_y = py if py < sy else sy
    max_x = px if px > sx else sx
    max_y = py if py > sy else sy

    for i in range(n_occ):
        ax = float(occ[i * 4])
        ay = float(occ[i * 4 + 1])
        bx = float(occ[i * 4 + 2])
        by = float(occ[i * 4 + 3])
        omin_x = ax if ax < bx else bx
        omin_y = ay if ay < by else by
        omax_x = ax if ax > bx else bx
        omax_y = ay if ay > by else by
        if not aabb_overlap(min_x, min_y, max_x, max_y, omin_x, omin_y, omax_x, omax_y, 2):
            continue
        if point_to_segment_dist(sx, sy, ax, ay, bx, by) < HOST_CLEAR_CM:
            continue
        if point_to_segment_dist(px, py, ax, ay, bx, by) < HOST_CLEAR_CM:
            continue
        if segment_hits(px, py, dx, dy, ax, ay, bx, by):
            return False
    return True


def _portal_angle(px: float, py: float, portal: Portal, vis_frac: float) -> float:
    if vis_frac <= 0:
        return 0.0
    vx = portal.mid_x - px
    vy = portal.mid_y - py
    dist = math.hypot(vx, vy)
    facing = (vx * portal.outward_x + vy * portal.outward_y) / dist if dist > 1e-6 else 1.0
    cos = abs(facing)
    if dist < 8:
        return vis_frac * REF_ANGLE
    apparent_half = (portal.width_cm * vis_frac * max(cos, 0.2)) / 2
    d_angle = 2 * math.atan2(apparent_half, dist)
    return d_angle * (0.35 + 0.65 * cos)


class InnerVis(NamedTuple):
    strength: float
    sky_dir_x: float
    sky_dir_y: float


def _inner_sees_outer(
    inner: Portal,
    outer_portals: list[Portal],
    occ: np.ndarray,
    n_occ: int,
) -> InnerVis | None:
    seeing = 0
    dir_x = 0.0
    dir_y = 0.0
    for sx, sy in inner.samples:
        hit = False
        for outer in outer_portals:
            for ox, oy in outer.samples:
                if not sample_visible(sx, sy, ox, oy, occ, n_occ):
                    continue
                hit = True
                dir_x += ox - sx
                dir_y += oy - sy
                break
            if hit:
                break
        if hit:
            seeing += 1
    if seeing == 0:
        return None
    mag = math.hypot(dir_x, dir_y)
    return InnerVis(
        strength=(seeing / len(inner.samples)) * INNER_HOP_ATTENUATION,
        sky_dir_x=dir_x / mag if mag > 1e-6 else inner.outward_x,
        sky_dir_y=dir_y / mag if mag > 1e-6 else inner.outward_y,
    )


def _build_sky_sources(scene: DaylightScene, occ: np.ndarray, n_occ: int) -> list[SkySource]:
    outer = [p for p in scene.portals if p.kind == "outer"]
    inner = [p for p in scene.portals if p.kind == "inner"]
    sources = [
        SkySource(portal=portal, strength=1, sky_dir_x=portal.outward_x, sky_dir_y=portal.outward_y)
        for portal in outer
    ]
    for portal in inner:
        vis = _inner_sees_outer(portal, outer, occ, n_occ)
        if vis is None or vis.strength < 0.02:
            continue
        sources.append(
            SkySource(
                portal=portal,
                strength=vis.strength,
                sky_dir_x=vis.sky_dir_x,
                sky_dir_y=vis.sky_dir_y,
            )
        )
    return sources


def trace_sky_visibility(grid: GridResult, scene: DaylightScene) -> np.ndarray:
    """Direct sky-vis only. Outer windows are sky; inner openings transmit sky they can see."""
    rows, cols = grid.rows, grid.cols
    scores = np.zeros(rows * cols, dtype=np.float32)
    occ = _pack_occluders(scene)
    n_occ = len(scene.occluders)
    sources = _build_sky_sources(scene, occ, n_occ)
    if not sources:
        return scores

    x_coords = grid.x_coords
    y_coords = grid.y_coords
    usable = grid.usable_mask

    for row in range(rows):
        py = float(y_coords[row])
        for col in range(cols):
            idx = row * cols + col
            if not usable[idx]:
                continue
            px = float(x_coords[col])
            angle_sum = 0.0

            for source in sources:
                portal = source.portal
                strength = source.strength
                if portal.kind == "outer":
                    side = (px - portal.mid_x) * portal.outward_x + (py - portal.mid_y) * portal.outward_y
                    if side > INTERIOR_SIDE_EPS:
                        continue
                else:
                    toward_sky = (
                        (px - portal.mid_x) * source.sky_dir_x + (py - portal.mid_y) * source.sky_dir_y
                    )
                    if toward_sky > INTERIOR_SIDE_EPS:
                        continue

                samples = portal.samples
                visible = 0
                for sx, sy in samples:
                    if sample_visible(px, py, sx, sy, occ, n_occ):
                        visible += 1
                if visible == 0:
                    continue
                angle_sum += _portal_angle(px, py, portal, visible / len(samples)) * strength

            scores[idx] = np.float32(angle_sum)

    return scores
