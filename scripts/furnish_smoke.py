#!/usr/bin/env python3
"""FU.7 smoke: furnish the sample house, room by room, by rules.

Run on the sample house after first_run.py has generated it and found its
rooms. Furnishes every room of a type Forsk furnishes (furnish_room all),
which must furnish each one, then again (nothing to add: each room already
has its pieces), then again with replace (the same pieces, room by room:
the rules give the same layout). Prints the plan with the furniture to
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


def run(send, pdf_path: Path = PDF_PATH) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    first = send("furnish_room", {"room": "all"})
    rooms = first.get("rooms") or []
    for room in rooms:
        pieces = [a["catalog_id"] for a in room.get("added") or []]
        lines.append(f"  {room.get('room')} {room.get('type')}: {len(pieces)} pieces"
                     + (f" · left out {len(room['skipped'])}" if room.get("skipped") else "")
                     + (f" · {room['why']}" if room.get("why") else ""))
        if room.get("why"):
            fail(f"{room.get('room')} {room.get('type')} not furnished: {room['why']}")
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

    pack = send("layout_pack", {"views": ["plan"], "replace": True})
    plan = next((p for p in pack.get("pages") or [] if p.get("view") == "plan"), None)
    drawn = (plan or {}).get("furniture", 0)
    lines.append(f"plan: {drawn} furniture symbols at 1:{(plan or {}).get('scale', '?')}")
    if drawn != redo.get("count"):
        fail(f"plan: {drawn} symbols for {redo.get('count')} pieces")
    if pdf_path.exists():
        pdf_path.unlink()
    send("export_pdf", {"path": str(pdf_path), "layout": "plan"})
    if pdf_path.is_file():
        lines.append(f"  {pdf_path}")
    else:
        fail("no furnished plan PDF")

    gone = send("delete_furniture", {"all": True})
    if gone.get("count") != redo.get("count"):
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
