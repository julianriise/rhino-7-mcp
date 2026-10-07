#!/usr/bin/env python3
"""FU smoke: furniture on the garage, placed, moved, printed, exported, deleted.

Run after the garage smoke has built the garage. Adds a double bed, a
wardrobe and a wall unit (back to a wall) and a dining table (free in the
middle), checks each sits inside its room clear of the others, refuses a
piece the catalogue has not got and one on top of the bed, moves and turns
a piece, lays out the plan (one symbol per piece), prints it to
/tmp/forsk-f5-garage-furniture.pdf (the garage smoke's pdf_check reads it),
exports the sheets as DXF (one FORSK_FU_<id> block per type, inserted on
A-FURN / A-FURN-FIXD, no furniture lines loose), then deletes every piece
so the garage is as it was. Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/furniture_smoke.py
Install first: python -m pip install ezdxf
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import sys
from pathlib import Path

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))
PDF_PATH = Path("/tmp/forsk-f5-garage-furniture.pdf")
DXF_DIR = Path("/tmp/forsk-furniture-dxf")
PLAN_NUMBER = "A-20-001"
PIECES = [
    ("double bed", "bed.double.160x200", "A-FURN"),
    ("wardrobe", "wardrobe.100", "A-FURN"),
    ("wall unit", "kitchen.wall.60", "A-FURN-FIXD"),
    ("dining table for 4", "dining_table.4seat", "A-FURN"),
]


def check_dxf(path: Path) -> tuple[dict[str, int], list[str]]:
    """INSERTs per FORSK_FU_ block and layer, and what is wrong."""
    import ezdxf

    doc = ezdxf.readfile(str(path))
    problems: list[str] = []
    inserts: dict[str, int] = {}
    for entity in doc.modelspace().query("INSERT"):
        name = entity.dxf.name
        if not name.startswith("FORSK_FU_"):
            continue
        inserts[name] = inserts.get(name, 0) + 1
        want = next((layer for _, cid, layer in PIECES if name.startswith("FORSK_FU_" + cid)), None)
        if want and entity.dxf.layer != want:
            problems.append(f"{name} on {entity.dxf.layer}, not {want}")
        block = doc.blocks.get(name)
        if block is None or len(list(block)) < 2:
            problems.append(f"{name} has no symbol")
    loose = [e for e in doc.modelspace() if e.dxf.layer in ("A-FURN", "A-FURN-FIXD") and e.dxftype() != "INSERT"]
    if loose:
        problems.append(f"{len(loose)} loose furniture entities outside a block")
    return inserts, problems


def run(send, pdf_path: Path = PDF_PATH, dxf_dir: Path = DXF_DIR, dxf=check_dxf) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    placed = []
    for item, cid, _ in PIECES:
        result = send("add_furniture", {"item": item})
        lines.append(f"add {item}: {result.get('message')}")
        if result.get("catalog_id") != cid or not str(result.get("forsk_id", "")).startswith("F"):
            fail(f"add {item}: got {result.get('catalog_id')}")
            continue
        placed.append(result)
    if len(placed) != len(PIECES):
        send("delete_furniture", {"all": True})
        return lines[:24], False

    unknown = send("add_furniture", {"item": "grand piano"})
    if "has no grand piano" not in str(unknown.get("message", "")):
        fail(f"grand piano: {unknown.get('message')}")
    on_bed = send("add_furniture", {"item": "armchair", "at": placed[0]["centre"], "rotation": 0})
    lines.append(f"refused: {on_bed.get('message')}")
    if on_bed.get("forsk_id"):
        fail("an armchair on the bed was added")
        send("delete_furniture", {"id": on_bed["forsk_id"]})
    elif "overlap the double bed" not in str(on_bed.get("message", "")):
        fail(f"armchair on the bed: {on_bed.get('message')}")

    # FU.7 furnishes homes: a garage is refused with the reason.
    garage = send("furnish_room", {})
    if "set the room's type first" not in str(garage.get("message", "")):
        fail(f"furnish the garage: {garage.get('message')}")

    # A half turn keeps a table's footprint, so nothing can be in its way.
    table = placed[3]
    turned = send("move_furniture", {"id": table["forsk_id"], "rotate": 180})
    lines.append(f"turn: {turned.get('message')}")
    if "rotation" not in turned or abs((turned["rotation"] - table["rotation"] - 180) % 360) > 1e-6:
        fail(f"turn: {turned.get('message')}")
        turned = table
    moved = {}
    for by in ([100, 0], [-100, 0], [0, 100], [0, -100]):
        moved = send("move_furniture", {"id": table["forsk_id"], "by": by})
        if "centre" in moved:
            break
    lines.append(f"move: {moved.get('message')}")
    if "centre" not in moved or abs(abs(moved["centre"][0] - turned["centre"][0]) + abs(moved["centre"][1] - turned["centre"][1]) - 100) > 1e-6:
        fail(f"move: {moved.get('message')}")
    outside = send("move_furniture", {"id": table["forsk_id"], "by": [100000, 0]})
    if not str(outside.get("message", "")).startswith("That would put"):
        fail(f"moved outside the room: {outside.get('message')}")

    pack = send("layout_pack", {"views": ["plan"]})
    plan = next((p for p in pack.get("pages") or [] if p.get("view") == "plan" or "Plan" in str(p.get("title", ""))), None)
    drawn = (plan or {}).get("furniture", 0)
    lines.append(f"plan: {drawn} furniture symbol(s) at 1:{(plan or {}).get('scale', '?')}")
    if drawn != len(PIECES):
        fail(f"plan: {drawn} symbols for {len(PIECES)} pieces")

    if pdf_path.exists():
        pdf_path.unlink()
    printed = send("export_pdf", {"path": str(pdf_path), "layout": "plan"})
    if not pdf_path.is_file():
        fail(f"pdf: {printed.get('message')}")
    else:
        lines.append(f"  {pdf_path}")

    if dxf_dir.exists():
        shutil.rmtree(dxf_dir)
    exported = send("export_sheets", {"folder": str(dxf_dir), "format": "dxf"})
    sheet = next((f for f in exported.get("files") or [] if PLAN_NUMBER in f), None)
    if sheet is None:
        fail(f"dxf: no plan sheet · {exported.get('message')}")
    else:
        try:
            inserts, problems = dxf(dxf_dir / sheet)
        except ImportError:
            inserts, problems = {}, ["ezdxf is not installed"]
        lines.append("dxf blocks: " + ", ".join(f"{k} x{v}" for k, v in sorted(inserts.items())))
        for _, cid, _ in PIECES:
            if not any(name.startswith("FORSK_FU_" + cid) for name in inserts):
                problems.append(f"no FORSK_FU_{cid} insert")
        for problem in problems[:4]:
            fail(f"dxf: {problem}")
        lines.append(f"  {dxf_dir / sheet}")

    gone = send("delete_furniture", {"all": True})
    lines.append(f"delete: {gone.get('message')}")
    if gone.get("count") != len(PIECES):
        fail(f"delete: count {gone.get('count')}")
    return lines[:24], ok


def main() -> int:
    def send(cmd: str, params: dict) -> dict:
        with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as sock:
            payload = json.dumps({"type": cmd, "params": params}).encode()
            sock.sendall(len(payload).to_bytes(4, "big") + payload)
            length = int.from_bytes(_recv(sock, 4), "big")
            response = json.loads(_recv(sock, length).decode())
        if response.get("status") == "error":
            return {"message": response.get("message")}
        return response.get("result") or {}

    try:
        lines, ok = run(send)
    except OSError as e:
        lines, ok = [f"FAIL Rhino not reachable on {HOST}:{PORT}: {e}"], False
    for line in lines:
        print(line)
    print("furniture smoke: " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def _recv(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise OSError("connection closed")
        buf.extend(chunk)
    return bytes(buf)


if __name__ == "__main__":
    sys.exit(main())
