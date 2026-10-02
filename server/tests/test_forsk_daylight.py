"""Forsk daylight: Rhino scene (mm) -> estimated daylight factor (%) -> paint cells (mm)."""

from __future__ import annotations

import copy
import json
import subprocess
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import numpy as np
import pytest

import forsk_daylight as fd

FIXTURES = Path(__file__).resolve().parent / "fixtures"

# The garage smoke at its daylight step: 8×4 m band, 200 mm thick, one room on
# the inner faces, the window and four doors on the south wall.
GARAGE = {
    "walls": [{
        "id": "w01",
        "thickness": 200,
        "rings": [
            [[0, 0], [8000, 0], [8000, 4000], [0, 4000]],
            [[200, 200], [7800, 200], [7800, 3800], [200, 3800]],
        ],
    }],
    "openings": [
        {"id": "door-01", "host_id": "w01", "kind": "door", "width": 900, "center": [1200, 100]},
        {"id": "door-02", "host_id": "w01", "kind": "door", "width": 900, "center": [2300, 100]},
        {"id": "door-03", "host_id": "w01", "kind": "door", "width": 900, "center": [3400, 100]},
        {"id": "window-03", "host_id": "w01", "kind": "window", "width": 1200, "center": [4800, 100]},
        {"id": "door-04", "host_id": "w01", "kind": "door", "width": 900, "center": [6115, 100]},
    ],
    "rooms": [{"id": "r01", "ring": [[200, 200], [7800, 200], [7800, 3800], [200, 3800]], "z": 0}],
    "selected_room_ids": [],
}
GARAGE_WINDOWS = 1
# 20 × 10 lattice squares: 400 mm squares on multiples of 400, the room from 200 to 7800 × 200 to 3800.
GARAGE_CELLS = 200
GARAGE_VERTICES = 800


def box(width=4000.0, depth=6000.0, window=1200.0, sill=900.0, head=2100.0, t=200.0, z1=3000.0) -> dict:
    """One room, width × depth on the inner faces, one window centred in the south wall."""
    return {
        "walls": [{"id": "w01", "thickness": t, "z0": 0.0, "z1": z1, "rings": [
            [[-t, -t], [width + t, -t], [width + t, depth + t], [-t, depth + t]],
            [[0, 0], [width, 0], [width, depth], [0, depth]],
        ]}],
        "openings": [{"id": "window-01", "host_id": "w01", "kind": "window", "width": window,
                      "center": [width / 2, -t / 2], "sill": sill, "head": head}],
        "rooms": [{"id": "r01", "ring": [[0, 0], [width, 0], [width, depth], [0, depth]], "z": 0.0}],
        "selected_room_ids": [],
    }


def stacked(partition: dict | None = None) -> dict:
    """Room A (y 0–3000) with the south window, room B (y 3200–6000) behind a
    200 mm partition, both 4000 wide. partition: an opening in it, or none."""
    scene = box()
    scene["walls"][0]["rings"] = [
        [[-200, -200], [4200, -200], [4200, 6200], [-200, 6200]],
        [[0, 0], [4000, 0], [4000, 3000], [0, 3000]],
        [[0, 3200], [4000, 3200], [4000, 6000], [0, 6000]],
    ]
    scene["rooms"] = [
        {"id": "a", "ring": [[0, 0], [4000, 0], [4000, 3000], [0, 3000]], "z": 0.0},
        {"id": "b", "ring": [[0, 3200], [4000, 3200], [4000, 6000], [0, 6000]], "z": 0.0},
    ]
    if partition:
        scene["openings"].append({"id": "inner", "host_id": "w01", "width": 1200, "center": [2000, 3100], **partition})
    return scene


def two_rooms() -> dict:
    """10×5 m band split at x=5000 by a 200 mm wall with one door; one window per room."""
    return {
        "walls": [{
            "id": "w01",
            "thickness": 200,
            "rings": [
                [[0, 0], [10000, 0], [10000, 5000], [0, 5000]],
                [[200, 200], [4900, 200], [4900, 4800], [200, 4800]],
                [[5100, 200], [9800, 200], [9800, 4800], [5100, 4800]],
            ],
        }],
        "openings": [
            {"id": "window-01", "host_id": "w01", "kind": "window", "width": 1200, "center": [2500, 4900]},
            {"id": "window-02", "host_id": "w01", "kind": "window", "width": 1200, "center": [7500, 4900]},
            {"id": "door-01", "host_id": "w01", "kind": "door", "width": 900, "center": [5000, 2500]},
        ],
        "rooms": [
            {"id": "r01", "ring": [[200, 200], [4900, 200], [4900, 4800], [200, 4800]], "z": 0},
            {"id": "r02", "ring": [[5100, 200], [9800, 200], [9800, 4800], [5100, 4800]], "z": 0},
        ],
        "selected_room_ids": [],
    }


