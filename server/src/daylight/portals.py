# -*- coding: utf-8 -*-
"""Openings become portal segments. Solid wall leftovers (and columns) occlude.

1:1 with apps/demo/lib/daylight/portals.ts
"""

from __future__ import annotations

import math
from collections.abc import Mapping, Sequence
from typing import Any, Literal, NamedTuple

from daylight.geom import (
    Point,
    Segment,
    js_round,
    point_in_any_polygon,
    point_on_wall,
    project_param,
)

MIN_WIDTH_CM = 20
MIN_SOLID_CM = 2
PROBE_CM = 40
SAMPLE_SPACING_CM = 28
SAMPLE_INSET_FRAC = 0.12
MIN_SAMPLES = 3

PortalKind = Literal["outer", "inner"]
PortalSource = Literal["window", "door"]


class Portal(NamedTuple):
    ax: float
    ay: float
    bx: float
    by: float
    kind: PortalKind
    source: PortalSource
    width_cm: float
    mid_x: float
    mid_y: float
    outward_x: float
    outward_y: float
    samples: tuple[Point, ...]


class DaylightScene(NamedTuple):
    occluders: tuple[Segment, ...]
    portals: tuple[Portal, ...]
    floor_rings: tuple[tuple[Point, ...], ...]


class WallRec(NamedTuple):
    id: str
    ax: float
    ay: float
    bx: float
    by: float


class OpeningRec(NamedTuple):
    type: str
    wall_id: str | None
    px: float
    py: float
    width_cm: float
    rotation_deg: float | None


class ColumnRec(NamedTuple):
    cx: float
    cy: float
    hw: float
    hh: float


def _as_record(el: object) -> Mapping[str, Any] | None:
    if not isinstance(el, Mapping):
        return None
    return el


def _extract_ring(geo: Mapping[str, Any] | None) -> list[Point] | None:
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


def _parse_elements(elements: Sequence[object]) -> tuple[
    list[WallRec],
    list[OpeningRec],
    list[list[Point]],
    list[ColumnRec],
]:
    walls: list[WallRec] = []
    openings: list[OpeningRec] = []
    floor_rings: list[list[Point]] = []
    columns: list[ColumnRec] = []

    for el in elements:
        rec = _as_record(el)
        if rec is None:
            continue
        cls = rec.get("_class")
        if cls == "element:wall":
            geo = rec.get("geometry")
            if not isinstance(geo, Mapping) or not geo.get("start") or not geo.get("end"):
                continue
            if not isinstance(rec.get("id"), str):
                continue
            start = geo["start"]
            end = geo["end"]
            walls.append(
                WallRec(
                    id=rec["id"],
                    ax=float(start[0]),
                    ay=float(start[1]),
                    bx=float(end[0]),
                    by=float(end[1]),
                )
            )
        elif cls == "element:opening":
            geo = rec.get("geometry")
            if not isinstance(geo, Mapping):
                continue
            pos = geo.get("position")
            dims = geo.get("dimensions")
            if not pos or len(pos) < 2:
                continue
            width_cm = float(dims[0]) if dims else 0.0
            typ = str(rec.get("type") or "").upper()
            wall = rec.get("wall")
            wall_id = wall.get("id") if isinstance(wall, Mapping) else None
            rot = geo.get("rotation")
            openings.append(
                OpeningRec(
                    type=typ,
                    wall_id=wall_id if isinstance(wall_id, str) else None,
                    px=float(pos[0]),
                    py=float(pos[1]),
                    width_cm=width_cm,
                    rotation_deg=float(rot) if isinstance(rot, (int, float)) and math.isfinite(rot) else None,
                )
            )
        elif cls == "element:space":
            geo = rec.get("geometry")
            ring = _extract_ring(geo if isinstance(geo, Mapping) else None)
            if ring:
                floor_rings.append(ring)
        elif cls == "element:column":
            geo = rec.get("geometry")
            if not isinstance(geo, Mapping):
                continue
            pos = geo.get("position")
            dims = geo.get("dimensions")
            if not pos or not dims or len(pos) < 2 or len(dims) < 2:
                continue
            columns.append(
                ColumnRec(
                    cx=float(pos[0]),
                    cy=float(pos[1]),
                    hw=float(dims[0]) / 2,
                    hh=float(dims[1]) / 2,
                )
            )

    return walls, openings, floor_rings, columns


