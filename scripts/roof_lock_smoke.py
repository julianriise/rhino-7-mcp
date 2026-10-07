#!/usr/bin/env python3
"""Roof lock smoke: the roof cannot be picked, and Forsk still replaces it.

Run on the garage after the garage smoke has built it. A-ROOF must be locked
(and hidden by default). With the layer shown, moving the south wall out must
still rebuild the roof (one roof, its south edge moved) and leave the layer
locked. The layer is hidden
again at the end. Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/roof_lock_smoke.py
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analysis_smoke  # noqa: E402  (the south wall lookup)
from option_smoke import _send  # noqa: E402

READ = """
import scriptcontext as sc, Rhino
s = Rhino.DocObjects.ObjectEnumeratorSettings(); s.HiddenObjects = True; s.LockedObjects = True
roofs = ["%.0f" % o.Geometry.GetBoundingBox(True).Min.Y for o in sc.doc.Objects.GetObjectList(s) if o.Attributes.GetUserString("forsk:kind") == "roof"]
L = sc.doc.Layers.FindName("A-ROOF")
print("ROOF %s %d %d %s" % (",".join(roofs) or "-", 1 if L and L.IsVisible else 0, 1 if L and L.IsLocked else 0, len(roofs)))
"""

SHOW = """
import scriptcontext as sc
L = sc.doc.Layers.FindName("A-ROOF")
L.IsVisible = %s
sc.doc.Layers.Modify(L, L.Index, True)
print("ok")
"""


def roof_layer(send) -> dict:
    """A-ROOF as get_document_summary reports it. Read-only: a write command (a script too) opens the layer while it runs."""

    def find(nodes):
        for node in nodes or []:
            if str(node.get("name", "")).upper() == "A-ROOF":
                return node
            hit = find(node.get("children"))
            if hit:
                return hit
        return None

    return find(send("get_document_summary", {}).get("layer_hierarchy")) or {}


def read(send) -> tuple[str, bool, bool, int]:
    out = str(send("execute_rhinoscript_python_code", {"code": READ}).get("output") or "")
    row = next((r for r in out.splitlines() if r.startswith("ROOF ")), "ROOF - 0 0 0")
    ids, _, _, count = row.split()[1:5]
    layer = roof_layer(send)
    return ids, bool(layer.get("visible")), bool(layer.get("locked")), int(count)


def run(send) -> tuple[list[str], bool]:
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    ids, visible, locked, count = read(send)
    lines.append(f"roof layer: visible {visible} locked {locked}, {count} roof(s)")
    if not locked:
        fail("A-ROOF is not locked")
    send("execute_rhinoscript_python_code", {"code": SHOW % "True"})
    wall = str(send("execute_rhinoscript_python_code", {"code": analysis_smoke.SOUTH_WALL}).get("output") or "").strip()
    try:
        moved = send("move_wall", {"id": wall, "side": "south", "toward": "south", "distance_mm": 300})
        after, _, locked_after, count_after = read(send)
        lines.append(f"wall out 300: {str(moved.get('message'))[:80]}")
        lines.append(f"roof after: {count_after} roof(s), south edge {ids} -> {after}, locked {locked_after}")
        if count_after != 1 or after == ids:
            fail("the roof was not replaced on the locked layer")
        if not locked_after:
            fail("A-ROOF is not locked after the edit")
    finally:
        send("move_wall", {"id": wall, "side": "south", "toward": "north", "distance_mm": 300})
        send("execute_rhinoscript_python_code", {"code": SHOW % "False"})
    return lines, ok


def main() -> int:
    try:
        lines, ok = run(_send)
    except OSError as e:
        lines, ok = [f"FAIL Rhino not reachable: {e}"], False
    for line in lines:
        print(line)
    print("roof lock smoke: " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
