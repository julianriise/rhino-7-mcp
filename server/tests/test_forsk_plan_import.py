"""Forsk plan import: raw Tectly JSON -> forsk.plan_import.v0, and the plan tools."""

from __future__ import annotations

import json
import math
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

import forsk_plan_import as fpi

FIXTURES = Path(__file__).resolve().parent / "fixtures"

# The synthetic page: 1200 x 900 px at 254 dpi and 1:100, so 1 px is 10 mm.
# Tectly got a 1000 x 800 crop whose top-left is at page px (100, 50), and
# found the plan in the section left 0.1, top 0.05, width 0.8, height 0.9 of it.
FRAME = dict(crop_size=(1000, 800), crop_offset=(100, 50), page_size=(1200, 900), dpi=254, scale=100)

# The real plan both detections were made from. A client drawing, so it is not
# in git: the comparison runs only where forsk-private is checked out.
PLAN1 = Path.home() / "Documents" / "hobby" / "forsk-private" / "import" / "plan1"
PLAN1_FRAME = dict(crop_size=(1423, 1684), crop_offset=(694, 327), page_size=(3308, 2339), dpi=200, scale=100)


def synthetic():
    raw = json.loads((FIXTURES / "tectly_synthetic.json").read_text(encoding="utf-8"))
    return fpi.tectly_to_plan(raw, source_file="tectly_synthetic.json", **FRAME)


def test_header_is_plan_import_v0_in_mm_y_up():
    plan = synthetic()
    assert plan["schema"] == "forsk.plan_import.v0"
    assert plan["units"] == "mm"
    assert plan["y_axis"] == "up"
    assert plan["source"] == {"vendor": "tectly", "file": "tectly_synthetic.json", "wall_tracing": "Rectangles"}
    assert plan["image"] == {"width_mm": 12000.0, "height_mm": 9000.0}


def test_scale_is_an_assumption_until_the_user_sets_it():
    scale = synthetic()["scale"]
    assert scale["status"] == "assumed"
    assert scale["ratio"] == "1:100"
    assert "Tectly found no scale" in scale["note"]


def test_walls_are_centreline_and_thickness_from_the_traced_rectangle():
    walls = synthetic()["walls"]
    # px = section, then crop, then the crop's offset on the page; y flips to up.
    assert walls[0] == {"start": [2000, -990], "end": [6000, -990], "thickness": 180.0}
    assert walls[1] == {"start": [6125, -900], "end": [6125, -4500], "thickness": 250.0}


def test_openings_keep_their_kind_and_points():
    window, door = synthetic()["openings"]
    assert window == {"kind": "window", "a": [3000, -990], "b": [4000, -990]}
    assert door == {
        "kind": "door", "hinge": [6125, -1800], "closed": [6125, -2700], "width": 900, "open": [5200, -1800],
    }


def test_rooms_are_named_by_caption_then_type_then_rom():
    rooms = synthetic()["rooms"]
    assert [(r["label"], r["ocr_text"], r["source_type"]) for r in rooms] == [
        ("Stue", "Stue", "LivingRoom"), ("Bad", None, "Bathroom"), ("Rom", None, "Other"),
    ]
    assert rooms[0]["boundary"] == [[2000, -1080], [6000, -1080], [6000, -4500], [2000, -4500]]
    assert [r["area_m2"] for r in rooms] == [13.7, 2.3, 1.2]
    assert rooms[0]["tag"] == "ca. 13,7 m²"


def test_whole_page_needs_no_offset_and_states_no_image_without_a_page_size():
    raw = json.loads((FIXTURES / "tectly_synthetic.json").read_text(encoding="utf-8"))
    plan = fpi.tectly_to_plan(raw, crop_size=(1000, 800), dpi=254)
    assert "image" not in plan
    assert plan["walls"][0]["start"] == [1000, -490]


def test_scale_and_dpi_set_the_millimetres():
    raw = json.loads((FIXTURES / "tectly_synthetic.json").read_text(encoding="utf-8"))
    half = fpi.tectly_to_plan(raw, **{**FRAME, "scale": 50})
    assert half["scale"]["ratio"] == "1:50"
    assert half["walls"][0] == {"start": [1000, -495], "end": [3000, -495], "thickness": 90.0}
    assert half["image"]["width_mm"] == 6000.0


def test_committed_plan_is_what_the_adapter_writes():
    # The plugin's headless tests read this same file: one schema on both sides.
    committed = json.loads((FIXTURES / "plan_import_synthetic.json").read_text(encoding="utf-8"))
    assert committed == synthetic()


