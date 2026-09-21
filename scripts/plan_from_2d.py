#!/usr/bin/env python3
"""Bake Forsk 3D from the open 2D plan — leave solids in the document.

Unlike plan_layer_smoke.py this does NOT wipe geometry at the end.
Use for demos and for facade edit testing (delete/add/move).
A-OPEN and A-ROOF are created hidden (walls/floor with holes show by default).
Turn those layers on in Rhino when you need markers or the roof.

Requires Rhino 7 + mcpstart and a mm plan (.3dm / DXF) open.
Disconnect Grok from MCP while this runs (one client on :1999).

Usage:
  python3 /Users/jr/Documents/hobby/rhino-7-mcp/scripts/plan_from_2d.py
  python3 …/plan_from_2d.py --no-clear-first   # keep existing Forsk solids (may duplicate)
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import time

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "180"))
FRAME = 4
MAX_FRAME = 64 * 1024 * 1024


class BakeError(RuntimeError):
    pass


def recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise BakeError(f"connection closed ({len(buf)}/{n})")
        buf.extend(chunk)
    return bytes(buf)


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode("utf-8")
    sock.sendall(len(payload).to_bytes(FRAME, "big") + payload)
    header = recv_exact(sock, FRAME)
    length = int.from_bytes(header, "big")
    if length <= 0 or length > MAX_FRAME:
        raise BakeError(f"bad frame length {length}")
    response = json.loads(recv_exact(sock, length).decode("utf-8"))
    if response.get("status") == "error":
        raise BakeError(f"{cmd_type}: {response.get('message', 'error')}")
    if response.get("status") != "success":
        raise BakeError(f"{cmd_type}: unexpected {response!r}")
    return response.get("result") or {}


def main() -> int:
    ap = argparse.ArgumentParser(description="Bake plan→3D and keep solids")
    ap.add_argument(
        "--no-clear-first",
        action="store_true",
        help="Do not clear previous Forsk solids before bake (default clears once)",
    )
    args = ap.parse_args()

    t0 = time.perf_counter()
    print(f"connecting to {HOST}:{PORT} ...")
    try:
        sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    except OSError as exc:
        print(f"could not connect: {exc}", file=sys.stderr)
        print("Open Rhino, open the plan (mm), run mcpstart, disconnect Grok MCP, retry.")
        return 2
    sock.settimeout(TIMEOUT)

    try:
        summary = send_command(sock, "get_document_summary", {})
        meta = summary.get("meta_data") or {}
        units = str(meta.get("units") or "")
        print(f"document  name={meta.get('name')} units={units} "
              f"objects={summary.get('object_count')}")
        if "millimet" not in units.lower():
            print(f"FAIL units={units} (need millimetres)")
            return 1

        if not args.no_clear_first:
            print("==> clear_generated (old Forsk 3D only; 2D curves stay)")
            cleared = send_command(sock, "clear_generated", {
                "include_untagged_prefixes": True,
            })
            print(f"    deleted={cleared.get('count')}")

        print("==> floor_from_layer")
        floor = send_command(sock, "floor_from_layer", {
            "layer": "wall",
            "thickness": 400,
            "target_layer": "A-FLOR",
            "name_prefix": "floor-",
        })
        print(f"    {floor.get('message')} count={floor.get('count')}")

        print("==> walls_from_layer")
        walls = send_command(sock, "walls_from_layer", {
            "layer": "wall",
            "height": 3000,
            "target_layer": "A-WALL",
            "name_prefix": "wall-",
        })
        print(f"    {walls.get('message')} count={walls.get('count')}")

        print("==> roof_flat_from_walls")
        roof = send_command(sock, "roof_flat_from_walls", {})
        print(f"    {roof.get('message')} count={roof.get('count')}")

        print("==> openings_from_layer door")
        doors = send_command(sock, "openings_from_layer", {"layer": "door"})
        print(f"    cut={doors.get('cut_count')} fail={doors.get('failed_count')} "
              f"markers={len(doors.get('marker_ids') or [])}")

        print("==> openings_from_layer window")
        windows = send_command(sock, "openings_from_layer", {"layer": "window"})
        print(f"    cut={windows.get('cut_count')} fail={windows.get('failed_count')} "
              f"markers={len(windows.get('marker_ids') or [])}")

        print("==> rooms_from_layer")
        rooms = send_command(sock, "rooms_from_layer", {})
        print(f"    {rooms.get('message')} count={rooms.get('count')}")

        after = send_command(sock, "get_document_summary", {})
        elapsed = time.perf_counter() - t0
        print(f"==> done  objects={after.get('object_count')} "
              f"by_layer={after.get('objects_by_layer')}")
        print(f"elapsed {elapsed:.1f}s — geometry KEPT (no final clear)")
        print("Next: reconnect Grok, click an A-OPEN marker, try delete / add / move.")
        return 0
    except BakeError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    finally:
        sock.close()


if __name__ == "__main__":
    sys.exit(main())
