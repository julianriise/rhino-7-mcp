#!/usr/bin/env python3
"""FU.7 smoke: furnish the sample house, room by room, by rules.

Run on the sample house after first_run.py has generated it and found its
rooms. Furnishes every room of a type Forsk furnishes (furnish_room all),
which must furnish each one, then again (nothing to add: each room already
has its pieces), then again with replace (the same pieces, room by room:
the rules give the same layout). A piece moved by hand stays through a
re-furnish. Lays out the plan (no room tag on a piece) and the south
facade with the furniture in, prints the plan with the furniture to
/tmp/forsk-f5-first-run-furnished.pdf (one symbol per piece), and deletes
every piece again. Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=600 python3 scripts/furnish_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys
from pathlib import Path

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "600"))
PDF_PATH = Path("/tmp/forsk-f5-first-run-furnished.pdf")


SHOWING = """
import System, Rhino
asm = [a for a in System.AppDomain.CurrentDomain.GetAssemblies() if a.GetName().Name == 'rhinomcp'][0]
print(asm.GetType('RhinoMCPPlugin.Functions.FurnishPreview').GetProperty('Showing').GetValue(None))
"""


def showing(send) -> str:
    """True while the furnish ghosts are on screen."""
    result = send("execute_rhinoscript_python_code", {"code": SHOWING})
    return str(result.get("output") or result.get("message") or "").strip()


def run(send, pdf_path: Path = PDF_PATH) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    # The two layouts as ghosts first: nothing placed, the ghosts on, then off once a layout goes in.
    shown = send("furnish_room", {"room": "all", "preview": True})
    options = shown.get("options") or []
    lines.append(f"preview: {len(options)} layouts, {' / '.join(str(o.get('count')) for o in options)} pieces, "
                 + f"same {shown.get('same')}, ghosts {showing(send)}")
    if len(options) != 2 or shown.get("count") != 0 or showing(send) != "True":
        fail(f"preview: {shown.get('message')}")
    first = send("furnish_room", {"room": "all"})
    rooms = first.get("rooms") or []
    for room in rooms:
        pieces = [a["catalog_id"] for a in room.get("added") or []]
        lines.append(f"  {room.get('room')} {room.get('type')}: {len(pieces)} pieces"
                     + (f" · left out {len(room['skipped'])}" if room.get("skipped") else "")
                     + (f" · {room['why']}" if room.get("why") else ""))
        if room.get("why"):
            fail(f"{room.get('room')} {room.get('type')} not furnished: {room['why']}")
    if showing(send) != "False":
        fail("the ghosts stayed after the furniture went in")
    if options and options[0].get("count") != first.get("count"):
        fail(f"the blue preview had {options[0].get('count')} pieces, the house got {first.get('count')}")
    total = first.get("count") or 0
    lines.append(f"furnish all: {len(rooms)} rooms, {total} pieces")
    if not rooms or total == 0:
        fail(f"furnish all: {first.get('message')}")
        return lines[:24], False

    again = send("furnish_room", {"room": "all"})
    if again.get("count") != 0:
        fail(f"furnish again added {again.get('count')} pieces to rooms that had theirs")

    redo = send("furnish_room", {"room": "all", "replace": True})
    same = [[a["catalog_id"] for a in r.get("added") or []] for r in rooms]
    now = [[a["catalog_id"] for a in r.get("added") or []] for r in redo.get("rooms") or []]
    lines.append(f"replace: {redo.get('count')} pieces, " + ("the same layout" if same == now else "a different layout"))
    if same != now:
        fail("the same rooms gave different pieces")

    # FU.8: a piece moved by hand stays through a re-furnish.
    moved_id, moved_room = None, None
    for room in redo.get("rooms") or []:
        for piece in room.get("added") or []:
            for by in ([50, 0], [-50, 0], [0, 50], [0, -50]):
                if "centre" in send("move_furniture", {"id": piece["forsk_id"], "by": by}):
                    moved_id, moved_room = piece["forsk_id"], room.get("room")
                    break
            if moved_id:
                break
        if moved_id:
            break
    if moved_id is None:
        fail("could not move any piece by hand")
    else:
        third = send("furnish_room", {"room": moved_room, "replace": True})
        kept = sum(r.get("kept", 0) for r in third.get("rooms") or [])
        still = "centre" in send("move_furniture", {"id": moved_id, "by": [0, 0]})
        lines.append(f"moved {moved_id} by hand, re-furnished {moved_room}: kept {kept}, " + ("still there" if still else "gone"))
        if kept != 1 or not still:
            fail(f"re-furnish kept {kept} and the moved piece is {'there' if still else 'gone'}")
    total_now = sum(len(r.get("added") or []) for r in (send("furnish_room", {"room": "all"}).get("rooms") or [])) or 0
    if total_now:
        fail(f"furnish after the re-furnish added {total_now}")

    pack = send("layout_pack", {"views": ["plan", "south"], "replace": True})
    plan = next((p for p in pack.get("pages") or [] if p.get("view") == "plan"), None)
    drawn = (plan or {}).get("furniture", 0)
    on_tags = (plan or {}).get("tags_on_furniture", -1)
    lines.append(f"plan: {drawn} furniture symbols at 1:{(plan or {}).get('scale', '?')}, {on_tags} room tags on furniture")
    if drawn < redo.get("count", 0) - 1 or drawn == 0:
        fail(f"plan: {drawn} symbols for about {redo.get('count')} pieces")
    if on_tags != 0:
        fail(f"{on_tags} room tags sit on furniture")
    if not any(p.get("view") == "south" for p in pack.get("pages") or []):
        fail(f"no south facade with the furniture in: {pack.get('message')}")
    if pdf_path.exists():
        pdf_path.unlink()
    send("export_pdf", {"path": str(pdf_path), "layout": "plan"})
    if pdf_path.is_file():
        lines.append(f"  {pdf_path}")
    else:
        fail("no furnished plan PDF")

    # Jump inside: the Perspective view inside the first furnished room's door, kept as a named view.
    first_room = (rooms[0] or {}).get("room") if rooms else None
    inside = send("jump_inside", {"room": first_room, "direction": "east"}) if first_room else {}
    lines.append(f"jump inside {first_room}: {inside.get('view')} eye {inside.get('eye')}")
    named = send("execute_rhinoscript_python_code", {"code": "import scriptcontext as sc\nprint(sc.doc.NamedViews.FindByName(%r) >= 0)" % str(inside.get("view"))})
    if not inside.get("view") or inside.get("direction") != "east" or str(named.get("output", "")).strip() != "True":
        fail(f"jump inside: {inside.get('message')}")
    # The window's direction card tries a shot without saving it.
    count = "import scriptcontext as sc\nprint(sc.doc.NamedViews.Count)"
    before = str(send("execute_rhinoscript_python_code", {"code": count}).get("output", "")).strip()
    tried = send("jump_inside", {"room": first_room, "direction": "south", "save": False}) if first_room else {}
    after = str(send("execute_rhinoscript_python_code", {"code": count}).get("output", "")).strip()
    lines.append(f"jump inside tried south: saved {tried.get('saved')}, named views {before} -> {after}")
    if tried.get("saved") is not False or before != after:
        fail(f"a tried shot was saved: {tried.get('message')}")

    gone = send("delete_furniture", {"all": True})
    if not gone.get("count"):
        fail(f"delete: {gone.get('message')}")
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
    print("furnish smoke: " + ("PASS" if ok else "FAIL"))
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