class Cut(NamedTuple):
    t0: float
    t1: float


def _merge_intervals(cuts: list[Cut]) -> list[Cut]:
    if not cuts:
        return []
    sorted_cuts = sorted(cuts, key=lambda c: c.t0)
    out = [Cut(sorted_cuts[0].t0, sorted_cuts[0].t1)]
    for i in range(1, len(sorted_cuts)):
        last = out[-1]
        nxt = sorted_cuts[i]
        if nxt.t0 <= last.t1 + 1:
            out[-1] = Cut(last.t0, max(last.t1, nxt.t1))
        else:
            out.append(Cut(nxt.t0, nxt.t1))
    return out


def _punch_wall(wall: WallRec, cuts: list[Cut]) -> list[Segment]:
    dx = wall.bx - wall.ax
    dy = wall.by - wall.ay
    length = math.hypot(dx, dy)
    if length < MIN_SOLID_CM:
        return []
    ux = dx / length
    uy = dy / length
    clamped: list[Cut] = []
    for cut in cuts:
        t0 = max(0.0, min(length, cut.t0))
        t1 = max(0.0, min(length, cut.t1))
        if t1 - t0 > 1:
            clamped.append(Cut(t0, t1))
    merged = _merge_intervals(clamped)
    solids: list[Segment] = []
    cursor = 0.0
    for m in merged:
        if m.t0 - cursor >= MIN_SOLID_CM:
            a = point_on_wall(wall.ax, wall.ay, ux, uy, cursor)
            b = point_on_wall(wall.ax, wall.ay, ux, uy, m.t0)
            solids.append(Segment(ax=a[0], ay=a[1], bx=b[0], by=b[1]))
        cursor = m.t1
    if length - cursor >= MIN_SOLID_CM:
        a = point_on_wall(wall.ax, wall.ay, ux, uy, cursor)
        b = point_on_wall(wall.ax, wall.ay, ux, uy, length)
        solids.append(Segment(ax=a[0], ay=a[1], bx=b[0], by=b[1]))
    return solids


class OpeningFrame(NamedTuple):
    ax: float
    ay: float
    bx: float
    by: float
    nx: float
    ny: float
    width_cm: float


def _opening_frame(opening: OpeningRec, wall_by_id: dict[str, WallRec]) -> OpeningFrame | None:
    if opening.width_cm < MIN_WIDTH_CM:
        return None

    host = wall_by_id.get(opening.wall_id) if opening.wall_id else None
    if host:
        proj = project_param(opening.px, opening.py, host.ax, host.ay, host.bx, host.by)
        ux, uy, t_cm, length = proj.ux, proj.uy, proj.t_cm, proj.len
        ax, ay, bx, by = host.ax, host.ay, host.bx, host.by
    else:
        rot = ((opening.rotation_deg or 0) * math.pi) / 180
        ux = math.cos(rot)
        uy = math.sin(rot)
        t_cm = opening.width_cm / 2
        length = opening.width_cm
        ax = opening.px - ux * t_cm
        ay = opening.py - uy * t_cm
        bx = opening.px + ux * t_cm
        by = opening.py + uy * t_cm

    if length < 1e-6:
        return None
    half = opening.width_cm / 2
    t0 = max(0.0, t_cm - half)
    t1 = min(length, t_cm + half)
    if t1 - t0 < MIN_WIDTH_CM * 0.5:
        return None
    a = point_on_wall(ax, ay, ux, uy, t0)
    b = point_on_wall(ax, ay, ux, uy, t1)
    return OpeningFrame(ax=a[0], ay=a[1], bx=b[0], by=b[1], nx=-uy, ny=ux, width_cm=t1 - t0)


def _sample_portal(ax: float, ay: float, bx: float, by: float, width_cm: float) -> tuple[Point, ...]:
    inset = min(width_cm * SAMPLE_INSET_FRAC, 12)
    dx = bx - ax
    dy = by - ay
    length = math.hypot(dx, dy)
    if length < 1e-6:
        return (((ax + bx) / 2, (ay + by) / 2),)
    usable = max(length - 2 * inset, length * 0.5)
    n = max(MIN_SAMPLES, js_round(usable / SAMPLE_SPACING_CM))
    samples: list[Point] = []
    for i in range(n):
        u = 0.5 if n == 1 else inset / length + (i / (n - 1)) * (usable / length)
        samples.append((ax + dx * u, ay + dy * u))
    return tuple(samples)