@pytest.mark.parametrize("frame, message", [
    ({**FRAME, "dpi": 0}, "dpi and scale"),
    ({**FRAME, "scale": 0}, "dpi and scale"),
    ({**FRAME, "crop_size": (0, 800)}, "crop_size"),
    ({**FRAME, "page_size": (1200,)}, "page_size"),
])
def test_bad_frame_is_refused(frame, message):
    raw = json.loads((FIXTURES / "tectly_synthetic.json").read_text(encoding="utf-8"))
    with pytest.raises(fpi.PlanImportError, match=message):
        fpi.tectly_to_plan(raw, **frame)


def test_result_with_no_plan_is_refused():
    with pytest.raises(fpi.PlanImportError, match="no plan 0 with walls"):
        fpi.tectly_to_plan({"plans": []}, **FRAME)
    with pytest.raises(fpi.PlanImportError, match="no plan 0 with walls"):
        fpi.tectly_to_plan({"elapsed_s": 3}, **FRAME)


def test_cli_writes_the_plan_file(tmp_path, capsys):
    out = tmp_path / "plan.json"
    code = fpi.main([
        str(FIXTURES / "tectly_synthetic.json"), str(out),
        "--crop-size", "1000", "800", "--crop-offset", "100", "50",
        "--page-size", "1200", "900", "--dpi", "254",
    ])
    assert code == 0
    assert "2 walls, 2 openings, 3 rooms" in capsys.readouterr().out
    written = json.loads(out.read_text(encoding="utf-8"))
    assert written["walls"] == synthetic()["walls"]
    assert written["rooms"][0]["tag"] == "ca. 13,7 m²"


def test_cli_reports_a_missing_file(tmp_path, capsys):
    code = fpi.main([str(tmp_path / "none.json"), str(tmp_path / "out.json"), "--crop-size", "10", "10", "--dpi", "100"])
    assert code == 1
    assert "plan import:" in capsys.readouterr().err


def near(a, b, tol=10):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


@pytest.mark.skipif(not (PLAN1 / "plan1_tectly_raw.json").exists(), reason="forsk-private is not checked out")
def test_plan1_matches_the_reference_conversion_within_10_mm():
    raw = json.loads((PLAN1 / "plan1_tectly_raw.json").read_text(encoding="utf-8"))
    want = json.loads((PLAN1 / "plan1_forsk.json").read_text(encoding="utf-8"))
    got = fpi.tectly_to_plan(raw, **PLAN1_FRAME)

    assert len(got["walls"]) == len(want["walls"]) == 41
    for mine, ref in zip(got["walls"], want["walls"]):
        assert near(mine["start"], ref["start"]) and near(mine["end"], ref["end"]), (mine, ref)
        assert abs(mine["thickness"] - ref["thickness"]) <= 10, (mine, ref)

    assert len(got["openings"]) == len(want["openings"]) == 19
    for mine, ref in zip(got["openings"], want["openings"]):
        assert mine["kind"] == ref["kind"]
        for key in ("a", "b", "hinge", "closed", "open"):
            assert (key in mine) == (key in ref)
            if key in ref:
                assert near(mine[key], ref[key]), (key, mine, ref)
        if "width" in ref:
            assert abs(mine["width"] - ref["width"]) <= 10

    assert len(got["rooms"]) == len(want["rooms"]) == 11
    for mine, ref in zip(got["rooms"], want["rooms"]):
        assert (mine["label"], mine["ocr_text"], mine["source_type"]) == (ref["label"], ref["ocr_text"], ref["source_type"])
        assert len(mine["boundary"]) == len(ref["boundary"])
        assert all(near(p, q) for p, q in zip(mine["boundary"], ref["boundary"]))
        assert math.isclose(mine["area_m2"], ref["area_m2"], abs_tol=0.11)

    # The page image lines up with it: 3308 px at 200 dpi and 1:100.
    assert got["image"]["width_mm"] == pytest.approx(42011.6)


