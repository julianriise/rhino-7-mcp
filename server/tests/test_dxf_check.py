"""Headless checks for scripts/dxf_check.py (R3): a plan sheet DXF as the
export writes it passes; one without the cut, the poché hatch, the heavy
weight, the sheet number or with a piece off the A3 sheet fails. Also the
DWG header check, and the export smoke's step against a fake Rhino."""

import sys
from pathlib import Path

import pytest

ezdxf = pytest.importorskip("ezdxf")

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import dxf_check  # noqa: E402
import export_smoke  # noqa: E402

LAYERS = [
    ("A-WALL-CUT", 50), ("A-WALL-PATT", 0), ("A-SYMB", 18), ("A-ELEV", 18), ("A-GRND", 50),
    ("A-ANNO-DIMS", 13), ("A-ANNO-TEXT", 13), ("A-ANNO-TTLB", 25),
]


def plan_doc(*, cut=4, hatch=True, heavy=True, number="A-20-001", off_sheet=False, layers=LAYERS, version="R2018"):
    doc = ezdxf.new(version)
    for name, weight in layers:
        doc.layers.add(name, lineweight=weight if heavy or weight != 50 else 25)
    msp = doc.modelspace()
    for i in range(cut):
        msp.add_line((100 + i * 10, 100), (100 + i * 10, 180), dxfattribs={"layer": "A-WALL-CUT"})
    if hatch:
        h = msp.add_hatch(dxfattribs={"layer": "A-WALL-PATT"})
        h.paths.add_polyline_path([(100, 100), (110, 100), (110, 180), (100, 180)])
    msp.add_line((10, 10), (410, 10), dxfattribs={"layer": "A-ANNO-TTLB"})
    msp.add_text(number, dxfattribs={"layer": "A-ANNO-TTLB", "height": 3.5, "insert": (300, 14)})
    if off_sheet:
        msp.add_line((0, 0), (900, 0), dxfattribs={"layer": "A-ELEV"})
    return doc


def test_a_plan_as_exported_passes():
    assert dxf_check.check_plan(plan_doc()) == []


def test_a_plan_without_a_ground_line_passes():
    layers = [layer for layer in LAYERS if layer[0] != "A-GRND"]
    assert dxf_check.check_plan(plan_doc(layers=layers)) == []


@pytest.mark.parametrize(
    "changes, problem",
    [
        ({"cut": 3}, "A-WALL-CUT"),
        ({"hatch": False}, "HATCH"),
        ({"heavy": False}, "0.50"),
        ({"number": "A-20-002"}, "A-20-001"),
        ({"off_sheet": True}, "420"),
        ({"layers": LAYERS[:-1]}, "A-ANNO-TTLB"),
    ],
)
def test_a_plan_missing_a_piece_fails(changes, problem):
    problems = dxf_check.check_plan(plan_doc(**changes))
    assert problems, changes
    assert any(problem in p for p in problems), problems


def test_a_heavy_entity_counts_when_its_layer_is_light():
    doc = plan_doc(heavy=False)
    doc.modelspace().add_line((0, 0), (1, 1), dxfattribs={"layer": "A-WALL-CUT", "lineweight": 50})
    assert dxf_check.check_plan(doc) == []


def test_a_dwg_starts_with_its_version(tmp_path):
    good = tmp_path / "a.dwg"
    good.write_bytes(b"AC1027" + b"\0" * 100)
    written = tmp_path / "ac1021.dwg"
    written.write_bytes(b"AC1021" + b"\0" * 100)
    year2004 = tmp_path / "ac1018.dwg"
    year2004.write_bytes(b"AC1018" + b"\0" * 100)
    empty = tmp_path / "b.dwg"
    empty.write_bytes(b"")
    text = tmp_path / "c.dwg"
    text.write_bytes(b"0\nSECTION\n")
    assert dxf_check.check_dwg(good) == []
    assert dxf_check.check_dwg(written) == []
    assert dxf_check.check_dwg(year2004) == []
    assert dxf_check.check_dwg(empty)
    assert dxf_check.check_dwg(text)
    assert dxf_check.check_dwg(tmp_path / "missing.dwg")
    old = tmp_path / "d.dwg"
    old.write_bytes(b"AC1009" + b"\0" * 100)
    assert dxf_check.check_dwg(old) == ["d.dwg is AC1009, want AC1018 (AutoCAD 2004) or later"]
    year2000 = tmp_path / "ac1015.dwg"
    year2000.write_bytes(b"AC1015" + b"\0" * 100)
    assert dxf_check.check_dwg(year2000) == ["ac1015.dwg is AC1015, want AC1018 (AutoCAD 2004) or later"]


def test_an_r12_plan_fails_on_its_version():
    doc = ezdxf.new("R12")
    assert "version AC1009, want AC1018 (AutoCAD 2004) or later" in dxf_check.check_plan(doc)


def test_a_2007_plan_passes_the_version_bar():
    assert dxf_check.check_plan(plan_doc(version="R2007")) == []


