"""Forsk daylight: an estimated daylight factor under a CIE overcast sky.

Rhino scene in mm (the daylight_scene bridge command) -> DF % per floor cell
-> one flat-coloured room mesh in mm (the daylight_paint bridge command). Pure
Python and NumPy, so the MCP tool, the panel and the live smokes share it.

DF = GLASS_TRANSMITTANCE * (SC + IRC), in percent of the open-sky horizontal
illuminance, on the work plane.

SC, the sky component: each cell's work-plane point looks at each facade
window's glass in patches. A patch counts when the ray to it, and on past it to
the sky, clears every wall face in 3D. Faces are the edges of the forsk:path
wall bands, from wall base to wall top, and an opening is a hole in the faces
it crosses between its sill and head: interior doors are open holes, interior
windows pass light at the glass transmittance, facade doors are shut. Past the
glass the ray must also clear the roof overhang. A patch weighs its solid angle
by the CIE overcast luminance (1 + 2 sin θ) / 3 and by sin θ, the cosine of
incidence on the horizontal work plane, over the 7π/9 of an open sky.

IRC, the internally reflected component: the BRE split-flux formula per room,
from its facade windows, floor, walls and ceiling, the same all over the room.

Not a simulation: no light bounces between rooms, no outside reflections,
no furniture, no window frames.
"""

from __future__ import annotations

import json
import math
import sys
import time
from dataclasses import dataclass, field

import numpy as np

DEFAULT_CELL_MM = 400.0
FLOOR_OFFSET_MM = 50.0
WORK_PLANE_MM = 850.0
# Glass is sampled in patches no larger than this, each side.
PATCH_MM = 200.0
GLASS_TRANSMITTANCE = 0.7
WALL_REFLECTANCE = 0.5
FLOOR_REFLECTANCE = 0.2
CEILING_REFLECTANCE = 0.7
GLASS_REFLECTANCE = 0.1
# BRE split-flux C for a window with no outside obstruction.
BRE_C_OPEN = 39.0
# Horizontal illuminance of an open CIE overcast sky, in zenith luminances.
OPEN_SKY = 7.0 * math.pi / 9.0
# The display ramp's top: DF at or above this reads near white.
DF_FULL_SCALE = 5.0
# Heights when the scene has none: ForskDefaults in the plugin.
WALL_HEIGHT_MM = 3000.0
WINDOW_SILL_MM = 900.0
WINDOW_HEAD_MM = 2100.0
DOOR_HEAD_MM = 2100.0
# The compute is per cell, so a tiny cell size on a large floor is refused.
MAX_CELLS = 20_000
SCOPE = "df-estimate"
LABEL = "Estimated daylight factor (CIE overcast), not a simulation"
DISCLAIMER = (
    f"{LABEL}. DF % on an {WORK_PLANE_MM:g} mm work plane, dark blue 0 %, near white {DF_FULL_SCALE:g} % "
    "and up. Not lux, not EN 17037, not a code check."
)

PARALLEL_SIN = 0.02
# Room probes this far past the wall band's faces, either side of an opening.
PROBE_MM = 200.0
# Cut lookup key: face index times this, plus the distance along the face.
CUT_KEY = 1e7
# Mesh: outline vertices closer than this merge; faces smaller than this drop.
MESH_EPS_MM = 0.01
MIN_FACE_AREA2 = 1e-3

# Forsk display ramp (F4.2): dark blue to near white, so it reads on the grey
# viewport. t is DF / DF_FULL_SCALE.
RAMP = "sky"
SKY_STOPS: tuple[tuple[float, int, int, int], ...] = (
    (0.0, 0x0B, 0x25, 0x45),
    (0.33, 0x2F, 0x66, 0x90),
    (0.66, 0x8F, 0xBC, 0xE6),
    (1.0, 0xF2, 0xF8, 0xFD),
)

