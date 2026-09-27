"""Forsk daylight adapter: Rhino scene (mm) -> DaylightRequest (cm) -> paint cells (mm)."""

from __future__ import annotations

import copy
import json
import subprocess
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

import forsk_daylight as fd
from daylight.elements import request_to_elements
from daylight.geom import point_in_polygon, point_to_segment_dist
from daylight.portals import build_daylight_scene
from daylight.ramp import score_to_rgb

# The garage smoke at its daylight step: 8×4 m band, 200 mm thick, one room on
# the inner faces, the high window and four doors on the south wall.
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
# Garage smoke baseline: daylight_windows / daylight_cells.
GARAGE_WINDOWS = 1
GARAGE_CELLS = 144
GARAGE_VERTICES = 231


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


def portals(run):
    return build_daylight_scene(request_to_elements(run.request)).portals


def score_at(run, x_mm: float, y_mm: float) -> float:
    grid = run.grid
    best, best_d = None, float("inf")
    for row in range(grid.rows):
        for col in range(grid.cols):
            idx = row * grid.cols + col
            if not grid.usable_mask[idx]:
                continue
            d = (grid.x_coords[col] * 10 - x_mm) ** 2 + (grid.y_coords[row] * 10 - y_mm) ** 2
            if d < best_d:
                best, best_d = float(run.scores[idx]), d
    return best


def test_garage_counts_match_the_smoke_baseline():
    run = fd.run_scene(GARAGE)
    assert (run.spaces, run.windows, run.cells) == (1, GARAGE_WINDOWS, GARAGE_CELLS)
    assert run.request.grid.cellSizeCm == 40.0
    assert all(p.kind == "outer" for p in portals(run))


def test_garage_is_brighter_at_the_window_than_at_the_back_wall():
    run = fd.run_scene(GARAGE)
    assert score_at(run, 4800, 500) > score_at(run, 4800, 3500)


def test_rhino_noise_does_not_move_edge_cells():
    noisy = copy.deepcopy(GARAGE)
    for ring in noisy["walls"][0]["rings"] + [noisy["rooms"][0]["ring"]]:
        for point in ring:
            point[0] += 1e-6
            point[1] -= 1e-6
    assert fd.run_scene(noisy).cells == GARAGE_CELLS


def test_opening_punches_both_faces_and_sits_on_the_centreline():
    run = fd.run_scene(GARAGE)
    ids = [w.id for w in run.request.walls]
    # South outer face (ring 0, edge 0) and south inner face (ring 1, edge 0) are split.
    assert "w01.0.0" not in ids and "w01.0.0.0" in ids
    assert "w01.1.0" not in ids and "w01.1.0.0" in ids
    portal_walls = [w for w in run.request.walls if w.id.startswith("portal.")]
    assert len(portal_walls) == len(GARAGE["openings"])
    assert all(abs(p[1] - 10.0) < 1e-9 for w in portal_walls for p in w.segments)


def test_marker_off_the_centreline_still_lands_between_the_faces():
    scene = copy.deepcopy(GARAGE)
    scene["openings"][3]["center"] = [4800, 180]
    run = fd.run_scene(scene)
    window = next(w for w in run.request.walls if w.id == "portal.3")
    assert all(abs(p[1] - 10.0) < 1e-9 for p in window.segments)


def test_interior_door_is_a_hole_not_a_sky_source():
    run = fd.run_scene(two_rooms())
    kinds = sorted((p.kind, p.source) for p in portals(run))
    assert kinds == [("outer", "window"), ("outer", "window")]
    assert run.spaces == 2 and run.windows == 2


def test_selected_room_is_one_space_and_its_door_stays_a_hole():
    scene = two_rooms()
    scene["selected_room_ids"] = ["r02"]
    run = fd.run_scene(scene, target="selection")
    assert run.spaces == 1
    assert [o.kind for o in run.request.openings] == ["WINDOW", "WINDOW"]
    assert score_at(run, 7500, 4000) > 0
    whole = fd.run_scene(two_rooms())
    assert run.cells < whole.cells


def test_empty_selection_is_refused():
    with pytest.raises(fd.DaylightTargetError, match="Select a room"):
        fd.run_scene(two_rooms(), target="selection")


def test_no_rooms_is_refused():
    scene = copy.deepcopy(GARAGE)
    scene["rooms"] = []
    with pytest.raises(fd.DaylightTargetError, match="rooms_from_layer"):
        fd.run_scene(scene)