def office() -> dict:
    """The office smoke's model after its opening edits: one wall band, 62
    windows, 14 doors, 15 rooms, flat roof (underside 2800, 500 overhang)."""
    return json.loads((FIXTURES / "office_daylight_scene.json").read_text())


def df_at(scene: dict, x: float, y: float) -> float:
    model = fd.build_model(scene)
    room = fd._room_at(x, y, model.rooms)
    return float(fd.daylight_factor(model, [(x, y)], [room])[0])


def sc_at(scene: dict, x: float, y: float, z: float = fd.WORK_PLANE_MM) -> float:
    return float(fd.sky_component(fd.build_model(scene), [(x, y, z)])[0])


def area_mean(run) -> float:
    return sum(c.df * c.area for c in run.cells) / sum(c.area for c in run.cells)


# --- The estimate against what a daylight factor must do ---------------------


def test_an_open_half_sky_is_half_the_daylight_factor_sky():
    """A window filling the half-space in front of the point: the CIE overcast
    sky is symmetric about the zenith, so half of it lights 50 % of an open plane."""
    scene = box(width=100000, depth=10000, window=99000, sill=850, head=200000, z1=300000)
    assert sc_at(scene, 50000, 500) == pytest.approx(50.0, abs=1.0)


def test_a_point_behind_a_solid_wall_sees_no_sky():
    scene = stacked()
    assert sc_at(scene, 2000, 1500) > 1.0
    for x, y in [(2000, 3600), (400, 5800), (3600, 3400)]:
        assert sc_at(scene, x, y) == 0.0
    run = fd.run_scene(scene)
    behind = [c.df for c in run.cells if c.room == 1]
    # No window of its own, so no reflected light either.
    assert behind and max(behind) == 0.0


def test_df_falls_with_depth_and_is_symmetric_about_the_window_axis():
    run = fd.run_scene(box())
    cells = {(round(c.x, 3), round(c.y, 3)): c.df for c in run.cells}
    assert len(cells) == 150
    for (x, y), df in cells.items():
        assert df == pytest.approx(cells[(round(4000 - x, 3), y)], rel=1e-9)
    axis = [df for (x, y), df in sorted(cells.items(), key=lambda kv: kv[0][1]) if x == 2200]
    assert len(axis) == 15
    assert all(a > b for a, b in zip(axis, axis[1:]))
    # Deep in the room the reflected light is most of what is left.
    direct, reflected = fd.daylight_components(run.model, [(2200, 5800)], [0])
    assert reflected[0] > direct[0] > 0


def test_a_wider_window_raises_df_near_it():
    assert df_at(box(window=2400), 2000, 1000) > 1.3 * df_at(box(window=1200), 2000, 1000)


def test_sill_and_head_matter():
    """The old sky-vis proxy scored a 100 mm slot like a 1.2 m window."""
    deep = (2000, 5000)
    assert sc_at(box(sill=1500, head=2700), *deep) > sc_at(box(sill=900, head=2100), *deep)
    assert sc_at(box(sill=1900, head=2000), *deep) < 0.2 * sc_at(box(sill=900, head=2100), *deep)
    # Glass below the work plane shows the ground, not the sky.
    assert sc_at(box(sill=100, head=800), 2000, 1000) == 0.0


def test_the_roof_overhang_cuts_high_sky_near_the_window():
    scene = box()
    near = sc_at(scene, 2000, 400)
    scene["roofs"] = [{"z0": 2300, "overhang": 800}]
    assert sc_at(scene, 2000, 400) < 0.9 * near