NO_ROOMS = "No rooms. Run rooms_detect (Make rooms) to find them from the walls, or draw closed outlines on A-ROOM."
NO_WINDOWS = "No windows. Daylight comes in through windows: add one with add_opening, then run daylight again."
NO_SELECTION = "Select a room marker first (A-ROOM), or run daylight on the whole floor."

# What a hole does to a ray that crosses it before reaching the sampled glass.
HOLE, GLASS, FACADE = 0, 1, 2


class DaylightTargetError(ValueError):
    """The scene has nothing to score. The message says what to do."""


@dataclass
class Face:
    """One edge of a wall band ring: a vertical rectangle from z0 to z1."""

    ax: float
    ay: float
    bx: float
    by: float
    length: float
    ux: float
    uy: float
    z0: float
    z1: float


@dataclass
class Opening:
    id: str
    kind: str
    width: float
    sill: float
    head: float
    # Centre on the wall band's centreline, the direction along the wall, and
    # the normal into the room it lights (facade openings).
    mx: float
    my: float
    ux: float
    uy: float
    nx: float
    ny: float
    half: float
    role: str
    room: int | None = None
    roof_z: float | None = None
    reach: float = 0.0


@dataclass
class Room:
    id: str | None
    ring: list[tuple[float, float]]
    z: float
    ceiling: float
    area: float
    perimeter: float


@dataclass
class Model:
    faces: list[Face]
    openings: list[Opening]
    rooms: list[Room]
    notes: list[str]
    # Face arrays for the ray tests.
    ax: np.ndarray = field(init=False)
    ay: np.ndarray = field(init=False)
    ex: np.ndarray = field(init=False)
    ey: np.ndarray = field(init=False)
    length: np.ndarray = field(init=False)
    z0: np.ndarray = field(init=False)
    z1: np.ndarray = field(init=False)
    cuts: "Cuts" = field(init=False)

    def __post_init__(self) -> None:
        self.ax = np.array([f.ax for f in self.faces], dtype=float)
        self.ay = np.array([f.ay for f in self.faces], dtype=float)
        self.ex = np.array([f.bx - f.ax for f in self.faces], dtype=float)
        self.ey = np.array([f.by - f.ay for f in self.faces], dtype=float)
        self.length = np.array([f.length for f in self.faces], dtype=float)
        self.z0 = np.array([f.z0 for f in self.faces], dtype=float)
        self.z1 = np.array([f.z1 for f in self.faces], dtype=float)

    def sky_windows(self) -> list[tuple[int, Opening]]:
        return [(i, o) for i, o in enumerate(self.openings) if o.kind == "window" and o.role == "facade"]


class Cuts:
    """Holes in the faces: along-face interval and z interval, per opening."""

    def __init__(self, rows: list[tuple[int, float, float, float, float, int, int]]):
        cols = list(zip(*rows)) if rows else [()] * 7
        self.face = np.array(cols[0], dtype=np.int64)
        self.a0 = np.array(cols[1], dtype=float)
        self.a1 = np.array(cols[2], dtype=float)
        self.z0 = np.array(cols[3], dtype=float)
        self.z1 = np.array(cols[4], dtype=float)
        self.opening = np.array(cols[5], dtype=np.int64)
        self.effect = np.array(cols[6], dtype=np.int64)
        keys = self.face * CUT_KEY + self.a0
        self.order = np.argsort(keys, kind="stable")
        self.keys = keys[self.order]

    def lookup(self, face: np.ndarray, along: np.ndarray) -> np.ndarray:
        """The cut that holds each crossing along its face, or -1. Openings do not overlap."""
        if not len(self.keys):
            return np.full(len(face), -1, dtype=np.int64)
        pos = np.searchsorted(self.keys, face * CUT_KEY + along, side="right") - 1
        k = self.order[np.clip(pos, 0, None)]
        hit = (pos >= 0) & (self.face[k] == face) & (along <= self.a1[k])
        return np.where(hit, k, -1)


