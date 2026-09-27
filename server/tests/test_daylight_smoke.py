"""Headless checks for scripts/daylight_smoke.py: the lines the forsk compare reads."""

import base64
import copy
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import daylight_smoke  # noqa: E402
from tests.test_forsk_daylight import GARAGE, GARAGE_CELLS, GARAGE_VERTICES  # noqa: E402

PNG = b"\x89PNG\r\n\x1a\n"


class FakeRhino:
    """Keeps the overlay objects the way the plugin does: paint replaces, clear empties."""

    def __init__(self, clear_works: bool = True, legend_below: bool = False):
        self.rows: list = []
        self.clear_works = clear_works
        self.legend_below = legend_below

    def __call__(self, command: str, params: dict) -> dict:
        if command == "daylight_scene":
            return copy.deepcopy(GARAGE)
        if command == "daylight_paint":
            deleted = len(self.rows)
            legend = params["legend"]
            shift = -5000 if self.legend_below else 0
            self.rows = [{"attributes": {"forsk:kind": "analysis", "forsk:role": "mesh"}}]
            bar = legend["bar"]
            boxes = [[[bar["x0"], bar["y0"] + shift, 50], [bar["x1"], bar["y1"] + shift, 50]]]
            boxes += [[[t["x"], t["y"] + shift, 50], [t["x"] + 1000, t["y"] + t["height"] + shift, 50]]
                      for t in legend["texts"]]
            self.rows += [{"attributes": {"forsk:kind": "analysis", "forsk:role": "legend"},
                           "bounding_box": box} for box in boxes]
            return {"id": "m", "cells": len(params["faces"]), "vertices": len(params["vertices"]),
                    "welded": True, "wires": "off", "legend": len(boxes), "layer": "A-ANALYSE",
                    "deleted": deleted, "bbox": [200, 200, 7800, 5000]}
        if command == "get_objects":
            return {"objects": list(self.rows)}
        if command == "capture_viewport":
            assert len(params["zoom_bbox"]) == 4
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
    assert (
        f"daylight mesh A-ANALYSE welded {GARAGE_VERTICES} vertices wires off overlays 1 "
        f"painted {GARAGE_CELLS} scope sky-vis-proxy"
    ) in out
    assert "daylight legend objects 5 top-left yes" in out
    assert f"daylight rerun overlays 1 legend 5 deleted 6 cells {GARAGE_CELLS}" in out
    assert "daylight clear remaining 0 overlays 0" in out
    assert "not illuminance / EN 17037" in out
    assert png.read_bytes() == PNG


def test_a_clear_that_leaves_the_mesh_fails(tmp_path, capsys):
    failures: list = []
    daylight_smoke.run_step(FakeRhino(clear_works=False), str(tmp_path / "d.png"), 1, failures)
    assert failures == ["daylight clear left 6"]


def test_wrong_space_count_fails_but_still_clears(tmp_path, capsys):
    rhino = FakeRhino()
    failures: list = []
    daylight_smoke.run_step(rhino, str(tmp_path / "d.png"), 15, failures)
    assert failures == ["daylight spaces 1 expected 15"]
    assert rhino.rows == []


def test_a_legend_inside_the_overlay_fails(tmp_path, capsys):
    failures: list = []
    daylight_smoke.run_step(FakeRhino(legend_below=True), str(tmp_path / "d.png"), 1, failures)
    assert "daylight legend objects 5 top-left no" in capsys.readouterr().out
    assert failures == ["daylight legend objects 5 top-left False"]
