#!/usr/bin/env python3
"""AN.2 smoke: live areas and daylight after a wall edit, on the sample house.

Run on the sample house after first_run.py. Switches daylight and areas
live (the menu's file strings), moves the south wall out 300 mm (the
first live daylight run stores each room's mean) and back in, and checks
the second edit's result carries a live line naming a room's area and its
daylight change, within 2 s. Then puts the switches back to the defaults.
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


def run(send) -> tuple[list[str], bool]:
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    send("execute_rhinoscript_python_code", {"code": SET_LIVE % ("1", "1")})
    try:
        out = send("move_wall", {"side": "south", "toward": "south", "distance_mm": 300})
        lines.append(f"out 300: live {out.get('live')!r}")
        if not out.get("live"):
            fail(f"no live line after the first move: {out.get('message')}")
        start = time.monotonic()
        back = send("move_wall", {"side": "south", "toward": "north", "distance_mm": 300})
        seconds = time.monotonic() - start
        live = str(back.get("live") or "")
        lines.append(f"back 300: {seconds:.2f} s · live {live!r}")
        if " m²" not in live or "daylight " not in live or "→" not in live:
            fail(f"the live line names no room's area and daylight change: {back.get('message')}")
        if seconds > LIMIT_S:
            fail(f"the edit with both live took {seconds:.2f} s, over {LIMIT_S:.0f} s")
        if "Live: " not in str(back.get("message", "")):
            fail("the edit's message does not carry the live line")
    finally:
        send("execute_rhinoscript_python_code", {"code": SET_LIVE % ("", "")})
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
