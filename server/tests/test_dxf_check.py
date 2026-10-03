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


def plan_doc(*, cut=4, hatch=True, heavy=True, number="A-20-001", off_sheet=False, layers=LAYERS):
    doc = ezdxf.new("R2000")
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
    empty = tmp_path / "b.dwg"
    empty.write_bytes(b"")
    text = tmp_path / "c.dwg"
    text.write_bytes(b"0\nSECTION\n")
    assert dxf_check.check_dwg(good) == []
    assert dxf_check.check_dwg(empty)
    assert dxf_check.check_dwg(text)
    assert dxf_check.check_dwg(tmp_path / "missing.dwg")


def test_the_plan_file_is_found_by_its_number():
    names = ["Garage A-00-001 Forside.dxf", "Garage A-20-001 Plan.dxf", "Garage A-40-101 Snitt A-A.dxf"]
    assert export_smoke.plan_file(names) == "Garage A-20-001 Plan.dxf"
    assert export_smoke.plan_file(["x.dxf"]) is None


class FakeRhino:
    def __init__(self, folder_files):
        self.folder_files = folder_files
        self.calls = []

    def __call__(self, cmd, params):
        self.calls.append((cmd, params))
        folder = Path(params["folder"])
        folder.mkdir(parents=True, exist_ok=True)
        files = []
        for name, write in self.folder_files(params["format"]):
            write(folder / name)
            files.append(name)
        return {
            "folder": str(folder), "format": params["format"], "count": len(files), "files": files,
            "writer": "headless", "misc": 0, "misc_roles": [],
            "message": f"Exported {len(files)} sheets as {params['format'].upper()} to {folder}.",
        }


def test_the_export_step_passes_on_a_right_set(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc().saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027" + b"\0" * 10))]

    rhino = FakeRhino(files)
    lines, ok = export_smoke.run(rhino, tmp_path)
    assert ok, lines
    assert [c[1]["format"] for c in rhino.calls] == ["dxf", "dwg"]
    assert len(lines) <= 25


def test_the_export_step_fails_on_a_dxf_without_poche(tmp_path):
    def files(fmt):
        if fmt == "dxf":
            return [("Garage A-20-001 Plan.dxf", lambda p: plan_doc(hatch=False).saveas(p))]
        return [("Garage A-20-001 Plan.dwg", lambda p: p.write_bytes(b"AC1027"))]

    lines, ok = export_smoke.run(FakeRhino(files), tmp_path)
    assert not ok
    assert any("HATCH" in line for line in lines)
