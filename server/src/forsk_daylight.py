"""Forsk adapter for the vendored Planwire daylight engine.

Rhino scene in mm (the daylight_scene bridge command) -> DaylightRequest in cm
-> one coloured room mesh in mm (the daylight_paint bridge command). Pure Python, so
the MCP tool and the live smokes share it. The tracer itself stays vendored.

Forsk walls are closed outline bands (forsk:path outer ring plus holes), not
centrelines. Each ring edge becomes a thin Planwire wall. Each opening punches
the faces it crosses and gets its own portal wall on the band centreline, so
Planwire classifies it by probing 40 cm either side, as it does upstream.
"""

from __future__ import annotations

import json
import math
import sys
import time
from dataclasses import dataclass, field

from daylight.compute import compute_daylight_request
from daylight.geom import point_in_any_polygon, point_to_segment_dist
from daylight.models import MAX_DAYLIGHT_CELLS, DaylightRequest
from daylight.ramp import DAYLIGHT_LEGEND_DISCLAIMER, RampStop

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
# Mesh: outline vertices closer than this merge; faces smaller than this drop.
MESH_EPS_MM = 0.01
MIN_FACE_AREA2 = 1e-3

# Forsk display ramp (F4.2): dark blue to near white, so it reads on the grey
# viewport. Planwire's scores and its own ramp are unchanged.
RAMP = "sky"
SKY_STOPS: tuple[RampStop, ...] = (
    RampStop(0.0, 0x0B, 0x25, 0x45),
    RampStop(0.33, 0x2F, 0x66, 0x90),
    RampStop(0.66, 0x8F, 0xBC, 0xE6),
    RampStop(1.0, 0xF2, 0xF8, 0xFD),
)

NO_ROOMS = "No rooms. Run rooms_detect (Make rooms) to find them from the walls, or draw closed outlines on A-ROOM."
NO_WINDOWS = "No windows. Daylight comes in through windows: add one with add_opening, then run daylight again."
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

    def rings_mm(self) -> list[list[tuple[float, float]]]:
        """The space outlines the tracer used (inner wall faces), in mm."""
        return [[(x * 10.0, y * 10.0) for x, y in space.floor] for space in self.request.spaces]

    def paint_params(self) -> dict:
        """daylight_paint params in mm: one welded mesh that fills each room.

        Each room outline is cut into convex pieces, and each piece is clipped
        to the squares between cell centres. Interior vertices sit on cell
        centres and take that cell's score. Outline vertices take the score of
        the nearest usable cell in the same room. The mesh stops on the inner
        wall faces: no gap, no overlap onto walls.
        """
        grid = self.grid
        cell = grid.cell_size_cm * 10.0
        x0 = float(grid.x_coords[0]) * 10.0
        y0 = float(grid.y_coords[0]) * 10.0
        polys = []
        for ring in self.rings_mm():
            cells = self._room_cells(ring)
            if not cells:
                continue
            for piece in _convex_pieces(ring):
                for poly in _lattice_clip(piece, x0, y0, cell):
                    keys = _keys(poly)
                    if keys:
                        polys.append((keys, cells))
        _split_t_junctions([keys for keys, _cells in polys], x0, y0, cell)
        index: dict[tuple[float, float], int] = {}
        vertices, colors, faces = [], [], []
        for keys, cells in polys:
            for face in _faces_of(keys):
                ids = []
                for key in face:
                    if key not in index:
                        index[key] = len(vertices)
                        vertices.append(list(key))
                        colors.append(list(display_rgb(self._score_near(key, cells, x0, y0, cell))))
                    ids.append(index[key])
                faces.append(ids)
        return {
            "z": self.floor_z + FLOOR_OFFSET_MM,
            "vertices": vertices,
            "colors": colors,
            "faces": faces,
        }

    def _room_cells(self, ring) -> dict[tuple[int, int], tuple[float, float, float]]:
        """Usable cells whose centre is in this room: (row, col) -> (x mm, y mm, score)."""
        grid = self.grid
        ring_cm = [(x / 10.0, y / 10.0) for x, y in ring]
        cells = {}
        for row in range(grid.rows):
            cy = float(grid.y_coords[row])
            for col in range(grid.cols):
                idx = row * grid.cols + col
                cx = float(grid.x_coords[col])
                if grid.usable_mask[idx] and point_in_any_polygon(cx, cy, [ring_cm]):
                    cells[(row, col)] = (cx * 10.0, cy * 10.0, float(self.scores[idx]))
        return cells

    @staticmethod
    def _score_near(point, cells, x0: float, y0: float, cell: float) -> float:
        """Score of the room cell that holds the point, else of the nearest room cell."""
        x, y = point
        home = (math.floor((y - y0) / cell + 0.5), math.floor((x - x0) / cell + 0.5))
        if home in cells:
            return cells[home][2]
        return min(cells.values(), key=lambda c: (c[0] - x) ** 2 + (c[1] - y) ** 2)[2]

    def mesh_gap_mm(self, params: dict) -> float:
        """Largest distance from a mesh boundary vertex to the nearest inner wall face."""
        edges: dict[tuple[int, int], int] = {}
        for face in params["faces"]:
            for a, b in zip(face, face[1:] + face[:1]):
                key = (min(a, b), max(a, b))
                edges[key] = edges.get(key, 0) + 1
        boundary = {v for edge, count in edges.items() if count == 1 for v in edge}
        segments = [(a, b) for ring in self.rings_mm() for a, b in zip(ring, ring[1:] + ring[:1])]
        gap = 0.0
        for v in boundary:
            x, y = params["vertices"][v]
            gap = max(gap, min(point_to_segment_dist(x, y, a[0], a[1], b[0], b[1]) for a, b in segments))
        return gap

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