def test_interior_glass_passes_sky_at_the_glass_transmittance():
    """Room B sees A's window through a glazed partition: the same rays as
    through an open hole of the same size, times the glass transmittance."""
    glass = stacked({"kind": "window", "sill": 900, "head": 2100})
    hole = stacked({"kind": "door", "sill": 900, "head": 2100})
    seen = sc_at(hole, 2000, 3800)
    assert seen > 0
    assert sc_at(glass, 2000, 3800) == pytest.approx(fd.GLASS_TRANSMITTANCE * seen, rel=1e-9)
    kinds = sorted((o.kind, o.role) for o in fd.build_model(glass).openings)
    assert kinds == [("window", "facade"), ("window", "interior")]


def test_df_is_stable_under_a_grid_shift():
    base = fd.run_scene(box())
    scene = box()
    dx, dy = 130.0, 70.0
    for ring in scene["walls"][0]["rings"] + [scene["rooms"][0]["ring"]]:
        for point in ring:
            point[0] += dx
            point[1] += dy
    scene["openings"][0]["center"] = [2000 + dx, -100 + dy]
    moved = fd.run_scene(scene)
    assert area_mean(moved) == pytest.approx(area_mean(base), rel=0.02)

    def band(run, lo, hi, oy):
        cells = [c for c in run.cells if lo <= c.y - oy < hi]
        return sum(c.df * c.area for c in cells) / sum(c.area for c in cells)

    for lo, hi in [(1000, 2000), (2000, 4000), (4000, 6000)]:
        assert band(moved, lo, hi, dy) == pytest.approx(band(base, lo, hi, 0), rel=0.1)


def test_facade_doors_are_shut_and_interior_doors_open():
    model = fd.build_model(two_rooms())
    roles = {o.id: (o.kind, o.role) for o in model.openings}
    assert roles == {"window-01": ("window", "facade"), "window-02": ("window", "facade"),
                     "door-01": ("door", "interior")}
    garage = fd.build_model(GARAGE)
    # Garage doors are on the facade: shut, so only the window's faces are cut.
    assert sorted(set(garage.cuts.opening.tolist())) == [3]


# --- Reflected light ------------------------------------------------------------


def bre_split_flux(width, depth, height, window_w, sill, head) -> float:
    """BRE split-flux average IRC for a box room with one unobstructed window,
    with the glass transmittance in place of BRE's 0.85 (clear glass)."""
    floor = width * depth / 1e6
    walls = 2 * (width + depth) / 1000 * height / 1000
    glass = window_w * (head - sill) / 1e6
    total = 2 * floor + walls
    mean = (floor * fd.FLOOR_REFLECTANCE + floor * fd.CEILING_REFLECTANCE
            + (walls - glass) * fd.WALL_REFLECTANCE + glass * fd.GLASS_REFLECTANCE) / total
    mid = (sill + head) / 2000
    below, above = 2 * (width + depth) / 1000 * mid, 2 * (width + depth) / 1000 * (height / 1000 - mid)
    r_fw = (floor * fd.FLOOR_REFLECTANCE + below * fd.WALL_REFLECTANCE) / (floor + below)
    r_cw = (floor * fd.CEILING_REFLECTANCE + above * fd.WALL_REFLECTANCE) / (floor + above)
    return fd.GLASS_TRANSMITTANCE * glass / (total * (1 - mean)) * (39 * r_fw + 5 * r_cw)


def test_a_closed_room_keeps_its_light():
    """Every patch sees the room all round, so with every surface at ρ and a
    uniform E, the bounce settles at B = ρ E / (1 − ρ)."""
    model = fd.build_model(box())
    patches = fd._patches(model, 0)
    view = fd._view(patches.pos, patches.normal, patches, model.rooms[0])
    assert view.sum(axis=1) == pytest.approx(1.0)
    exitance = np.linalg.solve(np.eye(len(view)) - 0.5 * view, np.full(len(view), 0.5))
    assert exitance == pytest.approx(1.0)
    # The patches cover the floor, the ceiling and the walls once.
    assert patches.area.sum() == pytest.approx(2 * 4000 * 6000 + 2 * 10000 * 3000)


def test_reflected_light_agrees_with_bre_split_flux():
    run = fd.run_scene(box())
    _direct, reflected = fd.daylight_components(run.model, [(c.x, c.y) for c in run.cells], [0] * len(run.cells))
    area = np.array([c.area for c in run.cells])
    mean = float((reflected * area).sum() / area.sum())
    assert mean == pytest.approx(bre_split_flux(4000, 6000, 3000, 1200, 900, 2100), rel=0.3)


