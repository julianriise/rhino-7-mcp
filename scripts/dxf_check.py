#!/usr/bin/env python3
"""Check a sheet the R3 export wrote (export_sheets), with ezdxf.

A plan DXF must have the plan export layers (not A-GRND: a plan has no
ground line), at least 4 entities on A-WALL-CUT, a HATCH on A-WALL-PATT, a
0.50 mm lineweight on an entity or a layer, the sheet number A-20-001 on
A-ANNO-TTLB, extents inside the A3 sheet (420 x 297 mm), and text at its
printed size (a room tag about 2.5 mm, not the template's x100). A DWG must
exist, not be empty, and start with AC10. Either must be AutoCAD 2004
(AC1018) or later. Hatches and lineweights exist from AutoCAD 2000; only
R12 (AC1009) drops them.

With --expect detail a DXF is a detail sheet (A-50-…): layers A-WALL-CUT,
A-ANNO-DIMS and A-ANNO-TTLB, a 0.70 mm lineweight on an entity, a dimension
value 200 on A-ANNO-DIMS, its number (from the file name, else A-50-001) on
A-ANNO-TTLB, extents inside the A3 sheet, and LINE / TEXT / HATCH / polyline
extents together (title underline within 5 mm of its title, dimension text
beside its lines).

Usage:
  python3 scripts/dxf_check.py "/tmp/forsk-export-garage/Garage DXF/Garage A-20-001 Plan.dxf"
  python3 scripts/dxf_check.py "/tmp/forsk-export-garage/Garage DWG/Garage A-20-001 Plan.dwg"
  python3 scripts/dxf_check.py --expect detail "/tmp/forsk-export-garage/Garage DXF/Garage A-50-001 Details 1-20.dxf"
Install first: python -m pip install ezdxf
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# A plan has no ground line, so A-GRND is not required here. Elevations and
# sections are not layer-checked (check_elevation only wants facade lines).
LAYERS = (
    "A-WALL-CUT", "A-WALL-PATT", "A-SYMB", "A-ELEV",
    "A-ANNO-DIMS", "A-ANNO-TEXT", "A-ANNO-TTLB",
)
SHEET_W, SHEET_H = 420.0, 297.0
# A text box is measured from its font; allow a little past the paper edge.
SLACK_MM = 1.0
# Printed sheet text is a few millimetres (room tag 2.5, title 3.5). The mm
# template's dimension scale leaves the same text at 125 mm and up.
PAPER_TEXT_MM = 12.0
HEAVY = 50  # 0.50 mm in DXF hundredths
MODERN = "AC1018"  # AutoCAD 2004
MODERN_NAME = "AutoCAD 2004"
DETAIL_LAYERS = ("A-WALL-CUT", "A-ANNO-DIMS", "A-ANNO-TTLB")
DETAIL_HEAVY = 70  # the cut pen at 1:20
DETAIL_VALUE = "200"  # the garage wall's thickness
DETAIL_NUMBER = re.compile(r"A-50-\d{3}")
# Title rule vs title text, dim value vs dim line, cut vs poché: one move.
TOGETHER_MM = 5.0


def check_plan(doc, number: str = "A-20-001") -> list[str]:
    """What is wrong with a plan sheet DXF. Empty when it passes."""
    problems = []
    if doc.dxfversion < MODERN:
        problems.append(f"version {doc.dxfversion}, want {MODERN} ({MODERN_NAME}) or later")
    names = {layer.dxf.name.upper() for layer in doc.layers}
    missing = [name for name in LAYERS if name not in names]
    if missing:
        problems.append("missing layers: " + ", ".join(missing))

    msp = doc.modelspace()
    on = lambda layer: [e for e in msp if e.dxf.get("layer", "0").upper() == layer]  # noqa: E731

    cut = on("A-WALL-CUT")
    if len(cut) < 4:
        problems.append(f"A-WALL-CUT has {len(cut)} entities, want 4 or more")
    if not any(e.dxftype() == "HATCH" for e in on("A-WALL-PATT")):
        problems.append("no HATCH on A-WALL-PATT")

    heavy = any(layer.dxf.get("lineweight", -3) == HEAVY for layer in doc.layers)
    heavy = heavy or any(e.dxf.get("lineweight", -1) == HEAVY for e in msp)
    if not heavy:
        problems.append("no entity or layer has lineweight 0.50 mm")

    texts = [_text(e) for e in on("A-ANNO-TTLB") if e.dxftype() in ("TEXT", "MTEXT")]
    if not any(number in t for t in texts):
        problems.append(f"A-ANNO-TTLB has no text {number}")

    problems.extend(check_fit(doc))
    return problems


def check_detail(doc, number: str = "A-50-001") -> list[str]:
    """What is wrong with a detail sheet DXF. Empty when it passes."""
    problems = []
    names = {layer.dxf.name.upper() for layer in doc.layers}
    missing = [name for name in DETAIL_LAYERS if name not in names]
    if missing:
        problems.append("missing layers: " + ", ".join(missing))

    msp = doc.modelspace()
    if not any(e.dxf.get("lineweight", -1) == DETAIL_HEAVY for e in msp):
        problems.append("no entity has lineweight 0.70 mm")
    values = [_text(e).strip() for e in msp
              if e.dxftype() in ("TEXT", "MTEXT") and e.dxf.get("layer", "0").upper() == "A-ANNO-DIMS"]
    if DETAIL_VALUE not in values:
        problems.append(f"A-ANNO-DIMS has no value {DETAIL_VALUE}")
    texts = [_text(e) for e in msp
             if e.dxftype() in ("TEXT", "MTEXT") and e.dxf.get("layer", "0").upper() == "A-ANNO-TTLB"]
    if not any(number in t for t in texts):
        problems.append(f"A-ANNO-TTLB has no text {number}")

    problems.extend(check_together(doc))
    problems.extend(check_fit(doc))
    return problems


def check_together(doc) -> list[str]:
    """LINE, TEXT, HATCH and polyline extents sit together after one move.

    The title underline is within 5 mm of its title text, dimension text
    sits beside its dimension lines, and a cut LINE stays on its poché.
    Empty when those pairs are missing or already together.
    """
    from ezdxf import bbox

    problems = []
    msp = list(doc.modelspace())

    def boxes(layer, types):
        found = []
        for entity in msp:
            if entity.dxftype() not in types:
                continue
            if entity.dxf.get("layer", "0").upper() != layer:
                continue
            box = bbox.extents([entity], fast=True)
            if box.has_data:
                found.append((box.extmin.x, box.extmin.y, box.extmax.x, box.extmax.y))
        return found

    def gap(a, b):
        dx = max(0.0, a[0] - b[2], b[0] - a[2])
        dy = max(0.0, a[1] - b[3], b[1] - a[3])
        return max(dx, dy)

    def nearest(ones, others):
        return min(gap(a, b) for a in ones for b in others)

    titles = boxes("A-ANNO-TEXT", ("TEXT", "MTEXT"))
    rules = boxes("A-ANNO-TEXT", ("LINE",))
    if titles and rules and nearest(titles, rules) > TOGETHER_MM:
        problems.append("title underline is not within 5 mm of its title text")

    values = boxes("A-ANNO-DIMS", ("TEXT", "MTEXT"))
    dims = boxes("A-ANNO-DIMS", ("LINE", "LWPOLYLINE", "POLYLINE"))
    if values and dims and nearest(values, dims) > TOGETHER_MM:
        problems.append("dimension text is not beside its dimension lines")

    cuts = boxes("A-WALL-CUT", ("LINE", "LWPOLYLINE", "POLYLINE"))
    fills = boxes("A-WALL-PATT", ("HATCH",))
    if cuts and fills and nearest(cuts, fills) > TOGETHER_MM:
        problems.append("LINE and HATCH extents do not move together")
    return problems


def sheet_number(name: str, default: str = "A-50-001") -> str:
    """The detail sheet's number in its file name."""
    found = DETAIL_NUMBER.search(name)
    return found.group(0) if found else default