def test_the_plan_file_is_found_by_its_number():
    names = ["Garage A-00-001 Forside.dxf", "Garage A-20-001 Plan.dxf", "Garage A-40-101 Snitt A-A.dxf"]
    assert export_smoke.plan_file(names) == "Garage A-20-001 Plan.dxf"
    assert export_smoke.plan_file(["x.dxf"]) is None


DOOR = {"attributes": {"forsk:kind": "opening_marker", "forsk:opening_kind": "door",
                        "forsk:id": "o-door", "forsk:host_id": "w01"}}


class FakeRhino:
    def __init__(self, folder_files, markers=(DOOR,)):
        self.folder_files = folder_files
        self.markers = list(markers)
        self.details = []
        self.calls = []

    def __call__(self, cmd, params):
        self.calls.append((cmd, params))
        if cmd == "get_objects":
            return {"objects": self.markers, "has_more": False}
        if cmd == "details":
            if params["action"] == "add":
                for ref in params["refs"]:
                    self.details.append({"id": f"DET{len(self.details) + 1:02d}", **ref})
            elif params["action"] == "remove":
                self.details = [d for d in self.details if d["id"] not in params["ids"]]
            return {"details": list(self.details), "count": len(self.details), "message": "ok"}
        if cmd == "export_ifc":
            Path(params["path"]).write_text("ISO-10303-21;")
            return {"path": params["path"], "walls": 4, "doors": 4, "windows": 1, "spaces": 1, "message": "ok"}
        folder = Path(params["folder"])
        folder.mkdir(parents=True, exist_ok=True)
        files = []
        for name, write in self.folder_files(params["format"]):
            write(folder / name)
            files.append(name)
        return {
            "folder": str(folder), "format": params["format"], "count": len(files), "files": files,
            "writer": "active_doc", "misc": 0, "misc_roles": [], "acad_version": "AC1032",
            "message": f"Exported {len(files)} sheets as {params['format'].upper()} to {folder}.",
        }


def test_the_export_step_passes_on_a_right_set(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p)),
                    DETAIL_FILE]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    rhino = FakeRhino(files)
    checked = []
    lines, ok = export_smoke.run(rhino, tmp_path, check_ifc=lambda p: checked.append(p) or [])
    assert ok, lines
    assert [c[0] for c in rhino.calls] == [
        "get_objects", "details", "details", "export_sheets", "export_sheets", "export_ifc", "details"]
    exports = [c[1] for c in rhino.calls if c[0] == "export_sheets"]
    assert [e["format"] for e in exports] == ["dxf", "dwg"]
    assert checked == [tmp_path / "Garage.ifc"]
    assert len(lines) <= 25
    assert "dxf: 3 files · writer active_doc · AC1032" in lines


def elevation_doc(lines=True):
    doc = ezdxf.new("R2018")
    doc.layers.add("A-ELEV")
    doc.layers.add("A-ANNO-TTLB")
    doc.modelspace().add_line((0, 0), (420, 0), dxfattribs={"layer": "A-ANNO-TTLB"})
    if lines:
        doc.modelspace().add_line((50, 50), (250, 50), dxfattribs={"layer": "A-ELEV"})
    return doc