def test_reflected_light_reaches_deeper_than_the_sky():
    model = fd.build_model(box())
    direct, reflected = fd.daylight_components(model, [(2000, 1000), (2000, 5800)], [0, 0])
    assert direct[1] < 0.02 * direct[0]
    assert reflected[1] > 0.5 * reflected[0]


def test_black_surfaces_reflect_nothing(monkeypatch):
    for name in ("WALL_REFLECTANCE", "FLOOR_REFLECTANCE", "CEILING_REFLECTANCE", "GLASS_REFLECTANCE"):
        monkeypatch.setattr(fd, name, 0.0)
    model = fd.build_model(box())
    direct, reflected = fd.daylight_components(model, [(2000, 1000), (2000, 5000)], [0, 0])
    assert reflected.tolist() == [0.0, 0.0] and direct.min() > 0


def test_a_brighter_ceiling_lights_the_back_of_the_room(monkeypatch):
    back = (2000, 5500)
    before = df_at(box(), *back)
    monkeypatch.setattr(fd, "CEILING_REFLECTANCE", 0.9)
    assert df_at(box(), *back) > 1.1 * before


def test_light_passes_an_interior_door_into_a_windowless_room():
    """Room B has no window. Through the door in the partition it sees a little
    sky and gets A's reflected light, most near the door."""
    run = fd.run_scene(stacked({"kind": "door", "sill": 0, "head": 2100}))
    model = run.model
    _direct, reflected = fd.daylight_components(model, [(2000, 3600), (200, 5800)], [1, 1])
    assert reflected[0] > reflected[1] > 0
    assert min(c.df for c in run.cells if c.room == 1) > 0


def test_the_display_is_a_log_scale():
    assert fd.df_shade(0.0) == fd.df_shade(fd.DF_LOW) == 0.0
    assert fd.df_shade(1.0) == pytest.approx(0.5)
    assert fd.df_shade(fd.DF_HIGH) == fd.df_shade(50.0) == 1.0
    shades = [fd.df_shade(v / 10) for v in range(1, 200)]
    assert shades == sorted(shades)


# --- The office: the dark spot -------------------------------------------------


def test_office_dark_spot_is_dim_for_a_reason():
    """room-06's west arm, 6.5 m from its only south window (430 mm wide) and
    round a corner from the east one. The old proxy scored 0.04 here: 3.7° of
    horizontal sky through the 430 mm window and nothing else, with no reflected
    light and no window height. The sky it sees is still tiny; what light it gets
    has bounced round the corner or come through the door from room-15."""
    model = fd.build_model(office())
    room = fd._room_at(6880, 20820, model.rooms)
    assert model.rooms[room].id == "room-06"
    # The east window lights the other end of the same room well.
    direct, reflected = fd.daylight_components(model, [(6880, 20820), (10880, 15280)], [room, room])
    assert direct[0] < 0.05 and reflected[0] > direct[0]
    assert 0.03 < direct[0] + reflected[0] < 0.5
    assert direct[1] + reflected[1] > 20 * (direct[0] + reflected[0])


def test_office_counts_and_every_room_gets_some_light():
    run = fd.run_scene(office())
    assert (run.spaces, run.windows, len(run.cells)) == (15, 62, 2875)
    roles = {}
    for o in run.model.openings:
        roles[(o.kind, o.role)] = roles.get((o.kind, o.role), 0) + 1
    assert roles == {("window", "facade"): 60, ("window", "interior"): 2,
                     ("door", "interior"): 12, ("door", "facade"): 2}
    # Every interior door and pane passes light between its two rooms.
    assert sum(len(patches.through) for patches, _b in run.model.light.values()) == 2 * 14
    # Rooms with no window of their own still get light through their doors.
    for r in run.rooms:
        assert min(c.df for c in run.cells if c.room == r) > 0
    summary = run.summary()
    assert 1.0 < summary["df_mean"] < 5.0 < summary["df_max"] < 40.0


# --- Scene handling -------------------------------------------------------------


def test_garage_counts_match_the_smoke_baseline():
    run = fd.run_scene(GARAGE)
    assert (run.spaces, run.windows, len(run.cells)) == (1, GARAGE_WINDOWS, GARAGE_CELLS)


