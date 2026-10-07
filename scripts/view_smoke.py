#!/usr/bin/env python3
"""View picker smoke: Perspective, Plan and the four elevations in the active viewport.

Run on the garage after the garage smoke has built it. For each view,
show_view must leave the active viewport on that camera (ViewPicker.Current
reads it back), on the look (Forsk Technical for plan and elevations, Forsk
White for perspective, when Forsk White is on), zoomed to the building. The
plan view must carry the 1.2 m plan cut (the cut object is hidden and
locked), and the perspective must not, whatever the viewport is named.
Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/view_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))
VIEWS = ("perspective", "plan", "north", "east", "south", "west")

READ = """
import scriptcontext as sc
import Rhino
view = sc.doc.Views.ActiveView
vp = view.MainViewport
d = vp.CameraDirection
settings = Rhino.DocObjects.ObjectEnumeratorSettings()
settings.HiddenObjects = True
settings.LockedObjects = True
cut = [o for o in sc.doc.Objects.GetObjectList(settings) if o.Attributes.GetUserString("forsk:plan_cut") == "1"]
clipped = bool(cut) and any(i == vp.Id for i in cut[0].ClippingPlaneGeometry.ViewportIds())
print("VIEW %s %d %.3f %.3f %.3f %s|%d" % (vp.Name.replace(" ", "_"), 1 if vp.IsParallelProjection else 0, d.X, d.Y, d.Z, vp.DisplayMode.EnglishName, 1 if clipped else 0))
"""


def current(parallel: bool, dx: float, dy: float, dz: float) -> str:
    """ViewPicker.Current, as the plugin reads a viewport."""
    if not parallel:
        return "perspective"
    if dz < -0.99:
        return "plan"
    if abs(dz) < 0.01:
        if abs(dx) > abs(dy):
            return "east" if dx < 0 else "west"
        return "north" if dy < 0 else "south"
    return "tilted"


def run(send) -> tuple[list[str], bool]:
    lines: list[str] = []
    ok = True
    for view in VIEWS:
        out = send("show_view", {"view": view})
        read = str(send("execute_rhinoscript_python_code", {"code": READ}).get("output") or "")
        row = next((r for r in read.splitlines() if r.startswith("VIEW ")), "")
        if not row:
            lines.append(f"FAIL {view}: {out.get('message')} · {read.strip()[:120]}")
            ok = False
            continue
        name, parallel, dx, dy, dz, rest = row.split(" ", 6)[1:7]
        mode, clipped = rest.rsplit("|", 1)
        shown = current(parallel == "1", float(dx), float(dy), float(dz))
        technical = view != "perspective"
        problems = []
        if shown != view:
            problems.append(f"shows {shown}")
        if out.get("zoom") != "building":
            problems.append(f"zoom {out.get('zoom')}")
        if technical and "Technical" not in mode and mode != "Shaded":
            problems.append(f"look {mode}")
        if not technical and "Technical" in mode:
            problems.append(f"look {mode}")
        if (clipped == "1") != (view == "plan"):
            problems.append("plan cut " + ("on" if clipped == "1" else "off"))
        lines.append(f"{'FAIL ' if problems else ''}{view}: {name} {mode} cut {clipped} {'; '.join(problems)}".rstrip())
        ok = ok and not problems
    send("show_view", {"view": "perspective"})
    return lines, ok


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
    print("view smoke: " + ("PASS" if ok else "FAIL"))
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
