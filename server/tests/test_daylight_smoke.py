"""Headless checks for scripts/daylight_smoke.py: the lines the forsk compare reads."""

import base64
import copy
import sys
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import daylight_smoke  # noqa: E402
from tests.test_forsk_daylight import GARAGE, GARAGE_CELLS, GARAGE_VERTICES  # noqa: E402

GARAGE_FACES = 200

PNG = b"\x89PNG\r\n\x1a\n"


class FakeRhino:
    """Keeps the overlay objects the way the plugin does: paint replaces, clear empties."""

    def __init__(self, clear_works: bool = True):
        self.rows: list = []
        self.clear_works = clear_works

    def __call__(self, command: str, params: dict) -> dict:
        if command == "daylight_scene":
            return copy.deepcopy(GARAGE)
        if command == "daylight_paint":
            assert "legend" not in params
            deleted = len(self.rows)
            self.rows = [{"attributes": {"forsk:kind": "analysis", "forsk:role": "mesh"}}]
            return {"id": "m", "faces": len(params["faces"]), "vertices": len(params["vertices"]),
                    "wires": "off", "layer": "A-ANALYSE",
                    "deleted": deleted, "bbox": [200, 200, 7800, 3800]}
        if command == "get_objects":
            return {"objects": list(self.rows)}
        if command == "capture_viewport":
            # Mesh bbox 7600 × 3600 plus 5% on each side.
            assert params["zoom_bbox"] == pytest.approx([-180, 20, 8180, 3980])
            return {"image_data": base64.b64encode(PNG).decode(), "width": 1000, "height": 700}
        if command == "daylight_clear":
            count = len(self.rows)
            if self.clear_works:
                self.rows = []
            return {"count": count, "remaining": len(self.rows)}
        raise AssertionError(command)


def test_garage_step_prints_the_compare_lines(tmp_path, capsys):
    png = tmp_path / "daylight.png"
    failures: list = []
    daylight_smoke.run_step(FakeRhino(), str(png), 1, failures, rerun=True)
    out = capsys.readouterr().out
    assert failures == []
    assert f"daylight spaces 1 windows 1 cells {GARAGE_CELLS} in " in out
    assert " ms df mean " in out
    assert (
        f"daylight mesh A-ANALYSE flat {GARAGE_VERTICES} vertices wires off gap 0 ramp sky overlays 1 "
        f"painted {GARAGE_FACES} scope df-estimate"
    ) in out
    assert "legend" not in out
    assert f"daylight rerun overlays 1 deleted 1 cells {GARAGE_CELLS}" in out
    assert "daylight clear remaining 0 overlays 0" in out
    assert "Estimated daylight factor (CIE overcast), not a simulation" in out
    assert png.read_bytes() == PNG


def test_a_clear_that_leaves_the_mesh_fails(tmp_path, capsys):
    failures: list = []
    daylight_smoke.run_step(FakeRhino(clear_works=False), str(tmp_path / "d.png"), 1, failures)
    assert failures == ["daylight clear left 1"]


def test_wrong_space_count_fails_but_still_clears(tmp_path, capsys):
    rhino = FakeRhino()
    failures: list = []
    daylight_smoke.run_step(rhino, str(tmp_path / "d.png"), 15, failures)
    assert failures == ["daylight spaces 1 expected 15"]
    assert rhino.rows == []