def display_rgb(score: float) -> tuple[int, int, int]:
    """Forsk's sky display ramp. Same clamp and linear sRGB steps as Planwire's
    score_to_rgb; only the stops differ. Scores are Planwire's, unchanged."""
    t = min(max(float(score), 0.0), 1.0)
    i = 0
    while i < len(SKY_STOPS) - 2 and t > SKY_STOPS[i + 1].t:
        i += 1
    a, b = SKY_STOPS[i], SKY_STOPS[i + 1]
    u = (t - a.t) / ((b.t - a.t) or 1.0)
    return (
        round(a.r + (b.r - a.r) * u),
        round(a.g + (b.g - a.g) * u),
        round(a.b + (b.b - a.b) * u),
    )


def _cross(o, a, b) -> float:
    return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])


def _area2(poly) -> float:
    return sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(poly, poly[1:] + poly[:1]))


def _clean_ring(ring) -> list[tuple[float, float]]:
    """Counter-clockwise, no repeated or collinear vertices. Dropping a collinear
    vertex keeps the outline where it is."""
    pts = []
    for p in ring:
        if not pts or math.dist(p, pts[-1]) > MESH_EPS_MM:
            pts.append((float(p[0]), float(p[1])))
    if len(pts) > 1 and math.dist(pts[0], pts[-1]) <= MESH_EPS_MM:
        pts.pop()
    if _area2(pts) < 0:
        pts.reverse()
    changed = True
    while changed and len(pts) > 3:
        changed = False
        for i in range(len(pts)):
            a, b, c = pts[i - 1], pts[i], pts[(i + 1) % len(pts)]
            if abs(_cross(a, b, c)) <= MESH_EPS_MM * math.dist(a, c):
                del pts[i]
                changed = True
                break
    return pts


def _in_triangle(p, a, b, c) -> bool:
    return _cross(a, b, p) >= 0 and _cross(b, c, p) >= 0 and _cross(c, a, p) >= 0