def test_garage_is_brighter_at_the_window_than_at_the_back_wall():
    assert df_at(GARAGE, 4800, 500) > df_at(GARAGE, 4800, 3500) > 0


def test_rhino_noise_does_not_move_edge_cells():
    noisy = copy.deepcopy(GARAGE)
    for ring in noisy["walls"][0]["rings"] + [noisy["rooms"][0]["ring"]]:
        for point in ring:
            point[0] += 1e-6
            point[1] -= 1e-6
    assert len(fd.run_scene(noisy).cells) == GARAGE_CELLS


def test_opening_sits_on_the_centreline_and_cuts_both_faces():
    model = fd.build_model(GARAGE)
    window = next(o for o in model.openings if o.id == "window-03")
    assert (window.my, window.half, window.role) == (100.0, 100.0, "facade")
    assert (window.nx, window.ny) == (0.0, 1.0)
    faces = model.cuts.face[model.cuts.opening == model.openings.index(window)]
    assert len(faces) == 2


def test_marker_off_the_centreline_still_lands_between_the_faces():
    scene = copy.deepcopy(GARAGE)
    scene["openings"][3]["center"] = [4800, 180]
    window = next(o for o in fd.build_model(scene).openings if o.id == "window-03")
    assert window.my == 100.0


def test_missing_sill_and_head_take_the_forsk_defaults_on_the_wall_base():
    model = fd.build_model(GARAGE)
    window = next(o for o in model.openings if o.kind == "window")
    door = next(o for o in model.openings if o.kind == "door")
    assert (window.sill, window.head) == (fd.WINDOW_SILL_MM, fd.WINDOW_HEAD_MM)
    assert (door.sill, door.head) == (0.0, fd.DOOR_HEAD_MM)


def test_selected_room_is_one_space():
    scene = two_rooms()
    scene["selected_room_ids"] = ["r02"]
    run = fd.run_scene(scene, target="selection")
    assert run.spaces == 1
    assert {c.room for c in run.cells} == {1}
    assert len(run.cells) < len(fd.run_scene(two_rooms()).cells)
    assert min(c.df for c in run.cells) > 0


def test_empty_selection_is_refused():
    with pytest.raises(fd.DaylightTargetError, match="Select a room"):
        fd.run_scene(two_rooms(), target="selection")


def test_no_rooms_is_refused():
    scene = copy.deepcopy(GARAGE)
    scene["rooms"] = []
    with pytest.raises(fd.DaylightTargetError, match="rooms_detect"):
        fd.run_scene(scene)


def test_doors_only_is_refused():
    scene = copy.deepcopy(GARAGE)
    scene["openings"] = [o for o in scene["openings"] if o["kind"] == "door"]
    with pytest.raises(fd.DaylightTargetError, match="No windows"):
        fd.run_scene(scene)


def test_huge_grid_is_refused():
    with pytest.raises(fd.DaylightTargetError, match="cell size"):
        fd.run_scene(GARAGE, cell_mm=10)


def test_unknown_host_is_noted_and_skipped():
    scene = copy.deepcopy(GARAGE)
    scene["openings"][0]["host_id"] = "w99"
    run = fd.run_scene(scene)
    assert len(run.model.openings) == len(GARAGE["openings"]) - 1
    assert "w99" in run.notes[0]


def test_panel_entry_scores_in_a_child_process_like_the_panel():
    """The Forsk panel pipes {"scene"} into python -m forsk_daylight and paints "paint"."""
    src = Path(fd.__file__).resolve().parent
    done = subprocess.run(
        [sys.executable, "-m", "forsk_daylight"],
        input=json.dumps({"scene": GARAGE, "target": "floor"}),
        capture_output=True, text=True, cwd=src, env={"PYTHONPATH": str(src)}, check=True,
    )
    out = json.loads(done.stdout)
    assert out["success"] is True
    assert (out["spaces"], out["windows"], out["cells"]) == (1, GARAGE_WINDOWS, GARAGE_CELLS)
    assert out["compute_ms"] >= 0 and fd.DISCLAIMER in out["message"]
    assert len(out["paint"]["vertices"]) == GARAGE_VERTICES


def test_panel_entry_refusal_is_a_message_not_a_crash():
    scene = copy.deepcopy(GARAGE)
    scene["rooms"] = []
    out = fd.evaluate(scene)
    assert out["success"] is False and "A-ROOM" in out["message"] and "paint" not in out