def test_doors_only_is_refused():
    scene = copy.deepcopy(GARAGE)
    scene["openings"] = [o for o in scene["openings"] if o["kind"] == "door"]
    with pytest.raises(fd.DaylightTargetError, match="No windows"):
        fd.run_scene(scene)


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


def test_huge_grid_is_refused():
    with pytest.raises(fd.DaylightTargetError, match="cell size"):
        fd.run_scene(GARAGE, cell_mm=10)


def test_unknown_host_is_noted_and_skipped():
    scene = copy.deepcopy(GARAGE)
    scene["openings"][0]["host_id"] = "w99"
    run = fd.run_scene(scene)
    assert len(run.request.openings) == len(GARAGE["openings"]) - 1
    assert "w99" in run.notes[0]


def mesh_area(params) -> float:
    return sum(abs(fd._area2([tuple(params["vertices"][i]) for i in f])) / 2 for f in params["faces"])


def l_shape() -> dict:
    """An L room with a 200 mm wall stub poking into it and a notch: the cases a
    grid-corner mesh cannot fill. One window on the south face."""
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
        inside = any(point_in_polygon(x, y, r) for r in rings)
        on_face = any(point_to_segment_dist(x, y, *a, *b) <= 0.01 for r in rings for a, b in zip(r, r[1:] + r[:1]))
        assert inside or on_face


def test_mesh_is_welded_with_no_inner_open_edges():
    run = fd.run_scene(l_shape())
    params = run.paint_params()
    assert len({tuple(v) for v in params["vertices"]}) == len(params["vertices"])
    assert len(params["colors"]) == len(params["vertices"])
    assert all(len(f) in (3, 4) for f in params["faces"])
    # Every open edge lies on a room face: nothing inside the room is left unshared.
    assert run.mesh_gap_mm(params) < 0.02


def test_garage_mesh_pins_the_smoke_vertex_count():
    params = fd.run_scene(GARAGE).paint_params()
    assert params["z"] == 50.0
    # Lattice lines through cell centres, 20 × 10 pieces up to the faces.
    assert len(params["faces"]) == 200
    assert len(params["vertices"]) == GARAGE_VERTICES == 21 * 11


def test_centre_vertex_takes_its_cell_score_and_edge_vertex_the_nearest():
    run = fd.run_scene(GARAGE)
    params = run.paint_params()
    colour = {tuple(v): tuple(c) for v, c in zip(params["vertices"], params["colors"])}
    grid = run.grid
    row, col = grid.rows // 2, grid.cols // 2
    x, y = grid.x_coords[col] * 10, grid.y_coords[row] * 10
    assert colour[(round(x, 2), round(y, 2))] == fd.display_rgb(float(run.scores[row * grid.cols + col]))
    # On the south face, straight below that centre: the nearest cell is the first usable row.
    first = next(r for r in range(grid.rows) if grid.usable_mask[r * grid.cols + col])
    assert colour[(round(x, 2), 200.0)] == fd.display_rgb(float(run.scores[first * grid.cols + col]))


def test_sky_ramp_stops_and_clamp():
    assert fd.RAMP == "sky"
    assert fd.display_rgb(-1) == fd.display_rgb(0) == (0x0B, 0x25, 0x45)
    assert fd.display_rgb(0.33) == (0x2F, 0x66, 0x90)
    assert fd.display_rgb(0.66) == (0x8F, 0xBC, 0xE6)
    assert fd.display_rgb(2) == fd.display_rgb(1) == (0xF2, 0xF8, 0xFD)


def test_colours_stay_on_the_sky_ramp_and_scores_stay_planwire():
    run = fd.run_scene(GARAGE)
    ramp = {fd.display_rgb(i / 100000) for i in range(100001)}
    assert all(tuple(c) in ramp for c in run.paint_params()["colors"])
    # Planwire's own ramp is untouched: the display ramp is Forsk's only.
    assert score_to_rgb(0) == (8, 42, 82)


def test_summary_carries_the_proxy_scope():
    summary = fd.run_scene(GARAGE).summary()
    assert summary["scope"] == "sky-vis-proxy"
    assert "not illuminance" in summary["disclaimer"] and "EN 17037" in summary["disclaimer"]
    assert 0 < summary["score_mean"] <= summary["score_max"] <= 1


def _conn(scene: dict) -> MagicMock:
    conn = MagicMock()

    def send(command, params):
        if command == "daylight_scene":
            return copy.deepcopy(scene)
        if command == "daylight_paint":
            return {"id": "mesh-1", "cells": len(params["faces"]), "deleted": 1, "layer": "A-ANALYSE"}
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
    assert result["scope"] == "sky-vis-proxy"
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
