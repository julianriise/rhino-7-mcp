"""Headless checks for scripts/plan_smoke.py helpers used by the live smokes."""

import sys
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import plan_smoke  # noqa: E402

OFFICE_DXF = Path(__file__).resolve().parents[3] / "forsk" / "tests" / "fixtures" / "office_2D.dxf"


def test_open_ring_drops_closing_and_repeated_vertices():
    ring = [(0, 0), (10, 0), (10, 0), (10, 5), (0, 5), (0, 0)]
    assert plan_smoke._open_ring(ring) == [(0, 0), (10, 0), (10, 5), (0, 5)]


@pytest.mark.skipif(not OFFICE_DXF.is_file(), reason="forsk office fixture not checked out")
def test_office_rooms_are_open_rings():
    rooms = plan_smoke.dxf_rooms(OFFICE_DXF)
    assert len(rooms) == 15
    for _, ring in rooms:
        # create_object refuses a polyline with a zero-length segment. The
        # smoke closes each ring once, so no vertex may repeat here.
        closed = ring + ring[:1]
        assert all(closed[i] != closed[i + 1] for i in range(len(ring)))


@pytest.mark.parametrize(
    "need,scale",
    [(34.55, 35), (50, 50), (50.01, 60), (100, 100), (100.01, 125), (137, 150), (500.01, 550), (0, 0)],
)
def test_round_scale_up_matches_plugin_steps(need, scale):
    assert plan_smoke.round_scale_up(need) == scale


def _footer_page(scale=50, meters=2, length=40.0, cells=None, arrow_x1=17.4, free=0):
    """A page record shaped like layout_pack's, boxes in paper mm."""
    if cells is None:
        cells = [
            {"name": "drawing", "text": "Plan 1. etg"},
            {"name": "scale", "text": f"1:{scale}"},
            {"name": "sheet", "text": "A3"},
        ]
    return {
        "view": "plan",
        "scale": scale,
        "page_scale": scale,
        "footer": {
            "title_block": {
                "x0": 130, "y0": 10, "x1": 410, "y1": 28, "w": 280, "h": 18, "cells": cells,
                "all": {"x0": 130, "y0": 10, "x1": 410, "y1": 28},
            },
            "north_arrow": {"x0": 11.0, "y0": 13.0, "x1": arrow_x1, "y1": 25.0},
            "scale_bar": {
                "x0": 26, "y0": 16.25, "x1": 26 + length, "y1": 18.25, "w": length, "h": 2,
                "meters": meters,
                "with_labels": {"x0": 25.3, "y0": 16.25, "x1": 30 + length, "y1": 21.75},
            },
            "free_labels": free,
        },
    }


def test_footer_passes_and_prints_the_smoke_lines(capsys):
    failures = []
    plan_smoke.check_footer(_footer_page(), "sheet", failures)
    out = capsys.readouterr().out
    assert failures == []
    assert "sheet title block h 18.0 w 280.0 cells 3" in out
    assert "sheet scale bar 2 m len 40.0 mm at 1:50" in out
    assert "sheet free label 0" in out
    assert "sheet north arrow left" in out
    assert "sheet footer inside sheet" in out


@pytest.mark.parametrize(
    "page,needle",
    [
        (_footer_page(meters=5, length=100.0), "scale bar 5 m"),
        (_footer_page(meters=2, length=45.0), "scale bar 2 m"),
        (_footer_page(meters=4, length=60.0), "scale bar 4 m"),
        (_footer_page(cells=[{"name": "scale", "text": "1:50"}, {"name": "client", "text": "—"}]), "empty"),
        (_footer_page(cells=[{"name": "scale", "text": "1:100"}]), "title block scale"),
        (_footer_page(free=1), "free label 1"),
        (_footer_page(arrow_x1=30.0), "north arrow"),
    ],
)
def test_footer_fails_on_each_broken_rule(page, needle):
    failures = []
    plan_smoke.check_footer(page, "sheet", failures)
    assert any(needle in item for item in failures), failures


def test_footer_fails_a_tall_title_block():
    page = _footer_page()
    page["footer"]["title_block"].update({"y1": 56, "h": 46, "w": 168, "x0": 242})
    failures = []
    plan_smoke.check_footer(page, "sheet", failures)
    assert any("title block h 46.0" in item for item in failures)


def test_footer_accepts_3_and_30_m_bars():
    for scale, meters in ((55, 3), (60, 3), (525, 30), (600, 30)):
        failures = []
        page = _footer_page(scale=scale, meters=meters, length=round(meters * 1000 / scale, 2))
        plan_smoke.check_footer(page, "sheet", failures)
        assert failures == [], (scale, failures)


def test_footer_fails_the_scaled_north_letter():
    """The F5.0b run: "N" drawn at 100x its paper height."""
    page = _footer_page()
    page["footer"]["north_arrow"] = {"x0": -113.6, "y0": 11.8, "x1": 138.6, "y1": 343.0}
    page["footer"]["scale_bar"]["with_labels"]["x0"] = -187.2
    failures = []
    plan_smoke.check_footer(page, "sheet", failures)
    assert any("footer outside band" in item and "arrow" in item and "bar" in item for item in failures)
    assert any("north arrow" in item and "h 331.2" in item for item in failures)


def test_footer_fails_title_text_out_of_the_band():
    page = _footer_page()
    page["footer"]["title_block"]["all"]["y1"] = 300.0
    failures = []
    plan_smoke.check_footer(page, "sheet", failures)
    assert any("footer outside band: title" in item for item in failures)


def test_footer_fails_a_bar_running_into_the_title_block():
    page = _footer_page()
    page["footer"]["scale_bar"]["with_labels"]["x1"] = 135.0
    failures = []
    plan_smoke.check_footer(page, "sheet", failures)
    assert any("bar x1 135.0 >= title x0 130.0" in item for item in failures)