def check_text(doc) -> list[str]:
    """Text height and width in paper mm. Empty when every text is printed size.

    Dimension values are MTEXT. A DIMENSION's text and arrow, when one is
    present, use the same paper cap.
    """
    problems = []
    height_noted = False
    width_noted = False
    arrow_noted = False
    for entity in doc.modelspace():
        kind = entity.dxftype()
        height, width, arrow = _text_size(entity)
        if kind not in ("TEXT", "MTEXT", "DIMENSION"):
            continue
        if not height_noted and height > PAPER_TEXT_MM:
            problems.append(
                f"text height {height:.0f} mm is model size, want paper mm (<= {PAPER_TEXT_MM:.0f})"
            )
            height_noted = True
        if not width_noted and width > SHEET_W + SLACK_MM:
            problems.append(f"text width {width:.0f} mm is wider than the 420 mm sheet")
            width_noted = True
        if not arrow_noted and arrow > PAPER_TEXT_MM:
            problems.append(
                f"dimension arrow {arrow:.0f} mm is model size, want paper mm (<= {PAPER_TEXT_MM:.0f})"
            )
            arrow_noted = True
        if height_noted and width_noted and arrow_noted:
            break
    return problems


def check_fit(doc) -> list[str]:
    """Paper-size text, and the drawing inside the A3 sheet. Empty when it fits."""
    from ezdxf import bbox

    problems = check_text(doc)
    box = bbox.extents(doc.modelspace(), fast=True)
    if box.has_data:
        lo, hi = box.extmin, box.extmax
        if lo.x < -SLACK_MM or lo.y < -SLACK_MM or hi.x > SHEET_W + SLACK_MM or hi.y > SHEET_H + SLACK_MM:
            problems.append(
                f"extents {lo.x:.0f},{lo.y:.0f} to {hi.x:.0f},{hi.y:.0f} do not fit 420 x 297"
            )
    else:
        problems.append("the sheet is empty")
    return problems


