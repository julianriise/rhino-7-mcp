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
    [(34.55, 100), (100, 100), (100.01, 200), (137, 200), (200, 200), (230, 500), (900, 500), (0, 100)],
)
def test_ladder_step_matches_sheet_scale(need, scale):
    # SheetScale.Pick for one drawing: 1:100, 1:200, 1:500; 1:50 only when asked.
    assert plan_smoke.ladder_step(need) == scale


def _fit_page(scale, need, fitted=True):
    return {"scale": scale, "fit_need": need, "fill": round(need * 0.9 / scale, 3), "fitted": fitted}


def test_check_fit_takes_the_first_ladder_step_the_plan_fits(capsys):
    failures = []
    plan_smoke.check_fit(_fit_page(200, 118.4), "office fit", failures)
    plan_smoke.check_fit(_fit_page(100, 31.0), "sheet", failures)
    assert failures == []
    # The office's fill at 1:200 is under the old 0.6 floor and still passes.
    assert capsys.readouterr().out == "    office fit plan fit 1:200 fill 0.53\n    sheet plan fit 1:100 fill 0.28\n"


def test_check_fit_fails_off_the_ladder_or_when_the_plan_does_not_fit():
    failures = []
    plan_smoke.check_fit(_fit_page(125, 118.4), "office fit", failures)
    plan_smoke.check_fit(_fit_page(100, 118.4), "office fit", failures)
    plan_smoke.check_fit(_fit_page(200, 118.4, fitted=False), "office fit", failures)
    assert failures == [
        "office fit plan fit 1:125 want 1:200 need 118.4 fill 0.85",
        "office fit plan fit 1:100 want 1:200 need 118.4 fill 1.07",
        "office fit plan fit 1:200 want 1:200 need 118.4 fill 0.53",
    ]


def test_check_fit_checks_the_fill_floor_only_at_the_step_the_plan_chose():
    # Another sheet put the set at 1:500: the plan's low fill is not a fault.
    failures = []
    plan_smoke.check_fit(_fit_page(500, 118.4), "office fit", failures)
    assert failures == []
    # At the step the plan chose, its fill must be over the step below's share.
    page = _fit_page(200, 118.4)
    page["fill"] = 0.40
    plan_smoke.check_fit(page, "office fit", failures)
    assert failures == ["office fit plan fit 1:200 want 1:200 need 118.4 fill 0.40"]


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


def test_footer_reads_the_norwegian_title_block_by_its_english_keys():
    # v3 P3: captions are Norwegian, forsk:cell stays the English key, and a
    # revision that is not set has no cell at all.
    cells = [
        {"name": "drawing", "caption": "Tegning", "text": "Plan 1. etg"},
        {"name": "number", "caption": "Tegningsnr.", "text": "A-20-001"},
        {"name": "scale", "caption": "Målestokk", "text": "1:50"},
        {"name": "sheet", "caption": "Format", "text": "A3"},
        {"name": "date", "caption": "Dato", "text": "2026-10-03"},
        {"name": "project", "caption": "Prosjekt", "text": "Holmen"},
    ]
    failures = []
    plan_smoke.check_footer(_footer_page(cells=cells), "sheet", failures)
    assert failures == []


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


def _laid_out():
    return [{"view": "plan", "page": "Forsk — Plan"}, {"view": "schedules", "page": "Forsk — Schedules"}]


def test_every_laid_out_page_is_written():
    failures = []
    pdf = {"pages": ["Forsk — Plan", "Forsk — Schedules"], "message": "Wrote 2 page(s) to /tmp/x.pdf."}
    assert plan_smoke.check_pages_written(_laid_out(), pdf, "sheet", failures) == 2
    assert failures == []


def test_a_page_left_out_of_the_pdf_goes_red():
    """F5.2 smoke: laid out 2, the schedules preview stayed blank, and export wrote 1."""
    failures = []
    message = "Wrote 1 page(s) to /tmp/x.pdf. Blank preview: Forsk — Schedules /tmp/forsk-print-page-2.png."
    pdf = {"pages": ["Forsk — Plan"], "message": message}
    assert plan_smoke.check_pages_written(_laid_out(), pdf, "office fit", failures) == 1
    assert failures == [f"office fit schedules page not in the PDF: {message}"]


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