def _convex_pieces(ring) -> list[list[tuple[float, float]]]:
    """Ear-clip the outline, then merge triangles back across diagonals while
    the result stays convex (Hertel–Mehlhorn). A rectangle stays one piece."""
    pts = _clean_ring(ring)
    if len(pts) < 3:
        return []
    left = list(range(len(pts)))
    polys: list[list[int]] = []
    while len(left) > 3:
        n = len(left)
        ears = [
            k for k in range(n)
            if _cross(pts[left[k - 1]], pts[left[k]], pts[left[(k + 1) % n]]) > 0
            and not any(
                _in_triangle(pts[m], pts[left[k - 1]], pts[left[k]], pts[left[(k + 1) % n]])
                for m in left if m not in (left[k - 1], left[k], left[(k + 1) % n])
            )
        ]
        # A simple polygon always has an ear. Rhino noise can hide it: then cut
        # the sharpest convex corner rather than loop forever.
        k = ears[0] if ears else max(
            range(n), key=lambda k: _cross(pts[left[k - 1]], pts[left[k]], pts[left[(k + 1) % n]])
        )
        polys.append([left[k - 1], left[k], left[(k + 1) % n]])
        del left[k]
    polys.append(left)

    def convex(poly) -> bool:
        return all(_cross(pts[poly[i - 1]], pts[poly[i]], pts[poly[(i + 1) % len(poly)]]) >= 0
                   for i in range(len(poly)))

    merged = True
    while merged:
        merged = False
        for p in range(len(polys)):
            edges = {(polys[p][i], polys[p][(i + 1) % len(polys[p])]): i for i in range(len(polys[p]))}
            for q in range(p + 1, len(polys)):
                for j in range(len(polys[q])):
                    b, a = polys[q][j], polys[q][(j + 1) % len(polys[q])]
                    if (a, b) not in edges:
                        continue
                    i = edges[(a, b)]
                    first = polys[p][i + 1:] + polys[p][:i + 1]   # b … a
                    second = polys[q][j + 1:] + polys[q][:j + 1]  # a … b
                    union = first + second[1:-1]
                    if convex(union):
                        polys[p] = union
                        del polys[q]
                        merged = True
                    break
                if merged:
                    break
            if merged:
                break
    return [[pts[i] for i in poly] for poly in polys]


def _line_point(p, q, axis: int, c: float) -> tuple[float, float]:
    # Endpoints in a fixed order, so both pieces that share an edge get the
    # same crossing point, bit for bit.
    a, b = sorted((p, q))
    t = (c - a[axis]) / (b[axis] - a[axis])
    other = a[1 - axis] + t * (b[1 - axis] - a[1 - axis])
    return (c, other) if axis == 0 else (other, c)


def _half_plane(poly, axis: int, c: float, keep_above: bool) -> list:
    out = []
    for k in range(len(poly)):
        p, q = poly[k - 1], poly[k]
        p_in = p[axis] >= c if keep_above else p[axis] <= c
        q_in = q[axis] >= c if keep_above else q[axis] <= c
        if q_in:
            if not p_in:
                out.append(_line_point(p, q, axis, c))
            out.append(q)
        elif p_in:
            out.append(_line_point(p, q, axis, c))
    return out


def _lattice_clip(piece, x0: float, y0: float, cell: float) -> list[list]:
    """Clip a convex piece to the squares whose corners are cell centres."""
    xs = [p[0] for p in piece]
    ys = [p[1] for p in piece]
    out = []
    for i in range(math.floor((min(xs) - x0) / cell), math.floor((max(xs) - x0) / cell) + 1):
        lo_x, hi_x = x0 + i * cell, x0 + (i + 1) * cell
        strip = _half_plane(_half_plane(piece, 0, lo_x, True), 0, hi_x, False)
        if len(strip) < 3:
            continue
        for j in range(math.floor((min(ys) - y0) / cell), math.floor((max(ys) - y0) / cell) + 1):
            lo_y, hi_y = y0 + j * cell, y0 + (j + 1) * cell
            poly = _half_plane(_half_plane(strip, 1, lo_y, True), 1, hi_y, False)
            if len(poly) >= 3:
                out.append(poly)
    return out