def _text_size(entity) -> tuple[float, float, float]:
    """Height, width and arrow length in mm. Width and arrow are 0 when absent."""
    kind = entity.dxftype()
    if kind == "MTEXT":
        return float(entity.dxf.get("char_height", 0) or 0), float(entity.dxf.get("width", 0) or 0), 0.0
    if kind == "TEXT":
        return float(entity.dxf.get("height", 0) or 0), 0.0, 0.0
    if kind != "DIMENSION":
        return 0.0, 0.0, 0.0
    style_name = entity.dxf.get("dimstyle", "")
    style = entity.doc.dimstyles.get(style_name) if style_name else None
    if style is None:
        return 0.0, 0.0, 0.0
    return float(style.dxf.get("dimtxt", 0) or 0), 0.0, float(style.dxf.get("dimasz", 0) or 0)


def check_elevation(doc) -> list[str]:
    """An elevation or section (A-40-…) has facade lines, paper-size text and the A3 sheet."""
    problems = []
    if not any(e.dxf.get("layer", "0").upper() == "A-ELEV" for e in doc.modelspace()):
        problems.append("no facade lines on A-ELEV")
    problems.extend(check_fit(doc))
    return problems


def read_dxf(path):
    """The DXF document, or the one-line reason it does not read."""
    import ezdxf

    try:
        return ezdxf.readfile(str(path)), None
    except (OSError, ezdxf.DXFError) as e:
        return None, f"{Path(path).name} does not read: {e}"


def _text(entity) -> str:
    if entity.dxftype() == "MTEXT":
        return entity.plain_text()
    return entity.dxf.get("text", "")


def check_dwg(path) -> list[str]:
    """A DWG that exists, is not empty and starts with its AC10xx version."""
    path = Path(path)
    if not path.is_file():
        return [f"{path.name} is missing"]
    head = path.read_bytes()[:6]
    if not head:
        return [f"{path.name} is empty"]
    if not head.startswith(b"AC10"):
        return [f"{path.name} does not start with AC10"]
    if head.decode("ascii", "replace") < MODERN:
        return [f"{path.name} is {head.decode('ascii', 'replace')}, want {MODERN} ({MODERN_NAME}) or later"]
    return []


def check_file(path, number: str = "A-20-001", expect: str = "plan") -> list[str]:
    path = Path(path)
    if path.suffix.lower() == ".dwg":
        return check_dwg(path)
    doc, why = read_dxf(path)
    if why:
        return [why]
    if expect == "detail":
        return check_detail(doc, sheet_number(path.name))
    return check_plan(doc, number)


def main(argv: list[str]) -> int:
    expect = "plan"
    if argv[:1] == ["--expect"]:
        if len(argv) < 2 or argv[1] not in ("plan", "detail"):
            print("--expect is plan or detail")
            return 1
        expect, argv = argv[1], argv[2:]
    if not argv:
        print(__doc__.strip().splitlines()[0])
        return 1
    failed = False
    for arg in argv:
        problems = check_file(arg, expect=expect)
        print(("FAIL " if problems else "ok   ") + Path(arg).name + ("" if not problems else ": " + "; ".join(problems)))
        failed = failed or bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
