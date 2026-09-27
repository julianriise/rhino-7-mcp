"""Forsk adapter for the vendored Planwire daylight engine.

Rhino scene in mm (the daylight_scene bridge command) -> DaylightRequest in cm
-> coloured cells in mm (the daylight_paint bridge command). Pure Python, so
the MCP tool and the live smokes share it. The tracer itself stays vendored.

Forsk walls are closed outline bands (forsk:path outer ring plus holes), not
centrelines. Each ring edge becomes a thin Planwire wall. Each opening punches
the faces it crosses and gets its own portal wall on the band centreline, so
Planwire classifies it by probing 40 cm either side, as it does upstream.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from daylight.compute import compute_daylight_request
from daylight.geom import point_in_any_polygon, point_to_segment_dist
from daylight.models import MAX_DAYLIGHT_CELLS, DaylightRequest
from daylight.ramp import DAYLIGHT_LEGEND_DISCLAIMER, score_to_rgb

DEFAULT_CELL_MM = 400.0
FLOOR_OFFSET_MM = 50.0
SCOPE = "sky-vis-proxy"
DISCLAIMER = f"Daylight scores are a {DAYLIGHT_LEGEND_DISCLAIMER}: relative 0–1, not lux, not a code check."

# Faces are lines. 1 cm keeps cells off the face without eating the room.
FACE_CM = 1.0
# Same probe distance as daylight.portals.
PROBE_CM = 40.0
PARALLEL_SIN = 0.02
MIN_PIECE_CM = 1.0

LEGEND_TITLE = "Sky visibility (proxy)"
# Same 24 steps the Planwire demo heatmap draws.
LEGEND_STEPS = 24
LEGEND_MIN_MM = 100.0
LEGEND_MAX_MM = 600.0

NO_ROOMS ="No rooms. Draw closed room outlines on A-ROOM and run rooms_from_layer first."
NO_SELECTION = "Select a room marker first (A-ROOM), or run daylight on the whole floor."


class DaylightTargetError(ValueError):
    """The scene has nothing to score. The message says what to do."""


@dataclass
class Face:
    id: str
    ax: float
    ay: float
    bx: float
    by: float
    length: float
    ux: float
    uy: float
    cuts: list[tuple[float, float]] = field(default_factory=list)


@dataclass
class DaylightRun:
    request: DaylightRequest
    scores: object
    grid: object
    spaces: int
    windows: int
    cells: int
    floor_z: float
    notes: list[str]

    def paint_params(self) -> dict:
        """daylight_paint params in mm: one welded mesh and its legend.

        Vertices sit on cell corners and are shared. A vertex takes the mean
        score of the usable cells around it, then the Planwire ramp, so colours
        blend and stay on the ramp. Cells outside rooms have no face.
        """
        grid = self.grid
        cell = grid.cell_size_cm * 10.0
        x0 = float(grid.x_coords[0]) * 10.0 - cell / 2
        y0 = float(grid.y_coords[0]) * 10.0 - cell / 2
        corner_scores: dict[tuple[int, int], list[float]] = {}
        quads = []
        for row in range(grid.rows):
            for col in range(grid.cols):
                idx = row * grid.cols + col
                if not grid.usable_mask[idx]:
                    continue
                corners = [(row, col), (row, col + 1), (row + 1, col + 1), (row + 1, col)]
                for corner in corners:
                    corner_scores.setdefault(corner, []).append(float(self.scores[idx]))
                quads.append(corners)
        index = {corner: n for n, corner in enumerate(sorted(corner_scores))}
        vertices, colors = [], []
        for (row, col), scores in sorted(corner_scores.items()):
            vertices.append([round(x0 + col * cell, 2), round(y0 + row * cell, 2)])
            colors.append(list(score_to_rgb(sum(scores) / len(scores))))
        faces = [[index[c] for c in quad] for quad in quads]
        return {
            "z": self.floor_z + FLOOR_OFFSET_MM,
            "vertices": vertices,
            "colors": colors,
            "faces": faces,
            "legend": legend_layout(vertices),
        }

    def summary(self) -> dict:
        usable = [float(self.scores[i]) for i in range(len(self.scores)) if self.grid.usable_mask[i]]
        return {
            "spaces": self.spaces,
            "windows": self.windows,
            "cells": self.cells,
            "score_max": round(max(usable), 3) if usable else 0.0,
            "score_mean": round(sum(usable) / len(usable), 3) if usable else 0.0,
            "scope": SCOPE,
            "disclaimer": DISCLAIMER,
            "notes": self.notes,
        }


def legend_layout(vertices: list[list[float]]) -> dict:
    """Legend in mm above the overlay's top-left corner, bottom to top:
    disclaimer, then 0 · colour bar · 1, then the title. Text height scales
    with the overlay so it reads at Top-view zoom on a garage and an office."""
    xs = [v[0] for v in vertices] or [0.0]
    ys = [v[1] for v in vertices] or [0.0]
    h = min(max(0.03 * max(max(xs) - min(xs), max(ys) - min(ys)), LEGEND_MIN_MM), LEGEND_MAX_MM)
    x, y = min(xs), max(ys) + h
    bar_y = y + 1.2 * h
    bar_x0, bar_x1 = x + h, x + h + 10 * h
    return {
        "bar": {
            "x0": bar_x0, "y0": bar_y, "x1": bar_x1, "y1": bar_y + 0.8 * h,
            "colors": [list(score_to_rgb(i / LEGEND_STEPS)) for i in range(LEGEND_STEPS + 1)],
        },
        "texts": [
            {"text": DISCLAIMER, "x": x, "y": y, "height": 0.6 * h},
            {"text": "0", "x": x, "y": bar_y, "height": 0.8 * h},
            {"text": "1", "x": bar_x1 + 0.3 * h, "y": bar_y, "height": 0.8 * h},
            {"text": LEGEND_TITLE, "x": x, "y": y + 2.6 * h, "height": h},
        ],
    }


def _cm(point) -> tuple[float, float]:
    # 0.1 mm grid: Rhino noise must not flip a cell that sits exactly on a face.
    return round(float(point[0]) / 10.0, 2), round(float(point[1]) / 10.0, 2)


def _ring_cm(ring) -> list[tuple[float, float]]:
    pts = [_cm(p) for p in ring]
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts = pts[:-1]
    return pts


def _faces(wall: dict) -> list[Face]:
    faces = []
    for ring_index, ring in enumerate(wall.get("rings") or []):
        pts = _ring_cm(ring)
        for i, (a, b) in enumerate(zip(pts, pts[1:] + pts[:1])):
            length = math.hypot(b[0] - a[0], b[1] - a[1])
            if length < 0.01:
                continue
            faces.append(Face(
                id=f"{wall['id']}.{ring_index}.{i}",
                ax=a[0], ay=a[1], bx=b[0], by=b[1], length=length,
                ux=(b[0] - a[0]) / length, uy=(b[1] - a[1]) / length,
            ))
    return faces


@dataclass
class Portal:
    kind: str
    width: float
    mx: float
    my: float
    ux: float
    uy: float


def _place(opening: dict, faces: list[Face], thickness_cm: float) -> Portal | None:
    """Punch the faces this opening crosses. The portal sits midway between them."""
    if not faces:
        return None
    cx, cy = _cm(opening["center"])
    width = float(opening["width"]) / 10.0
    nearest = min(faces, key=lambda f: point_to_segment_dist(cx, cy, f.ax, f.ay, f.bx, f.by))
    ux, uy = nearest.ux, nearest.uy
    nx, ny = -uy, ux
    reach = thickness_cm + 2.0
    crossed = []
    for face in faces:
        if abs(face.ux * uy - face.uy * ux) > PARALLEL_SIN:
            continue
        offset = (face.ax - cx) * nx + (face.ay - cy) * ny
        along = (cx - face.ax) * face.ux + (cy - face.ay) * face.uy
        if abs(offset) <= reach and -width / 2 <= along <= face.length + width / 2:
            crossed.append((face, offset))
    offsets = [offset for _face, offset in crossed]
    if len(offsets) > 1:
        mid = (min(offsets) + max(offsets)) / 2
    else:
        mid = offsets[0] - math.copysign(thickness_cm / 2, offsets[0])
    mx, my = cx + nx * mid, cy + ny * mid
    for face, _offset in crossed:
        t = (mx - face.ax) * face.ux + (my - face.ay) * face.uy
        face.cuts.append((t - width / 2, t + width / 2))
    kind = "WINDOW" if str(opening.get("kind", "")).lower() == "window" else "DOOR"
    return Portal(kind=kind, width=width, mx=mx, my=my, ux=ux, uy=uy)


def _pieces(face: Face) -> list[tuple[str, list[list[float]]]]:
    if not face.cuts:
        return [(face.id, [[face.ax, face.ay], [face.bx, face.by]])]
    cuts = sorted((max(0.0, a), min(face.length, b)) for a, b in face.cuts)
    solid, cursor = [], 0.0
    for a, b in cuts + [(face.length, face.length)]:
        if a - cursor >= MIN_PIECE_CM:
            solid.append((cursor, a))
        cursor = max(cursor, b)
    out = []
    for n, (a, b) in enumerate(solid):
        out.append((f"{face.id}.{n}", [
            [face.ax + face.ux * a, face.ay + face.uy * a],
            [face.ax + face.ux * b, face.ay + face.uy * b],
        ]))
    return out


def _is_interior(portal: Portal, rings: list[list[tuple[float, float]]]) -> bool:
    nx, ny = -portal.uy, portal.ux
    return point_in_any_polygon(portal.mx + nx * PROBE_CM, portal.my + ny * PROBE_CM, rings) and \
        point_in_any_polygon(portal.mx - nx * PROBE_CM, portal.my - ny * PROBE_CM, rings)


def _check_cells(walls: list[dict], spaces: list[dict], cell_cm: float) -> None:
    xs = [p[0] for w in walls for p in w["segments"]] + [p[0] for s in spaces for p in s["floor"]]
    ys = [p[1] for w in walls for p in w["segments"]] + [p[1] for s in spaces for p in s["floor"]]
    cells = (math.ceil((max(xs) - min(xs)) / cell_cm) + 1) * (math.ceil((max(ys) - min(ys)) / cell_cm) + 1)
    if cells > MAX_DAYLIGHT_CELLS:
        raise DaylightTargetError(
            f"Grid of about {cells} cells exceeds {MAX_DAYLIGHT_CELLS}. Use a larger cell size."
        )


def build_request(scene: dict, target: str = "floor", cell_mm: float = DEFAULT_CELL_MM):
    """Return (DaylightRequest, spaces used, floor z mm, notes). Raises DaylightTargetError."""
    rooms = [r for r in scene.get("rooms") or [] if len(_ring_cm(r.get("ring") or [])) >= 3]
    if not rooms:
        raise DaylightTargetError(NO_ROOMS)
    all_rings = [_ring_cm(r["ring"]) for r in rooms]
    if target == "selection":
        picked = set(scene.get("selected_room_ids") or [])
        rooms = [r for r in rooms if r.get("id") in picked]
        if not rooms:
            raise DaylightTargetError(NO_SELECTION)
    elif target != "floor":
        raise DaylightTargetError("target must be floor or selection.")
    if cell_mm <= 0:
        raise DaylightTargetError("cell size must be positive.")

    notes: list[str] = []
    faces_by_wall: dict[str, list[Face]] = {}
    thickness: dict[str, float] = {}
    for wall in scene.get("walls") or []:
        faces_by_wall[wall["id"]] = _faces(wall)
        thickness[wall["id"]] = float(wall.get("thickness") or 200.0) / 10.0

    portals: list[Portal] = []
    for opening in scene.get("openings") or []:
        host = opening.get("host_id")
        portal = _place(opening, faces_by_wall.get(host, []), thickness.get(host, 20.0))
        if portal is None:
            notes.append(f"{opening.get('id')}: host wall {host!r} not found, skipped")
            continue
        # One room: its neighbours are not spaces, so an inner opening would read
        # as facade. Keep it as a hole only; the trace still sees through it.
        if target == "selection" and _is_interior(portal, all_rings):
            continue
        portals.append(portal)

    walls = []
    for faces in faces_by_wall.values():
        for face in faces:
            for piece_id, segments in _pieces(face):
                walls.append({"id": piece_id, "segments": segments, "thickness": FACE_CM})
    openings = []
    for n, portal in enumerate(portals):
        half = portal.width / 2
        wall_id = f"portal.{n}"
        walls.append({
            "id": wall_id,
            "segments": [
                [portal.mx - portal.ux * half, portal.my - portal.uy * half],
                [portal.mx + portal.ux * half, portal.my + portal.uy * half],
            ],
            "thickness": FACE_CM,
        })
        openings.append({"kind": portal.kind, "wallRef": wall_id, "width": portal.width, "t": 0.5})
    spaces = [{"id": r.get("id"), "floor": [list(p) for p in _ring_cm(r["ring"])]} for r in rooms]
    cell_cm = cell_mm / 10.0
    _check_cells(walls, spaces, cell_cm)
    request = DaylightRequest.model_validate({
        "walls": walls,
        "openings": openings,
        "spaces": spaces,
        "grid": {"cellSizeCm": cell_cm},
    })
    floor_z = min(float(r.get("z") or 0.0) for r in rooms)
    return request, len(spaces), floor_z, notes


def run_scene(scene: dict, target: str = "floor", cell_mm: float = DEFAULT_CELL_MM) -> DaylightRun:
    request, spaces, floor_z, notes = build_request(scene, target, cell_mm)
    scores, _meta, grid = compute_daylight_request(request)
    windows = sum(1 for o in request.openings if o.kind == "WINDOW")
    return DaylightRun(
        request=request,
        scores=scores,
        grid=grid,
        spaces=spaces,
        windows=windows,
        cells=int(grid.usable_cell_count),
        floor_z=floor_z,
        notes=notes,
    )