@dataclass
class Cell:
    """One lattice square of one room, as the convex polygons that fill it.
    DF is taken at the centroid of the largest one."""

    room: int
    polys: list[list[tuple[float, float]]]
    x: float
    y: float
    area: float
    df: float = 0.0


@dataclass
class DaylightRun:
    model: Model
    rooms: list[int]
    cells: list[Cell]
    cell_mm: float
    windows: int
    floor_z: float
    notes: list[str]

    @property
    def spaces(self) -> int:
        return len(self.rooms)

    def rings_mm(self) -> list[list[tuple[float, float]]]:
        """The room outlines scored (inner wall faces), in mm."""
        return [self.model.rooms[r].ring for r in self.rooms]

    def paint_params(self) -> dict:
        """daylight_paint params in mm: one mesh that fills each room, one flat
        colour per cell. Each room outline is cut into convex pieces, clipped to
        the lattice squares. A cell's vertices are its own, so no colour runs
        across a wall or into the next cell."""
        _split_t_junctions([keys for cell in self.cells for keys in cell.polys], 0.0, 0.0, self.cell_mm)
        vertices, colors, faces = [], [], []
        for cell in self.cells:
            rgb = list(display_rgb(cell.df / DF_FULL_SCALE))
            index: dict[tuple[float, float], int] = {}
            for keys in cell.polys:
                for face in _faces_of(keys):
                    ids = []
                    for key in face:
                        if key not in index:
                            index[key] = len(vertices)
                            vertices.append(list(key))
                            colors.append(rgb)
                        ids.append(index[key])
                    faces.append(ids)
        return {
            "z": self.floor_z + FLOOR_OFFSET_MM,
            "vertices": vertices,
            "colors": colors,
            "faces": faces,
        }

    def mesh_gap_mm(self, params: dict) -> float:
        """Largest distance from a mesh boundary vertex to the nearest inner wall
        face. Cells do not share vertices, so edges are matched by position."""
        edges: dict[tuple, int] = {}
        for face in params["faces"]:
            points = [tuple(params["vertices"][i]) for i in face]
            for a, b in zip(points, points[1:] + points[:1]):
                key = (min(a, b), max(a, b))
                edges[key] = edges.get(key, 0) + 1
        boundary = {p for edge, count in edges.items() if count == 1 for p in edge}
        segments = [(a, b) for ring in self.rings_mm() for a, b in zip(ring, ring[1:] + ring[:1])]
        gap = 0.0
        for x, y in boundary:
            gap = max(gap, min(segment_distance(x, y, a[0], a[1], b[0], b[1]) for a, b in segments))
        return gap

    def summary(self) -> dict:
        area = sum(c.area for c in self.cells)
        mean = sum(c.df * c.area for c in self.cells) / area if area else 0.0
        return {
            "spaces": self.spaces,
            "windows": self.windows,
            "cells": len(self.cells),
            "df_max": round(max((c.df for c in self.cells), default=0.0), 2),
            "df_mean": round(mean, 2),
            "scope": SCOPE,
            "disclaimer": DISCLAIMER,
            "notes": self.notes,
        }


def display_rgb(t: float) -> tuple[int, int, int]:
    """Forsk's sky display ramp: clamp to 0–1, linear sRGB between the stops."""
    t = min(max(float(t), 0.0), 1.0)
    i = 0
    while i < len(SKY_STOPS) - 2 and t > SKY_STOPS[i + 1][0]:
        i += 1
    a, b = SKY_STOPS[i], SKY_STOPS[i + 1]
    u = (t - a[0]) / ((b[0] - a[0]) or 1.0)
    return (
        round(a[1] + (b[1] - a[1]) * u),
        round(a[2] + (b[2] - a[2]) * u),
        round(a[3] + (b[3] - a[3]) * u),
    )