class ClassifiedPortal(NamedTuple):
    kind: PortalKind
    outward_x: float
    outward_y: float


def _classify_portal(
    mid_x: float,
    mid_y: float,
    nx: float,
    ny: float,
    floor_rings: list[list[Point]],
) -> ClassifiedPortal:
    p1x = mid_x + nx * PROBE_CM
    p1y = mid_y + ny * PROBE_CM
    p2x = mid_x - nx * PROBE_CM
    p2y = mid_y - ny * PROBE_CM
    in1 = point_in_any_polygon(p1x, p1y, floor_rings)
    in2 = point_in_any_polygon(p2x, p2y, floor_rings)

    if in1 and not in2:
        return ClassifiedPortal(kind="outer", outward_x=-nx, outward_y=-ny)
    if in2 and not in1:
        return ClassifiedPortal(kind="outer", outward_x=nx, outward_y=ny)
    if in1 and in2:
        return ClassifiedPortal(kind="inner", outward_x=nx, outward_y=ny)
    return ClassifiedPortal(kind="outer", outward_x=nx, outward_y=ny)


def _column_box(cx: float, cy: float, hw: float, hh: float) -> list[Segment]:
    x0, x1, y0, y1 = cx - hw, cx + hw, cy - hh, cy + hh
    return [
        Segment(ax=x0, ay=y0, bx=x1, by=y0),
        Segment(ax=x1, ay=y0, bx=x1, by=y1),
        Segment(ax=x1, ay=y1, bx=x0, by=y1),
        Segment(ax=x0, ay=y1, bx=x0, by=y0),
    ]


def build_daylight_scene(elements: Sequence[object]) -> DaylightScene:
    """Openings become portal segments. Space dividers are ignored — they are not walls."""
    walls, openings, floor_rings, columns = _parse_elements(elements)
    wall_by_id = {w.id: w for w in walls}
    cuts_by_wall: dict[str, list[Cut]] = {}

    portals: list[Portal] = []

    for opening in openings:
        is_window = opening.type == "WINDOW"
        is_door = opening.type == "DOOR" or opening.type.endswith("DOOR")
        if not is_window and not is_door:
            continue

        frame = _opening_frame(opening, wall_by_id)
        if frame is None:
            continue

        if opening.wall_id:
            host = wall_by_id.get(opening.wall_id)
            if host is not None:
                proj = project_param(opening.px, opening.py, host.ax, host.ay, host.bx, host.by)
                half = opening.width_cm / 2
                cuts_by_wall.setdefault(opening.wall_id, []).append(
                    Cut(t0=proj.t_cm - half, t1=proj.t_cm + half)
                )

        # Sky sources are outer windows (and outer doors). Inner openings are holes only.
        mid_x = (frame.ax + frame.bx) / 2
        mid_y = (frame.ay + frame.by) / 2
        classified = _classify_portal(mid_x, mid_y, frame.nx, frame.ny, floor_rings)
        if is_window or classified.kind == "outer":
            portals.append(
                Portal(
                    ax=frame.ax,
                    ay=frame.ay,
                    bx=frame.bx,
                    by=frame.by,
                    kind=classified.kind,
                    source="window" if is_window else "door",
                    width_cm=frame.width_cm,
                    mid_x=mid_x,
                    mid_y=mid_y,
                    outward_x=classified.outward_x,
                    outward_y=classified.outward_y,
                    samples=_sample_portal(frame.ax, frame.ay, frame.bx, frame.by, frame.width_cm),
                )
            )

    occluders: list[Segment] = []
    for wall in walls:
        occluders.extend(_punch_wall(wall, cuts_by_wall.get(wall.id, [])))
    for col in columns:
        occluders.extend(_column_box(col.cx, col.cy, col.hw, col.hh))

    return DaylightScene(
        occluders=tuple(occluders),
        portals=tuple(portals),
        floor_rings=tuple(tuple(ring) for ring in floor_rings),
    )
