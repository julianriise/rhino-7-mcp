"""Headless checks for scripts/daylight_smoke.py: the lines the forsk compare reads."""

import base64
import copy
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import daylight_smoke  # noqa: E402
from tests.test_forsk_daylight import GARAGE, GARAGE_CELLS  # noqa: E402

PNG = b"\x89PNG\r\n\x1a\n"


class FakeRhino:
    """Keeps the overlay count the way the plugin does: paint replaces, clear empties."""

    def __init__(self, clear_works: bool = True):
        self.overlays = 0
        self.clear_works = clear_works

    def __call__(self, command: str, params: dict) -> dict:
        if command == "daylight_scene":
            return copy.deepcopy(GARAGE)
        if command == "daylight_paint":
            deleted, self.overlays = self.overlays, 1
            return {"id": "m", "cells": len(params["cells"]), "layer": "A-ANALYSE", "deleted": deleted}
        if command == "get_objects":
            return {"objects": [{"attributes": {"forsk:kind": "analysis"}}] * self.overlays}
        if command == "capture_viewport":
            return {"image_data": base64.b64encode(PNG).decode(), "width": 1000, "height": 700}
        if command == "daylight_clear":
            count = self.overlays
            if self.clear_works:
                self.overlays = 0
            return {"count": count, "remaining": self.overlays}
        raise AssertionError(command)


def test_garage_step_prints_the_compare_lines(tmp_path, capsys):
    png = tmp_path / "daylight.png"
    failures: list = []
    daylight_smoke.run_step(FakeRhino(), str(png), 1, failures, rerun=True)
    out = capsys.readouterr().out
    assert failures == []
    assert f"daylight spaces 1 windows 1 cells {GARAGE_CELLS} in " in out
    assert f"daylight mesh A-ANALYSE overlays 1 painted {GARAGE_CELLS} scope sky-vis-proxy" in out
    assert f"daylight rerun overlays 1 deleted 1 cells {GARAGE_CELLS}" in out
    assert "daylight clear remaining 0 overlays 0" in out
    assert "not illuminance / EN 17037" in out
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
    assert rhino.overlays == 0
