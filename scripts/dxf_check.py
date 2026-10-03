#!/usr/bin/env python3
"""Check a sheet the R3 export wrote (export_sheets), with ezdxf.

A plan DXF must have the eight export layers, at least 4 entities on
A-WALL-CUT, a HATCH on A-WALL-PATT, a 0.50 mm lineweight on an entity or a
layer, the sheet number A-20-001 on A-ANNO-TTLB, and extents inside the A3
sheet (420 x 297 mm). A DWG must exist, not be empty, and start with AC10.

Usage:
  python3 scripts/dxf_check.py "/tmp/forsk-export-garage/Garage DXF/Garage A-20-001 Plan.dxf"
  python3 scripts/dxf_check.py "/tmp/forsk-export-garage/Garage DWG/Garage A-20-001 Plan.dwg"
Install first: python -m pip install ezdxf
"""

from __future__ import annotations

import sys
from pathlib import Path

LAYERS = (
    "A-WALL-CUT", "A-WALL-PATT", "A-SYMB", "A-ELEV",
    "A-GRND", "A-ANNO-DIMS", "A-ANNO-TEXT", "A-ANNO-TTLB",
)
SHEET_W, SHEET_H = 420.0, 297.0
# A text box is measured from its font; allow a little past the paper edge.
SLACK_MM = 1.0
HEAVY = 50  # 0.50 mm in DXF hundredths


def check_plan(doc, number: str = "A-20-001") -> list[str]:
    """What is wrong with a plan sheet DXF. Empty when it passes."""
    from ezdxf import bbox

    problems = []
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

    box = bbox.extents(msp, fast=True)
    if box.has_data:
        lo, hi = box.extmin, box.extmax
        if lo.x < -SLACK_MM or lo.y < -SLACK_MM or hi.x > SHEET_W + SLACK_MM or hi.y > SHEET_H + SLACK_MM:
            problems.append(
                f"extents {lo.x:.0f},{lo.y:.0f} to {hi.x:.0f},{hi.y:.0f} do not fit 420 x 297"
            )
    else:
        problems.append("the sheet is empty")
    return problems


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
    return []


def check_file(path, number: str = "A-20-001") -> list[str]:
    path = Path(path)
    if path.suffix.lower() == ".dwg":
        return check_dwg(path)
    import ezdxf

    try:
        doc = ezdxf.readfile(str(path))
    except (OSError, ezdxf.DXFError) as e:
        return [f"{path.name} does not read: {e}"]
    return check_plan(doc, number)


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__.strip().splitlines()[0])
        return 1
    failed = False
    for arg in argv:
        problems = check_file(arg)
        print(("FAIL " if problems else "ok   ") + Path(arg).name + ("" if not problems else ": " + "; ".join(problems)))
        failed = failed or bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