# --- Paint ------------------------------------------------------------------------


def mesh_area(params) -> float:
    return sum(abs(fd._area2([tuple(params["vertices"][i]) for i in f])) / 2 for f in params["faces"])


def l_shape() -> dict:
    """An L room with a 200 mm wall stub poking into it and a notch: the cases a
    plain grid cannot fill. One window on the south face."""
    ring = [[200, 200], [6000, 200], [6000, 2000], [3100, 2000], [3100, 1400], [2900, 1400],
            [2900, 2000], [2000, 2000], [2000, 4000], [200, 4000]]
    return {
        "walls": [{"id": "w01", "thickness": 200, "rings": [
            [[0, 0], [6200, 0], [6200, 2200], [2200, 2200], [2200, 4200], [0, 4200]], ring,
        ]}],
        "openings": [{"id": "window-01", "host_id": "w01", "kind": "window", "width": 1200, "center": [3000, 100]}],
        "rooms": [{"id": "r01", "ring": ring, "z": 0}],
        "selected_room_ids": [],
    }


@pytest.mark.parametrize("scene", [GARAGE, l_shape(), two_rooms()], ids=["garage", "l-shape", "two-rooms"])
def test_mesh_fills_each_room_to_the_inner_faces(scene):
    run = fd.run_scene(scene)
    params = run.paint_params()
    rings = run.rings_mm()
    assert mesh_area(params) == pytest.approx(sum(abs(fd._area2(r)) / 2 for r in rings), abs=1.0)
    assert run.mesh_gap_mm(params) <= 1.0
    for x, y in params["vertices"]:
        inside = any(fd.point_in_ring(x, y, r) for r in rings)
        on_face = any(fd.segment_distance(x, y, *a, *b) <= 0.01 for r in rings for a, b in zip(r, r[1:] + r[:1]))
        assert inside or on_face


def test_each_cell_is_one_flat_colour_of_its_own_df():
    run = fd.run_scene(l_shape())
    params = run.paint_params()
    colors = params["colors"]
    assert len(colors) == len(params["vertices"])
    assert all(len(f) in (3, 4) for f in params["faces"])
    assert all(len({tuple(colors[i]) for i in f}) == 1 for f in params["faces"])
    expected = {tuple(fd.display_rgb(fd.df_shade(c.df))) for c in run.cells}
    assert {tuple(c) for c in colors} == expected
    # No crack inside the room: every open edge lies on a room face.
    assert run.mesh_gap_mm(params) < 0.02


def test_a_wall_stub_keeps_its_two_sides_apart():
    """The l-shape's stub (x 2900–3100 up from y 1400) sits inside lattice
    squares: each side is its own cell with its own value."""
    run = fd.run_scene(l_shape())
    square = [c for c in run.cells if 2800 <= c.x < 3200 and 1600 <= c.y < 2000]
    assert len(square) == 2
    assert {c.x < 3000 for c in square} == {True, False}


def test_garage_mesh_pins_the_smoke_vertex_count():
    params = fd.run_scene(GARAGE).paint_params()
    assert params["z"] == 50.0
    assert len(params["faces"]) == GARAGE_CELLS
    assert len(params["vertices"]) == GARAGE_VERTICES


def test_sky_ramp_stops_and_clamp():
    assert fd.RAMP == "sky"
    assert fd.display_rgb(-1) == fd.display_rgb(0) == (0x0B, 0x25, 0x45)
    assert fd.display_rgb(0.33) == (0x2F, 0x66, 0x90)
    assert fd.display_rgb(0.66) == (0x8F, 0xBC, 0xE6)
    assert fd.display_rgb(2) == fd.display_rgb(1) == (0xF2, 0xF8, 0xFD)


def test_summary_carries_the_estimate_label():
    summary = fd.run_scene(GARAGE).summary()
    assert summary["scope"] == "df-estimate"
    assert summary["disclaimer"].startswith("Estimated daylight factor (CIE overcast), not a simulation")
    assert 0 < summary["df_mean"] <= summary["df_max"]


# --- MCP tools --------------------------------------------------------------------