def point_in_ring(px: float, py: float, ring) -> bool:
    inside = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > py) != (yj > py) and px < (xj - xi) * (py - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def segment_distance(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    dx, dy = bx - ax, by - ay
    len_sq = dx * dx + dy * dy
    if len_sq < 1e-12:
        return math.hypot(px - ax, py - ay)
    t = min(max(((px - ax) * dx + (py - ay) * dy) / len_sq, 0.0), 1.0)
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


# --- Scene -> model ---------------------------------------------------------


def _round(point) -> tuple[float, float]:
    # 0.1 mm grid: Rhino noise must not open slivers along lattice lines.
    return round(float(point[0]), 1), round(float(point[1]), 1)


def _ring(ring) -> list[tuple[float, float]]:
    pts = [_round(p) for p in ring]
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts = pts[:-1]
    return pts


def _wall_faces(wall: dict, z0: float, z1: float) -> list[Face]:
    faces = []
    for ring in wall.get("rings") or []:
        pts = _ring(ring)
        for a, b in zip(pts, pts[1:] + pts[:1]):
            length = math.hypot(b[0] - a[0], b[1] - a[1])
            if length < 0.1:
                continue
            faces.append(Face(
                ax=a[0], ay=a[1], bx=b[0], by=b[1], length=length,
                ux=(b[0] - a[0]) / length, uy=(b[1] - a[1]) / length, z0=z0, z1=z1,
            ))
    return faces


def _room_at(x: float, y: float, rooms: list[Room]) -> int | None:
    return next((i for i, room in enumerate(rooms) if point_in_ring(x, y, room.ring)), None)


def _place(opening: dict, host: list[int], faces: list[Face], thickness: float, rooms: list[Room]):
    """The opening on its wall band's centreline, and the host faces it crosses:
    (Opening, [(face index, along from, along to)]). Its role comes from the rooms
    either side: facade with a room on one side, interior with rooms on both."""
    cx, cy = _round(opening["center"])
    width = float(opening["width"])
    nearest = min(host, key=lambda i: segment_distance(cx, cy, faces[i].ax, faces[i].ay, faces[i].bx, faces[i].by))
    ux, uy = faces[nearest].ux, faces[nearest].uy
    nx, ny = -uy, ux
    crossed = []
    for i in host:
        face = faces[i]
        if abs(face.ux * uy - face.uy * ux) > PARALLEL_SIN:
            continue
        offset = (face.ax - cx) * nx + (face.ay - cy) * ny
        along = (cx - face.ax) * face.ux + (cy - face.ay) * face.uy
        if abs(offset) <= thickness + 20.0 and -width / 2 <= along <= face.length + width / 2:
            crossed.append((i, offset))
    if not crossed:
        return None, []
    offsets = [offset for _i, offset in crossed]
    if len(offsets) > 1:
        mid, half = (min(offsets) + max(offsets)) / 2, (max(offsets) - min(offsets)) / 2
    else:
        mid, half = offsets[0] - math.copysign(thickness / 2, offsets[0]), thickness / 2
    mx, my = cx + nx * mid, cy + ny * mid
    probe = half + PROBE_MM
    ahead = _room_at(mx + nx * probe, my + ny * probe, rooms)
    behind = _room_at(mx - nx * probe, my - ny * probe, rooms)
    role, room = "loose", None
    if ahead is not None and behind is not None:
        role = "interior"
    elif ahead is not None:
        role, room = "facade", ahead
    elif behind is not None:
        role, room, nx, ny = "facade", behind, -nx, -ny
    kind = "window" if str(opening.get("kind", "")).lower() == "window" else "door"
    base = min(faces[i].z0 for i in host)
    sill = opening.get("sill")
    head = opening.get("head")
    placed = Opening(
        id=str(opening.get("id") or ""), kind=kind, width=width,
        sill=float(sill) if sill is not None else base + (WINDOW_SILL_MM if kind == "window" else 0.0),
        head=float(head) if head is not None else base + (WINDOW_HEAD_MM if kind == "window" else DOOR_HEAD_MM),
        mx=mx, my=my, ux=ux, uy=uy, nx=nx, ny=ny, half=half, role=role, room=room,
    )
    holes = []
    for i, _offset in crossed:
        face = faces[i]
        t = (mx - face.ax) * face.ux + (my - face.ay) * face.uy
        holes.append((i, t - width / 2, t + width / 2))
    return placed, holes


def _ceiling(z: float, roofs: list[dict], faces: list[Face]) -> float:
    """The roof underside above a floor, else the top of the walls."""
    above = [float(r["z0"]) for r in roofs if float(r["z0"]) > z + WORK_PLANE_MM]
    if above:
        return min(above)
    return max((f.z1 for f in faces), default=z + WALL_HEIGHT_MM)


def build_model(scene: dict) -> Model:
    """Faces, placed openings and their holes, and rooms, all in mm."""
    rooms_in = [r for r in scene.get("rooms") or [] if len(_ring(r.get("ring") or [])) >= 3]
    if not rooms_in:
        raise DaylightTargetError(NO_ROOMS)
    floor = min(float(r.get("z") or 0.0) for r in rooms_in)
    faces: list[Face] = []
    host_faces: dict[str, list[int]] = {}
    thickness: dict[str, float] = {}
    for wall in scene.get("walls") or []:
        z0 = float(wall["z0"]) if wall.get("z0") is not None else floor
        z1 = float(wall["z1"]) if wall.get("z1") is not None else z0 + WALL_HEIGHT_MM
        start = len(faces)
        faces.extend(_wall_faces(wall, z0, z1))
        host_faces[wall["id"]] = list(range(start, len(faces)))
        thickness[wall["id"]] = float(wall.get("thickness") or 200.0)

    roofs = [r for r in scene.get("roofs") or [] if r.get("z0") is not None]
    rooms = []
    for r in rooms_in:
        ring = _clean_ring(_ring(r["ring"]))
        z = float(r.get("z") or 0.0)
        rooms.append(Room(
            id=r.get("id"), ring=ring, z=z, ceiling=_ceiling(z, roofs, faces),
            area=abs(_area2(ring)) / 2,
            perimeter=sum(math.dist(a, b) for a, b in zip(ring, ring[1:] + ring[:1])),
        ))

    notes: list[str] = []
    openings: list[Opening] = []
    holes = []
    for opening in scene.get("openings") or []:
        host = opening.get("host_id")
        if not host_faces.get(host):
            notes.append(f"{opening.get('id')}: host wall {host!r} not found, skipped")
            continue
        placed, crossed = _place(opening, host_faces[host], faces, thickness[host], rooms)
        if placed is None:
            notes.append(f"{opening.get('id')}: not on a face of wall {host!r}, skipped")
            continue
        if placed.kind == "window" and placed.role == "loose":
            notes.append(f"{placed.id}: no room on either side, not a sky source")
        if placed.kind == "window" and placed.role == "facade":
            above = [r for r in roofs if float(r["z0"]) >= placed.head]
            if above:
                roof = min(above, key=lambda r: float(r["z0"]))
                placed.roof_z = float(roof["z0"])
                placed.reach = placed.half + float(roof.get("overhang") or 0.0)
        # A facade door is shut: its faces stay solid.
        if placed.kind == "door" and placed.role == "facade":
            openings.append(placed)
            continue
        if placed.kind == "door":
            effect = HOLE
        else:
            effect = FACADE if placed.role == "facade" else GLASS
        n = len(openings)
        openings.append(placed)
        holes.extend((i, a0, a1, placed.sill, placed.head, n, effect) for i, a0, a1 in crossed)

    model = Model(faces=faces, openings=openings, rooms=rooms, notes=notes)
    model.cuts = Cuts(holes)
    return model


def room_irc(model: Model, r: int) -> float:
    """BRE split-flux internally reflected component, in % (before the glass
    transmittance), from the room's facade windows."""
    room = model.rooms[r]
    glass = [o for o in model.openings if o.kind == "window" and o.role == "facade" and o.room == r]
    window = sum(o.width * (o.head - o.sill) for o in glass) / 1e6
    if window <= 0:
        return 0.0
    height = max(room.ceiling - room.z, 1.0) / 1000.0
    mid = sum(o.width * (o.head - o.sill) * ((o.sill + o.head) / 2 - room.z) for o in glass) / 1e6 / window / 1000.0
    mid = min(max(mid, 0.0), height)
    floor = room.area / 1e6
    perimeter = room.perimeter / 1000.0
    total = 2 * floor + perimeter * height
    walls = max(perimeter * height - window, 0.0)
    mean = (floor * FLOOR_REFLECTANCE + floor * CEILING_REFLECTANCE + walls * WALL_REFLECTANCE
            + window * GLASS_REFLECTANCE) / total
    below, above = perimeter * mid, perimeter * (height - mid)
    r_fw = (floor * FLOOR_REFLECTANCE + below * WALL_REFLECTANCE) / (floor + below)
    r_cw = (floor * CEILING_REFLECTANCE + above * WALL_REFLECTANCE) / (floor + above)
    return 0.85 * window / (total * (1 - mean)) * (BRE_C_OPEN * r_fw + 5 * r_cw)


# --- Sky component ------------------------------------------------------------


def _patch_weights(depth, du, dz, pw: float, ph: float) -> np.ndarray:
    """Each glass patch's exact solid angle times (1 + 2 sin θ) / 3 times sin θ.
    depth (M,): distance to the glass plane; du (M,) and dz (M, R): the patch
    centre from the point's foot on that plane, along the wall and up."""
    d = depth[:, None]
    u = du[:, None]

    def corner(a, b):
        return np.arctan(a * b / (d * np.sqrt(a * a + b * b + d * d)))

    u1, u2 = u - pw / 2, u + pw / 2
    z1, z2 = dz - ph / 2, dz + ph / 2
    omega = corner(u2, z2) - corner(u1, z2) - corner(u2, z1) + corner(u1, z1)
    sin = dz / np.sqrt(d * d + u * u + dz * dz)
    return np.where(sin > 0, omega * (1 + 2 * sin) / 3 * sin, 0.0)


def _ray_blocks(model: Model, own: int, px, py, pz, qx: float, qy: float, zr):
    """Rays from the points to one column of glass patches, then on to the sky.
    Returns (blocked, interior panes crossed), each (M, R)."""
    dx, dy = qx - px, qy - py
    denom = dx[:, None] * model.ey - dy[:, None] * model.ex
    rx = model.ax - px[:, None]
    ry = model.ay - py[:, None]
    with np.errstate(divide="ignore", invalid="ignore"):
        t = (rx * model.ey - ry * model.ex) / denom
        s = (rx * dy[:, None] - ry * dx[:, None]) / denom
    point, face = np.nonzero((np.abs(denom) > 1e-9) & (t > 1e-9) & (s >= 0) & (s <= 1))
    t = t[point, face]
    k = model.cuts.lookup(face, s[point, face] * model.length[face])
    has = k >= 0
    k = np.where(has, k, 0)
    cuts = model.cuts
    # Before the glass (t < 1) the ray may pass interior doors and interior
    # glass; past it, or through another facade window, it is outside or back in.
    mine = has & (cuts.opening[k] == own)
    inside = has & (t < 1)
    passes = mine | (inside & (cuts.effect[k] == HOLE))
    pane = ~mine & inside & (cuts.effect[k] == GLASS)
    z = pz[point, None] + t[:, None] * (zr[None, :] - pz[point, None])
    in_face = (z > model.z0[face, None]) & (z < model.z1[face, None])
    in_hole = has[:, None] & (z >= cuts.z0[k, None]) & (z <= cuts.z1[k, None])
    block = in_face & ~(in_hole & (passes | pane)[:, None])
    m, r = len(px), len(zr)
    flat = point[:, None] * r + np.arange(r)[None, :]
    blocked = np.bincount(flat.ravel(), weights=block.ravel(), minlength=m * r).reshape(m, r) > 0
    # A pane is a hole in both faces of its wall: one pane per opening per ray.
    hit, row = np.nonzero(in_face & in_hole & pane[:, None])
    count = len(model.openings)
    rays = np.unique(flat[hit, row] * count + cuts.opening[k[hit]]) // count
    panes = np.bincount(rays, minlength=m * r).reshape(m, r)
    return blocked, panes


def sky_component(model: Model, points) -> np.ndarray:
    """SC in % at points (x, y, z mm): the open-sky share each point sees
    through facade glass. Interior glass on the way counts, the facade glass
    itself does not (DF applies it)."""
    pts = np.asarray(points, dtype=float).reshape(-1, 3)
    px, py, pz = pts[:, 0], pts[:, 1], pts[:, 2]
    total = np.zeros(len(pts))
    for own, o in model.sky_windows():
        depth = (px - o.mx) * o.nx + (py - o.my) * o.ny
        front = np.nonzero(depth > o.half)[0]
        if not len(front) or o.head <= o.sill:
            continue
        fx, fy, fz, fd = px[front], py[front], pz[front], depth[front]
        cols = max(1, math.ceil(o.width / PATCH_MM))
        rows = max(1, math.ceil((o.head - o.sill) / PATCH_MM))
        pw, ph = o.width / cols, (o.head - o.sill) / rows
        zr = o.sill + (np.arange(rows) + 0.5) * ph
        dz = zr[None, :] - fz[:, None]
        foot = (fx - o.mx) * o.ux + (fy - o.my) * o.uy
        if o.roof_z is not None:
            # Past the glass the ray must reach the overhang's edge below its underside.
            edge = fz[:, None] + (1 + o.reach / fd)[:, None] * dz
            open_sky = edge < o.roof_z
        else:
            open_sky = np.ones_like(dz, dtype=bool)
        seen = np.zeros(len(front))
        for j in range(cols):
            c = -o.width / 2 + (j + 0.5) * pw
            weight = _patch_weights(fd, c - foot, dz, pw, ph) * open_sky
            live = np.nonzero((weight > 0).any(axis=1))[0]
            if not len(live):
                continue
            blocked, panes = _ray_blocks(
                model, own, fx[live], fy[live], fz[live], o.mx + o.ux * c, o.my + o.uy * c, zr,
            )
            seen[live] += (weight[live] * ~blocked * GLASS_TRANSMITTANCE ** panes).sum(axis=1)
        total[front] += seen
    return 100.0 * total / OPEN_SKY


def daylight_factor(model: Model, points, rooms: list[int]) -> np.ndarray:
    """DF % at points (x, y mm) on the work plane of the given rooms."""
    pts = [(x, y, model.rooms[r].z + WORK_PLANE_MM) for (x, y), r in zip(points, rooms)]
    sc = sky_component(model, pts) if pts else np.zeros(0)
    irc = {r: room_irc(model, r) for r in set(rooms)}
    return GLASS_TRANSMITTANCE * (sc + np.array([irc[r] for r in rooms]))


# --- Cells and mesh -----------------------------------------------------------


def _centroid(poly) -> tuple[float, float]:
    a2 = _area2(poly)
    if abs(a2) <= MIN_FACE_AREA2:
        return sum(p[0] for p in poly) / len(poly), sum(p[1] for p in poly) / len(poly)
    cx = cy = 0.0
    for a, b in zip(poly, poly[1:] + poly[:1]):
        cross = a[0] * b[1] - b[0] * a[1]
        cx += (a[0] + b[0]) * cross
        cy += (a[1] + b[1]) * cross
    return cx / (3 * a2), cy / (3 * a2)


def _connected(polys: list[list]) -> list[list[list]]:
    """Polygons of one lattice square that share an edge form one cell. A wall
    stub inside the room keeps its two sides apart."""
    parent = list(range(len(polys)))

    def root(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    seen: dict[tuple, int] = {}
    for i, keys in enumerate(polys):
        for a, b in zip(keys, keys[1:] + keys[:1]):
            edge = (min(a, b), max(a, b))
            if edge in seen:
                parent[root(i)] = root(seen[edge])
            else:
                seen[edge] = i
    groups: dict[int, list] = {}
    for i, keys in enumerate(polys):
        groups.setdefault(root(i), []).append(keys)
    return list(groups.values())


def _cells(model: Model, rooms: list[int], cell: float) -> list[Cell]:
    """Each room clipped to the lattice squares whose corners are multiples of
    the cell size, one cell per connected part of a square."""
    out = []
    for r in rooms:
        squares: dict[tuple[int, int], list] = {}
        for piece in _convex_pieces(model.rooms[r].ring):
            for poly in _lattice_clip(piece, 0.0, 0.0, cell):
                keys = _keys(poly)
                if keys:
                    x, y = _centroid(keys)
                    squares.setdefault((math.floor(x / cell), math.floor(y / cell)), []).append(keys)
        for polys in squares.values():
            for group in _connected(polys):
                largest = max(group, key=lambda keys: abs(_area2(keys)))
                x, y = _centroid(largest)
                out.append(Cell(room=r, polys=group, x=x, y=y, area=sum(abs(_area2(k)) / 2 for k in group)))
    return out


def _check_cells(model: Model, rooms: list[int], cell: float) -> None:
    count = 0
    for r in rooms:
        xs = [p[0] for p in model.rooms[r].ring]
        ys = [p[1] for p in model.rooms[r].ring]
        count += (math.ceil((max(xs) - min(xs)) / cell) + 1) * (math.ceil((max(ys) - min(ys)) / cell) + 1)
    if count > MAX_CELLS:
        raise DaylightTargetError(f"Grid of about {count} cells exceeds {MAX_CELLS}. Use a larger cell size.")


def run_scene(scene: dict, target: str = "floor", cell_mm: float = DEFAULT_CELL_MM) -> DaylightRun:
    model = build_model(scene)
    if target == "selection":
        picked = set(scene.get("selected_room_ids") or [])
        rooms = [i for i, room in enumerate(model.rooms) if room.id in picked]
        if not rooms:
            raise DaylightTargetError(NO_SELECTION)
    elif target == "floor":
        rooms = list(range(len(model.rooms)))
    else:
        raise DaylightTargetError("target must be floor or selection.")
    if cell_mm <= 0:
        raise DaylightTargetError("cell size must be positive.")
    if not model.sky_windows():
        raise DaylightTargetError(NO_WINDOWS)
    _check_cells(model, rooms, cell_mm)
    cells = _cells(model, rooms, cell_mm)
    for cell, df in zip(cells, daylight_factor(model, [(c.x, c.y) for c in cells], [c.room for c in cells])):
        cell.df = float(df)
    return DaylightRun(
        model=model,
        rooms=rooms,
        cells=cells,
        cell_mm=cell_mm,
        windows=sum(1 for o in model.openings if o.kind == "window"),
        floor_z=min(model.rooms[r].z for r in rooms),
        notes=model.notes,
    )


def status(run: DaylightRun, summary: dict) -> str:
    return (
        f"Daylight on {run.spaces} space(s): {run.windows} window(s), {len(run.cells)} cells on A-ANALYSE, "
        f"mean DF {summary['df_mean']:.1f} %, max {summary['df_max']:.1f} %. {DISCLAIMER}"
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
        "message": status(run, summary),
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


# --- Mesh geometry (F4.2) -------------------------------------------------------


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
    """Clip a convex piece to the lattice squares from (x0, y0), cell wide."""
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
    neighbours meet vertex to vertex along a shared edge (no cracks).
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


if __name__ == "__main__":
    sys.exit(main())