class TestPlanTools:
    """The MCP tools validate their arguments and pass the command on as the contract has it."""

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_sends_only_what_was_given(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        conn = MagicMock()
        conn.send_command.return_value = {"walls": 5, "message": "Imported 5 walls."}
        mock_get_conn.return_value = conn
        result = plan_import(ctx=None, image_path="/tmp/plan.png", plan_path="/tmp/plan.json")
        conn.send_command.assert_called_once_with(
            "plan_import", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json"})
        assert result == {"success": True, "walls": 5, "message": "Imported 5 walls."}

        plan_import(ctx=None, image_path="/tmp/plan.png", plan_path="/tmp/plan.json",
                    scale_hint="1:100", image_dpi=200, image_width_mm=42012, replace=True)
        assert conn.send_command.call_args[0][1] == {
            "image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json",
            "scale_hint": "1:100", "image_dpi": 200, "image_width_mm": 42012, "replace": True,
        }

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_needs_absolute_paths(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        assert plan_import(ctx=None, image_path="plan.png", plan_path="/tmp/plan.json") == {
            "success": False, "message": "plan_import needs an absolute image_path, or a pdf_path."}
        assert plan_import(ctx=None, image_path="/tmp/plan.png") == {
            "success": False, "message": "plan_import needs an absolute plan_path, or a pdf_path."}
        assert plan_import(ctx=None, pdf_path="plan1.pdf") == {
            "success": False, "message": "plan_import needs an absolute pdf_path."}
        assert plan_import(ctx=None, pdf_path="/tmp/plan1.pdf", image_path="/tmp/plan.png")["message"] == (
            "plan_import takes pdf_path, or image_path with plan_path, not both.")
        assert plan_import(ctx=None, pdf_path="/tmp/plan1.pdf", page=0)["message"] == "plan_import page is 1-based."
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_passes_a_pdf_and_its_page_on(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        conn = MagicMock()
        conn.send_command.return_value = {"walls": 46, "pdf": {"file": "plan1.pdf", "page": 1}}
        mock_get_conn.return_value = conn
        plan_import(ctx=None, pdf_path="/tmp/plan1.pdf")
        conn.send_command.assert_called_once_with("plan_import", {"pdf_path": "/tmp/plan1.pdf"})
        plan_import(ctx=None, pdf_path=" /tmp/plan1.pdf ", page=2, replace=True)
        assert conn.send_command.call_args[0][1] == {"pdf_path": "/tmp/plan1.pdf", "page": 2, "replace": True}

    @patch("rhinomcp.tools.plan_scale.get_rhino_connection")
    def test_plan_scale_measures_without_a_length_and_sets_with_one(self, mock_get_conn):
        from rhinomcp.tools.plan_scale import plan_scale

        conn = MagicMock()
        conn.send_command.return_value = {"measured_mm": 3900.0, "factor": 1.0}
        mock_get_conn.return_value = conn
        plan_scale(ctx=None, p1=[1000, -2000], p2=[4900, -2000])
        conn.send_command.assert_called_once_with(
            "plan_scale", {"p1": [1000, -2000], "p2": [4900, -2000], "frame": "model"})

        plan_scale(ctx=None, p1=[1000, -2000], p2=[4900, -2000], length_mm=4000, frame="source")
        assert conn.send_command.call_args[0][1] == {
            "p1": [1000, -2000], "p2": [4900, -2000], "frame": "source", "length_mm": 4000}

    @patch("rhinomcp.tools.plan_scale.get_rhino_connection")
    def test_plan_scale_refuses_bad_arguments(self, mock_get_conn):
        from rhinomcp.tools.plan_scale import plan_scale

        assert plan_scale(ctx=None, p1=[0, 0])["message"] == "plan_scale needs p1 and p2 as [x, y] in mm."
        assert plan_scale(ctx=None, p1=[0, 0], p2=[1, 0], frame="page")["message"] == "plan_scale frame must be model or source."
        assert plan_scale(ctx=None, p1=[0, 0], p2=[1, 0], length_mm=0)["message"] == "plan_scale length_mm must be above 0."
        mock_get_conn.assert_not_called()

    def test_plan_from_tectly_writes_the_plan_and_counts(self, tmp_path):
        from rhinomcp.tools.plan_from_tectly import plan_from_tectly

        out = tmp_path / "plan.json"
        result = plan_from_tectly(
            ctx=None, raw_path=str(FIXTURES / "tectly_synthetic.json"), out_path=str(out),
            crop_size=[1000, 800], crop_offset=[100, 50], page_size=[1200, 900], dpi=254)
        assert result["success"] is True
        assert (result["walls"], result["openings"], result["rooms"]) == (2, 2, 3)
        assert result["message"] == "Wrote 2 walls, 2 openings, 3 rooms to plan.json. Scale 1:100 is assumed."
        assert json.loads(out.read_text(encoding="utf-8"))["walls"] == synthetic()["walls"]

    def test_plan_from_tectly_refuses_a_missing_frame(self, tmp_path):
        from rhinomcp.tools.plan_from_tectly import plan_from_tectly

        raw = str(FIXTURES / "tectly_synthetic.json")
        assert plan_from_tectly(ctx=None, raw_path=raw, out_path="plan.json")["message"] == (
            "plan_from_tectly needs an absolute out_path.")
        assert plan_from_tectly(ctx=None, raw_path=raw, out_path=str(tmp_path / "p.json"))["message"] == (
            "plan_from_tectly needs crop_size [width, height] in px.")
        assert "dpi and scale" in plan_from_tectly(
            ctx=None, raw_path=raw, out_path=str(tmp_path / "p.json"), crop_size=[1000, 800])["message"]
