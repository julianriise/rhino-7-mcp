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


def _schedule_case():
    markers = [
        {"attributes": {"forsk:kind": "opening_marker", "forsk:opening_kind": "door", "forsk:mark": "D01"}},
        {"attributes": {"forsk:kind": "opening_marker", "forsk:opening_kind": "door", "forsk:mark": "D02"}},
        {"attributes": {"forsk:kind": "opening_marker", "forsk:opening_kind": "window", "forsk:mark": "V01"}},
    ]
    plan_rows = [{"text": m, "attributes": {"forsk:role": "opening_mark"}} for m in ("D01", "D02", "V01")] + [
        {"text": "Kontor", "attributes": {"forsk:role": "room_tag", "forsk:tag": "name", "forsk:room_id": "rd-01"}},
        {"text": "ca. 12,4 m²", "attributes": {"forsk:role": "room_tag", "forsk:tag": "area", "forsk:room_id": "rd-01"}},
    ]
    sheets = [{"view": "schedules", "schedules": {"lists": {
        "door": {"rows": [{"id": "D01", "cells": ["D01"]}, {"id": "D02", "cells": ["D02"]}]},
        "window": {"rows": [{"id": "V01", "cells": ["V01"]}]},
        "room": {"rows": [{"id": "rd-01", "cells": ["Kontor", "12,4 m²"]}], "total": ["Sum", "12,4 m²"]},
    }}}]
    return sheets, plan_rows, markers, {"room_tags": 1}


def test_schedules_match_the_model(capsys):
    failures = []
    plan_smoke.check_schedules(*_schedule_case(), "sheet", failures)
    assert failures == []
    assert capsys.readouterr().out == (
        "    sheet schedules doors 2 windows 1 rooms 1 marks 3/3, rooms as tagged, cells fit, pages 1\n"
    )


@pytest.mark.parametrize("break_it, reason", [
    (lambda r, p, m, g: p.pop(0), "plan marks 2 are not the markers' 3"),
    (lambda r, p, m, g: r[0]["schedules"]["lists"]["door"]["rows"].pop(), "rows doors 1/2"),
    (lambda r, p, m, g: m[1]["attributes"].update({"forsk:mark": "D01"}), "a mark twice"),
    (lambda r, p, m, g: r[0]["schedules"]["lists"]["room"]["rows"][0].update({"cells": ["Kontor", "12,5 m²"]}),
     "rooms not as tagged"),
    (lambda r, p, m, g: g.update({"room_tags": 2}), "room rows 1 for 2 tagged rooms"),
])
def test_schedules_that_drift_from_the_model_fail(break_it, reason):
    case = _schedule_case()
    break_it(*case)
    failures = []
    plan_smoke.check_schedules(*case, "sheet", failures)
    assert len(failures) == 1 and reason in failures[0], failures


def test_a_marker_without_its_plan_mark_goes_red():
    """The plan dropped V01's label: the schedule row is there, the mark on the plan is not."""
    sheets, plan_rows, markers, page = _schedule_case()
    plan_rows = [row for row in plan_rows if row.get("text") != "V01"]
    failures = []
    plan_smoke.check_schedules(sheets, plan_rows, markers, page, "sheet", failures)
    assert failures and "plan marks 2 are not the markers' 3" in failures[0]


def test_a_marker_that_never_got_a_mark_goes_red():
    sheets, plan_rows, markers, page = _schedule_case()
    del markers[2]["attributes"]["forsk:mark"]
    failures = []
    plan_smoke.check_schedules(sheets, plan_rows, markers, page, "sheet", failures)
    assert failures and "a marker without a mark" in failures[0]


def test_no_markers_read_says_so():
    sheets, plan_rows, _, page = _schedule_case()
    failures = []
    plan_smoke.check_schedules(sheets, plan_rows, [], page, "sheet", failures)
    assert failures and "read 0 opening markers from A-OPEN" in failures[0]


def test_opening_markers_asks_for_the_hidden_layer():
    """Rhino's default object list skips A-OPEN, which Forsk keeps off: the
    garage smoke read 0 markers. The fake answers like Rhino does."""
    marker = {"id": "m1", "attributes": {"forsk:kind": "opening_marker", "forsk:mark": "D01"}}
    block = {"id": "b1", "attributes": {"forsk:kind": "opening"}}

    def send(command, params):
        assert command == "get_objects" and params["layer_filter"] == "A-OPEN"
        visible = params.get("include_hidden") is True
        return {"objects": [marker, block] if visible else [], "has_more": False}

    assert plan_smoke.opening_markers(send) == [marker]


