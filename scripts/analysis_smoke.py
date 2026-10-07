#!/usr/bin/env python3
"""AN.2 smoke: live areas and daylight after a wall edit, on the garage.

Run on the garage after the garage smoke has built it. Switches daylight and areas
live (the menu's file strings), moves the south wall out 300 mm (the live
line names the room's new area; the live daylight run stores each room's
mean), then lowers the window's sill to 800 (the line names the room's
daylight change, within 2 s). Then puts the window, the wall and the
switches back.
Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=600 python3 scripts/analysis_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys
import time

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "600"))
LIMIT_S = 2.0

SET_LIVE = """
import scriptcontext as sc
for key, value in (("live.daylight", "%s"), ("live.areas", "%s")):
    if value: sc.doc.Strings.SetString("forsk.analysis", key, value)
    else: sc.doc.Strings.Delete("forsk.analysis", key)
print("ok")
"""


SOUTH_WALL = """
import scriptcontext as sc
walls = [o for o in sc.doc.Objects if o.Attributes.GetUserString("forsk:kind") == "wall" and o.Attributes.GetUserString("forsk:generated") == "1"]
south = min(walls, key=lambda o: o.Geometry.GetBoundingBox(True).Min.Y)
print(south.Id)
"""


WINDOW = """
import scriptcontext as sc
import Rhino
settings = Rhino.DocObjects.ObjectEnumeratorSettings()
settings.HiddenObjects = True
settings.LockedObjects = True
for o in sc.doc.Objects.GetObjectList(settings):
    a = o.Attributes
    if a.GetUserString("forsk:kind") == "opening_marker" and a.GetUserString("forsk:opening_kind") == "window":
        print("WIN %s %s" % (o.Id, a.GetUserString("forsk:sill") or ""))
        break
"""


STORED = """
import scriptcontext as sc
print("; ".join(k + "=" + str(sc.doc.Strings.GetValue("forsk.analysis", k)) for k in ("live.daylight", "last.daylight", "daylight.rooms")))
"""


def run(send) -> tuple[list[str], bool]:
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    send("execute_rhinoscript_python_code", {"code": SET_LIVE % ("1", "1")})
    wall = str(send("execute_rhinoscript_python_code", {"code": SOUTH_WALL}).get("output") or "").strip()
    found = str(send("execute_rhinoscript_python_code", {"code": WINDOW}).get("output") or "")
    window, sill = "", ""
    for row in found.splitlines():
        if row.startswith("WIN "):
            window, sill = (row.split() + ["", "", ""])[1:3]
    if not window:
        fail("no window marker: " + found.strip()[:160])
    try:
        out = send("move_wall", {"id": wall, "side": "south", "toward": "south", "distance_mm": 300})
        lines.append(f"wall out 300: live {out.get('live')!r}")
        if " m²" not in str(out.get("live") or ""):
            fail(f"no area in the live line after a wall move: {out.get('message')}")
        if "Live: " not in str(out.get("message", "")):
            fail("the edit's message does not carry the live line")
        start = time.monotonic()
        lower = send("set_opening", {"id": window, "sill": 800})
        seconds = time.monotonic() - start
        live = str(lower.get("live") or "")
        lines.append(f"window sill {sill} -> 800: {seconds:.2f} s · live {live!r}")
        if "daylight " not in live or "→" not in live:
            fail(f"no daylight change in the live line: {lower.get('message')}")
            stored = send("execute_rhinoscript_python_code", {"code": STORED})
            lines.append("  stored: " + str(stored.get("output") or stored.get("message") or "").strip()[:200])
        if seconds > LIMIT_S:
            fail(f"the edit with both live took {seconds:.2f} s, over {LIMIT_S:.0f} s")
    finally:
        send("execute_rhinoscript_python_code", {"code": SET_LIVE % ("", "")})
        try:
            if window and sill:
                send("set_opening", {"id": window, "sill": float(sill)})
        except ValueError:
            lines.append(f"FAIL could not put the sill back: {sill!r}")
        send("move_wall", {"id": wall, "side": "south", "toward": "north", "distance_mm": 300})
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
    print("analysis smoke: " + ("PASS" if ok else "FAIL"))
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