def _keys(poly) -> list[tuple[float, float]]:
    """Vertices rounded to 0.01 mm, no repeats. Empty when the polygon has no area."""
    keys = []
    for p in poly:
        key = (round(p[0], 2), round(p[1], 2))
        if not keys or key != keys[-1]:
            keys.append(key)
    if len(keys) > 1 and keys[0] == keys[-1]:
        keys.pop()
    if len(keys) < 3 or abs(_area2(keys)) <= MIN_FACE_AREA2:
        return []
    return keys


def _split_t_junctions(polys: list[list], x0: float, y0: float, cell: float) -> None:
    """Add to each polygon edge the vertices of other polygons that lie on it, so
    neighbours share every vertex along a shared edge (welded, no cracks).
    Polygons sit in one lattice square each, so only nearby vertices can hit."""
    buckets: dict[tuple[int, int], set] = {}
    for keys in polys:
        for x, y in keys:
            buckets.setdefault((math.floor((x - x0) / cell), math.floor((y - y0) / cell)), set()).add((x, y))
    for keys in polys:
        i, j = math.floor((keys[0][0] - x0) / cell), math.floor((keys[0][1] - y0) / cell)
        near = set().union(*(buckets.get((i + di, j + dj), set()) for di in (-1, 0, 1) for dj in (-1, 0, 1)))
        out = []
        for a, b in zip(keys, keys[1:] + keys[:1]):
            out.append(a)
            length = math.dist(a, b)
            on = [
                p for p in near
                if p != a and p != b
                and abs(_cross(a, b, p)) <= MESH_EPS_MM * length
                and 0 < (p[0] - a[0]) * (b[0] - a[0]) + (p[1] - a[1]) * (b[1] - a[1]) < length * length
            ]
            out.extend(sorted(on, key=lambda p: math.dist(a, p)))
        keys[:] = out


def _faces_of(keys) -> list[list[tuple[float, float]]]:
    """A convex polygon as one quad, a triangle fan, or, when a vertex sits on a
    straight edge, a star from its centroid so that vertex keeps both edges."""
    n = len(keys)
    straight = any(abs(_cross(keys[i - 1], keys[i], keys[(i + 1) % n])) <= MIN_FACE_AREA2 for i in range(n))
    if straight:
        c = (round(sum(p[0] for p in keys) / n, 2), round(sum(p[1] for p in keys) / n, 2))
        return [[a, b, c] for a, b in zip(keys, keys[1:] + keys[:1]) if abs(_cross(a, b, c)) > MIN_FACE_AREA2]
    if n == 4:
        return [keys]
    return [[keys[0], keys[i], keys[i + 1]] for i in range(1, n - 1)]


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
    if not any(p.kind == "WINDOW" for p in portals):
        raise DaylightTargetError(NO_WINDOWS)

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


def status(run: DaylightRun) -> str:
    return (
        f"Daylight on {run.spaces} space(s): {run.windows} window(s), "
        f"{run.cells} cells on A-ANALYSE. {DISCLAIMER}"
    )


def evaluate(scene: dict, target: str = "floor", cell_mm: float = DEFAULT_CELL_MM) -> dict:
    """Score a daylight_scene result. On success the daylight_paint params ride
    along under "paint"; a refusal is success False with the message."""
    try:
        started = time.perf_counter()
        run = run_scene(scene, target=target, cell_mm=cell_mm)
        compute_ms = round((time.perf_counter() - started) * 1000)
    except DaylightTargetError as e:
        return {"success": False, "message": str(e), "disclaimer": DISCLAIMER}
    summary = run.summary()
    return {
        "success": True,
        **summary,
        "compute_ms": compute_ms,
        "warnings": list(scene.get("warnings") or []) + summary["notes"],
        "message": status(run),
        "paint": run.paint_params(),
    }


def main() -> int:
    """Forsk panel entry: {"scene", "target", "cell_size"} JSON on stdin, the
    evaluate() result as one JSON object on stdout. The panel paints it."""
    request = json.load(sys.stdin)
    result = evaluate(
        request.get("scene") or {},
        target=request.get("target") or "floor",
        cell_mm=float(request.get("cell_size") or DEFAULT_CELL_MM),
    )
    json.dump(result, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