def test_an_elevation_with_only_its_title_block_fails(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc(lines=False).saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert "FAIL Garage A-40-001 North.dxf: no facade lines on A-ELEV" in lines


def test_the_export_step_fails_on_a_bad_ifc(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027"))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: ["IfcWall 3, want 4 or more"])
    assert not ok
    assert "FAIL ifc: IfcWall 3, want 4 or more" in lines


def test_model_size_text_fails_on_a_plan():
    doc = plan_doc()
    doc.modelspace().add_mtext(
        "Room",
        dxfattribs={"layer": "A-ANNO-TEXT", "char_height": 250, "width": 2255, "insert": (210, 171)},
    )
    problems = dxf_check.check_plan(doc)
    assert any("text height" in p for p in problems), problems
    assert any("text width" in p for p in problems), problems


def test_paper_size_text_passes_on_a_plan():
    doc = plan_doc()
    doc.modelspace().add_mtext(
        "Room",
        dxfattribs={"layer": "A-ANNO-TEXT", "char_height": 2.5, "width": 22.6, "insert": (210, 171)},
    )
    assert dxf_check.check_plan(doc) == []


def test_an_elevation_with_model_size_text_fails():
    doc = elevation_doc()
    doc.modelspace().add_mtext(
        "North elevation",
        dxfattribs={"char_height": 350, "width": 7175, "insert": (20, 20)},
    )
    problems = dxf_check.check_elevation(doc)
    assert any("text height" in p for p in problems), problems


def front_doc(*, text_height=3.5, text_width=40):
    doc = ezdxf.new("R2018")
    msp = doc.modelspace()
    msp.add_line((10, 10), (400, 10))
    msp.add_mtext(
        "Drawing list",
        dxfattribs={"char_height": text_height, "width": text_width, "insert": (20, 20)},
    )
    return doc


def test_a_front_sheet_at_paper_size_passes(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [
                ("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p)),
                ("Garage A-00-001 Drawing list.dxf", lambda p: front_doc().saveas(p)),
                DETAIL_FILE,
            ]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1021" + b"\0" * 10))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert ok, lines


def test_a_front_sheet_at_model_size_fails(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [
                ("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p)),
                ("Garage A-00-001 Drawing list.dxf", lambda p: front_doc(text_height=350, text_width=10287).saveas(p)),
            ]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1021" + b"\0" * 10))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert any("A-00-001" in line and "text height" in line for line in lines), lines


def test_a_dimension_at_model_size_fails():
    doc = plan_doc()
    style = doc.dimstyles.new("ForskModel")
    style.dxf.dimtxt = 180
    style.dxf.dimasz = 100
    doc.modelspace().add_linear_dim(
        base=(100, 40), p1=(100, 50), p2=(200, 50), dimstyle="ForskModel",
    ).render()
    problems = dxf_check.check_plan(doc)
    assert any("text height" in p and "180" in p for p in problems), problems
    assert any("dimension arrow" in p and "100" in p for p in problems), problems


def detail_doc(*, heavy=True, value="200", number="A-50-001", off_sheet=False):
    doc = ezdxf.new("R2018")
    for name in ("A-WALL-CUT", "A-ANNO-DIMS", "A-ANNO-TTLB", "A-SYMB"):
        doc.layers.add(name)
    msp = doc.modelspace()
    msp.add_line((40, 100), (40, 110), dxfattribs={"layer": "A-WALL-CUT", "lineweight": 70 if heavy else 50})
    msp.add_mtext(value, dxfattribs={"layer": "A-ANNO-DIMS", "char_height": 1.8, "insert": (45, 105)})
    msp.add_text(number, dxfattribs={"layer": "A-ANNO-TTLB", "height": 3.5, "insert": (300, 14)})
    if off_sheet:
        msp.add_line((0, 0), (500, 0), dxfattribs={"layer": "A-WALL-CUT"})
    return doc


DETAIL_FILE = ("Garage A-50-001 Details 1-20.dxf", lambda p: detail_doc().saveas(p))


def test_a_detail_sheet_as_exported_passes():
    assert dxf_check.check_detail(detail_doc()) == []


@pytest.mark.parametrize(
    "changes, problem",
    [
        ({"heavy": False}, "0.70"),
        ({"value": "250"}, "200"),
        ({"number": "A-50-002"}, "A-50-001"),
        ({"off_sheet": True}, "420"),
    ],
)
def test_a_detail_sheet_missing_a_piece_fails(changes, problem):
    problems = dxf_check.check_detail(detail_doc(**changes))
    assert any(problem in p for p in problems), problems


def test_a_detail_sheet_needs_its_layers():
    doc = ezdxf.new("R2018")
    problems = dxf_check.check_detail(doc)
    assert any("A-WALL-CUT" in p and "A-ANNO-DIMS" in p and "A-ANNO-TTLB" in p for p in problems), problems


def test_expect_detail_on_the_command_line(tmp_path, capsys):
    path = tmp_path / "Garage A-50-001 Details 1-20.dxf"
    detail_doc().saveas(path)
    assert dxf_check.main(["--expect", "detail", str(path)]) == 0
    # Read as a plan it fails: no poché hatch, no A-20-001.
    assert dxf_check.main([str(path)]) == 1
    assert "FAIL" in capsys.readouterr().out


def test_the_export_smoke_adds_a_detail_and_checks_its_sheet(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p)),
                    ("Garage A-50-001 Details 1-20.dxf", lambda p: detail_doc().saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    rhino = FakeRhino(files)
    rhino.details = [{"id": "DET01", "wall": "w02"}]
    lines, ok = export_smoke.run(rhino, tmp_path, check_ifc=lambda p: [])
    assert ok, lines
    calls = [c[1] for c in rhino.calls if c[0] == "details"]
    assert calls[1] == {"action": "add", "refs": [{"wall": "w01"}, {"opening": "o-door"}]}
    # Only what it added goes again; a detail the file had stays.
    assert calls[-1] == {"action": "remove", "ids": ["DET02", "DET03"]}
    assert rhino.details == [{"id": "DET01", "wall": "w02"}]
    assert "details: 2 added (w01, o-door)" in lines


def test_the_export_smoke_fails_on_a_detail_sheet_without_its_value(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p)),
                    ("Garage A-50-001 Details 1-20.dxf", lambda p: detail_doc(value="250").saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert any(line.startswith("FAIL Garage A-50-001") and "200" in line for line in lines), lines


def test_the_export_smoke_fails_with_no_detail_sheet(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert "FAIL dxf: no detail sheet (A-50-…)" in lines


def test_the_export_smoke_without_a_door_fails_and_still_exports(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p)),
                    ("Garage A-40-001 North.dxf", lambda p: elevation_doc().saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    rhino = FakeRhino(files, markers=[])
    lines, ok = export_smoke.run(rhino, tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert "FAIL details: no door with a host wall" in lines
    assert [c[0] for c in rhino.calls].count("export_sheets") == 2


def test_the_export_step_fails_on_a_dxf_without_poche(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc(hatch=False).saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027"))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path, check_ifc=lambda p: [])
    assert not ok
    assert any("HATCH" in line for line in lines)