def _dims_case():
    """An 8 x 4 m plan at 1:100 with one door at x 1200 on the south wall:
    its facade chain, the four overalls, the poché, a room tag and the
    door's mark, as layout_pack stamps them."""
    door = "0f0e0d0c-0000-4000-8000-000000000001"

    def obj(role, box, text=None, **attrs):
        attributes = {"forsk:role": role, **{f"forsk:{k}": str(v) for k, v in attrs.items()}}
        row = {"attributes": attributes, "bounding_box": [[box[0], box[1], 0], [box[2], box[3], 0]]}
        if text is not None:
            row["text"] = text
        return row

    def value(chain, kind, side, number, total, span, box, **ends):
        spans = ";".join(f"{x},{y}" for x, y in span)
        return obj("dimension", box, str(number), symbol="text", dim_chain=chain, dim_kind=kind, dim_side=side,
                   dim_value=number, dim_total=total, dim_span=spans, paper_height=1.8, **ends)

    rows = [
        obj("section_fill", (0, 0, 8000, 4000)),
        obj("room_tag", (3500, 1800, 4500, 2300), "Rom"),
        obj("opening_mark", (1100, -260, 1300, -130), "D01"),
        obj("symbol", (750, -900, 1650, 0)),
        value("facade-S-1", "facade", "S", 1200, 8000, [(0, 0), (1200, 0)], (450, -1100, 750, -960), dim_to=door),
        value("facade-S-1", "facade", "S", 6800, 8000, [(1200, 0), (8000, 0)], (4400, -1100, 4800, -960), dim_from=door),
        value("overall-S-2", "overall", "S", 8000, 8000, [(0, 0), (8000, 0)], (3800, -1800, 4200, -1660)),
        value("overall-N-3", "overall", "N", 8000, 8000, [(0, 4000), (8000, 4000)], (3800, 4660, 4200, 4800)),
        value("overall-E-4", "overall", "E", 4000, 4000, [(8000, 0), (8000, 4000)], (8560, 1800, 8700, 2200)),
        value("overall-W-5", "overall", "W", 4000, 4000, [(0, 0), (0, 4000)], (-700, 1800, -560, 2200)),
        obj("dimension", (0, -1200, 8000, -1200), symbol="line", dim_chain="facade-S-1", dim_kind="facade"),
        obj("dimension", (0, -1450, 8000, -1450), symbol="line", dim_chain="overall-S-2", dim_kind="overall"),
    ]
    markers = [{"id": door, "bounding_box": [[750, 0, 0], [1650, 200, 2100]]}]
    page = {"dimensions": {"openings": 1, "openings_shown": 1, "collisions": 0}}
    return rows, page, markers


def test_dimensions_read_back_as_the_model(capsys):
    rows, page, markers = _dims_case()
    failures = []
    counts = plan_smoke.check_dimensions(rows, page, "sheet", failures, markers)
    assert failures == []
    assert counts == {"chains": 5, "exterior": 5, "exterior_values": 6, "room": 0}
    assert capsys.readouterr().out == (
        "    sheet dims chains 5 exterior 5 values 6 room 0, sums 5/5, spans 6/6, overall 8000x4000, "
        "centres 2/2, openings 1/1, paper 1.8, clear\n"
    )


def _set(row, key, value):
    row["attributes"][f"forsk:{key}"] = str(value)


@pytest.mark.parametrize(
    "break_it, reason",
    [
        (lambda rows: _set(rows[4], "dim_value", 1300), "facade-S-1 adds [1300.0, 6800.0] to 8000.0"),
        (lambda rows: _set(rows[5], "dim_span", "1200,0;7900,0"), "facade-S-1 6800.0 spans 6700.0"),
        (lambda rows: rows[0].update(bounding_box=[[0, 0, 0], [8200, 4000, 0]]), "poché 8200 x 4000"),
        (lambda rows: _set(rows[4], "dim_span", "0,0;1500,0") or _set(rows[4], "dim_value", 1500)
         or _set(rows[5], "dim_value", 6500) or _set(rows[5], "dim_span", "1500,0;8000,0"), "mm off 0f0e0d0c's centre"),
        (lambda rows: rows[2].update(bounding_box=[[450, -1100, 0], [700, -980, 0]]), "facade-S-1 1200 on a opening_mark"),
        (lambda rows: rows[7].update(bounding_box=[[3800, -1500, 0], [4200, -1400, 0]]), "overall-N-3 8000 on a dimension line"),
        (lambda rows: _set(rows[6], "paper_height", 2.5), "value paper [1.8, 2.5] mm expected 1.8"),
    ],
)
def test_dimensions_that_drift_or_touch_go_red(break_it, reason):
    rows, page, markers = _dims_case()
    break_it(rows)
    failures = []
    plan_smoke.check_dimensions(rows, page, "sheet", failures, markers)
    assert failures and reason in failures[0], failures


def test_an_opening_off_every_chain_or_a_stuck_value_goes_red():
    rows, page, markers = _dims_case()
    page["dimensions"].update(openings=2, collisions=1)
    failures = []
    plan_smoke.check_dimensions(rows, page, "sheet", failures, markers)
    assert failures and "openings on a chain 1/2" in failures[0] and "1 values with no clear spot" in failures[0]


def test_no_dimensions_on_the_plan_goes_red(capsys):
    failures = []
    plan_smoke.check_dimensions([], {}, "office fit", failures)
    assert failures == ["office fit dims: no dimensions on the plan"]


def _leader_case():
    ring = "0,0;1300,0;1300,1100;0,1100"

    def tag(part, box):
        return {"attributes": {"forsk:role": "room_tag", "forsk:tag": part, "forsk:room_id": "rd-03", "forsk:leader": "1",
                               "forsk:room": ring, "forsk:text_height": "312.5"},
                "bounding_box": [[box[0], box[1], 0], [box[2], box[3], 0]]}

    line = {"attributes": {"forsk:role": "room_leader", "forsk:symbol": "line", "forsk:room_id": "rd-03",
                           "forsk:line": "650,550;1925,550"}, "bounding_box": [[650, 550, 0], [1925, 550, 0]]}
    return [tag("name", (2000, 560, 3700, 900)), tag("area", (2000, 200, 3600, 480)), line]


def test_a_leader_tag_outside_its_room_passes():
    failures = []
    assert plan_smoke.check_leaders(_leader_case(), "office fit", failures) == 1
    assert failures == []


def test_a_leader_tag_without_its_leader_goes_red():
    rows = _leader_case()[:2]
    failures = []
    plan_smoke.check_leaders(rows, "office fit", failures)
    assert failures == ["office fit leaders: rd-03 leader missing"]