def _conn(scene: dict) -> MagicMock:
    conn = MagicMock()

    def send(command, params):
        if command == "daylight_scene":
            return copy.deepcopy(scene)
        if command == "daylight_paint":
            return {"id": "mesh-1", "faces": len(params["faces"]), "deleted": 1, "layer": "A-ANALYSE"}
        raise AssertionError(command)

    conn.send_command.side_effect = send
    return conn


@patch("rhinomcp.tools.daylight_from_model.get_rhino_connection")
def test_tool_reads_scene_paints_once_and_states_the_scope(mock_get_conn):
    from rhinomcp.tools.daylight_from_model import daylight_from_model

    mock_get_conn.return_value = _conn(GARAGE)
    result = daylight_from_model(ctx=None)
    commands = [c.args[0] for c in mock_get_conn.return_value.send_command.call_args_list]
    assert commands == ["daylight_scene", "daylight_paint"]
    assert result["success"] is True
    assert (result["spaces"], result["windows"], result["cells"]) == (1, GARAGE_WINDOWS, GARAGE_CELLS)
    assert result["scope"] == "df-estimate"
    assert fd.DISCLAIMER in result["message"]
    assert result["deleted"] == 1


@patch("rhinomcp.tools.daylight_from_model.get_rhino_connection")
def test_tool_refuses_an_empty_selection_without_painting(mock_get_conn):
    from rhinomcp.tools.daylight_from_model import daylight_from_model

    mock_get_conn.return_value = _conn(GARAGE)
    result = daylight_from_model(ctx=None, target="selection")
    assert result["success"] is False
    assert "Select a room" in result["message"]
    commands = [c.args[0] for c in mock_get_conn.return_value.send_command.call_args_list]
    assert commands == ["daylight_scene"]


@patch("rhinomcp.tools.daylight_clear.get_rhino_connection")
def test_clear_tool_reports_remaining(mock_get_conn):
    from rhinomcp.tools.daylight_clear import daylight_clear

    conn = MagicMock()
    conn.send_command.return_value = {"count": 1, "remaining": 0, "message": "Cleared 1"}
    mock_get_conn.return_value = conn
    result = daylight_clear(ctx=None)
    conn.send_command.assert_called_once_with("daylight_clear", {})
    assert (result["success"], result["count"], result["remaining"]) == (True, 1, 0)


def split_two_rooms() -> dict:
    """two_rooms() as selection S1 bakes it: one wall record per run, the
    north and south walls full length, the east, west and partition between
    them. Each opening on the record that holds it."""
    scene = two_rooms()
    pieces = {
        "w02": [[0, 4800], [10000, 4800], [10000, 5000], [0, 5000]],
        "w03": [[0, 0], [10000, 0], [10000, 200], [0, 200]],
        "w04": [[0, 200], [200, 200], [200, 4800], [0, 4800]],
        "w05": [[9800, 200], [10000, 200], [10000, 4800], [9800, 4800]],
        "w06": [[4900, 200], [5100, 200], [5100, 4800], [4900, 4800]],
    }
    scene["walls"] = [{"id": k, "thickness": 200, "rings": [ring]} for k, ring in pieces.items()]
    hosts = {"window-01": "w02", "window-02": "w02", "door-01": "w06"}
    for opening in scene["openings"]:
        opening["host_id"] = hosts[opening["id"]]
    return scene


def room_means(run) -> dict:
    means = {}
    for r in run.rooms:
        cells = [c for c in run.cells if c.room == r]
        means[run.model.rooms[r].id] = sum(c.df * c.area for c in cells) / sum(c.area for c in cells)
    return means


def test_split_walls_give_the_same_daylight_per_room():
    """S1: the walls as one record or as one record per run light each room the same, within 1 %."""
    whole = fd.run_scene(two_rooms())
    split = fd.run_scene(split_two_rooms())
    assert (split.spaces, split.windows, len(split.cells)) == (whole.spaces, whole.windows, len(whole.cells))
    a, b = room_means(whole), room_means(split)
    assert a.keys() == b.keys() == {"r01", "r02"}
    for room in a:
        assert b[room] == pytest.approx(a[room], rel=0.01), room


def test_the_mesh_floats_above_the_room_plates():
    """S3: room plates stand 20 mm on the slab (RoomPlate.ThicknessMm, pinned in RoomPlateTests); the mesh sits above them."""
    assert fd.FLOOR_OFFSET_MM == 50.0