def test_a_cell_wider_than_its_column_goes_red():
    sheets, plan_rows, markers, page = _schedule_case()
    sheets[0]["schedules"]["cells_over"] = ["Sidehengslet 17.9/18 mm"]
    failures = []
    plan_smoke.check_schedules(sheets, plan_rows, markers, page, "sheet", failures)
    assert failures and "1 cells wider than their column: Sidehengslet" in failures[0]


def _office_rooms_case():
    # Two rooms side by side, wall on x = 5000 between them (200 thick), outer wall on y = 0.
    rings = [
        (["Kontor"], [(100, 100), (4900, 100), (4900, 4000), (100, 4000)]),
        (["Gang"], [(5100, 100), (9900, 100), (9900, 4000), (5100, 4000)]),
    ]
    rooms = [{"id": "rd-01", "name": "Kontor", "x": 2500, "y": 2000}, {"id": "rd-02", "name": "Gang", "x": 7500, "y": 2000}]
    markers = [
        {"attributes": {"forsk:mark": "D01"}, "bounding_box": [[4900, 1000, 0], [5100, 1900, 2100]]},
        {"attributes": {"forsk:mark": "V01"}, "bounding_box": [[1800, -100, 900], [3000, 100, 2100]]},
    ]
    sheets = [{"schedules": {"lists": {
        "door": {"rows": [{"id": "D01", "cells": ["D01", "Slagdør", "900 × 2100", "V inn", "Kontor / Gang"]}]},
        "window": {"rows": [{"id": "V01", "cells": ["V01", "Sidehengslet", "1200 × 1200", "900", "Kontor"]}]},
    }}}]
    return sheets, markers, rooms, rings


def test_opening_rooms_match_the_plan(capsys):
    failures = []
    plan_smoke.check_opening_rooms(*_office_rooms_case(), "office fit", failures)
    assert failures == []
    assert capsys.readouterr().out == "    office fit opening rooms 2/2 as the plan\n"


@pytest.mark.parametrize("row, cell, reason", [
    ("window", "–", "V01 '–' expected 'Kontor'"),
    ("window", "Rom", "V01 'Rom' expected 'Kontor'"),
    ("door", "Kontor", "D01 'Kontor' expected 'Kontor / Gang'"),
])
def test_opening_rooms_placeholder_or_wrong_room_goes_red(row, cell, reason):
    sheets, markers, rooms, rings = _office_rooms_case()
    sheets[0]["schedules"]["lists"][row]["rows"][0]["cells"][4] = cell
    failures = []
    plan_smoke.check_opening_rooms(sheets, markers, rooms, rings, "office fit", failures)
    assert failures and reason in failures[0], failures


def _marks_case():
    def text(role, value, box, paper=None):
        attributes = {"forsk:role": role}
        if paper is not None:
            attributes["forsk:paper_height"] = str(paper)
        return {"text": value, "attributes": attributes, "bounding_box": [[box[0], box[1], 0], [box[2], box[3], 0]]}

    rows = [
        text("room_tag", "Wet Room", (1000, 1000, 3000, 1300)),
        text("room_tag", "ca. 1,4 m²", (1200, 700, 2800, 950)),
        text("opening_mark", "D08", (1000, 200, 1300, 360), 1.25),
        text("opening_mark", "V01", (5000, 200, 5300, 360), 1.25),
    ]
    return rows, {"marks_on_tags": []}


def test_marks_clear_of_tags(capsys):
    rows, page = _marks_case()
    failures = []
    plan_smoke.check_marks(rows, page, "office fit", failures)
    assert failures == []
    assert capsys.readouterr().out == "    office fit marks 2 paper 1.25 clear of tags 2/2\n"


def test_a_mark_on_a_room_tag_goes_red():
    """D08 printed over Wet Room's name, as on the office sheet."""
    rows, page = _marks_case()
    rows[2]["bounding_box"] = [[1500, 1100, 0], [1800, 1260, 0]]
    failures = []
    plan_smoke.check_marks(rows, page, "office fit", failures)
    assert failures and "1 marks on room tags: D08 on 'Wet Room'" in failures[0]


def test_a_full_size_mark_goes_red():
    rows, page = _marks_case()
    rows[3]["attributes"]["forsk:paper_height"] = "2.5"
    failures = []
    plan_smoke.check_marks(rows, page, "office fit", failures)
    assert failures and "mark paper [1.25, 2.5] mm expected 1.25" in failures[0]


def test_a_mark_layout_pack_could_not_place_goes_red():
    rows, page = _marks_case()
    page["marks_on_tags"] = ["D10"]
    failures = []
    plan_smoke.check_marks(rows, page, "office fit", failures)
    assert failures and "no clear spot for D10" in failures[0]
