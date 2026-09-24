#!/usr/bin/env python3
"""Short F2 smoke for a blank millimetre file: one 6×4 m garage, one window.

Refuses a document that already has a plan. Does not save.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/garage_opening_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))


class SmokeError(RuntimeError):
    pass


def recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise SmokeError("connection closed")
        buf.extend(chunk)
    return bytes(buf)


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode()
    sock.sendall(len(payload).to_bytes(4, "big") + payload)
    length = int.from_bytes(recv_exact(sock, 4), "big")
    response = json.loads(recv_exact(sock, length).decode())
    if response.get("status") == "error":
        raise SmokeError(f"{cmd_type}: {response.get('message')}")
    return response.get("result") or {}


def layer(sock: socket.socket, name: str) -> None:
    try:
        send_command(sock, "create_layer", {"name": name})
    except SmokeError:
        # An existing layer comes back as a null-layer error. Setting it current
        # is what the wall and window curves need.
        pass
    send_command(sock, "get_or_set_current_layer", {"name": name})


def main() -> int:
    failures = []
    sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    sock.settimeout(TIMEOUT)
    try:
        summary = send_command(sock, "get_document_summary", {})
        meta = summary.get("meta_data") or {}
        units = str(meta.get("units") or "")
        count = int(summary.get("object_count") or 0)
        print(f"==> {meta.get('name')} units={units} objects={count}")
        if units.lower() not in ("millimeters", "millimetres"):
            raise SmokeError(f"units={units!r}")
        if count > 40:
            raise SmokeError("document is not a blank garage sheet")

        print("==> 6×4 m wall band and one window")
        layer(sock, "wall")
        # A single filled rectangle is a room, not a wall. The band is 200 mm.
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-wall",
            "params": {"points": [[0, 0, 0], [6000, 0, 0], [6000, 4000, 0], [0, 4000, 0], [0, 0, 0]]},
        })
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-wall-inner",
            "params": {"points": [[200, 200, 0], [5800, 200, 0], [5800, 3800, 0], [200, 3800, 0], [200, 200, 0]]},
        })
        walls = send_command(sock, "walls_from_layer", {})
        print(f"    {walls.get('message')}")
        layer(sock, "window")
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-window",
            "params": {"points": [[2000, -50, 0], [3200, -50, 0], [3200, 250, 0], [2000, 250, 0], [2000, -50, 0]]},
        })
        windows = send_command(sock, "openings_from_layer", {"layer": "window"})
        print(f"    {windows.get('message')} cut={windows.get('cut_count')}")
        if (windows.get("cut_count") or 0) != 1:
            failures.append(f"cut_count={windows.get('cut_count')}")
        blocks = windows.get("block_ids") or []
        markers = windows.get("marker_ids") or []
        if not blocks or not markers:
            raise SmokeError("no window frame")
        frame = send_command(sock, "get_object_info", {"id": blocks[0]})
        marker = send_command(sock, "get_object_info", {"id": markers[0]})
        box = marker.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
        origin = ((box[0][0] + box[1][0]) / 2, (box[0][1] + box[1][1]) / 2)

        name = frame.get("name")
        print(f"==> select {name} and move 500 mm")
        selected = send_command(sock, "select_objects", {"filters": {"name": [name]}})
        print(f"    selected {selected.get('count')}")
        if selected.get("count") != 1:
            failures.append(f"select count={selected.get('count')}")
        moved = send_command(sock, "move_opening", {"delta_mm": 500})
        print(f"    {moved.get('message')} openings={moved.get('host_openings')} voids={moved.get('host_voids')}")
        if moved.get("host_openings") != 1 or moved.get("host_voids") != 1:
            failures.append(f"move openings={moved.get('host_openings')} voids={moved.get('host_voids')}")
        row = (moved.get("markers") or [{}])[0]
        if row.get("inside") is not False:
            failures.append("garage void missing after move")
        if origin:
            shift = ((float(row.get("x", 0)) - origin[0]) ** 2 + (float(row.get("y", 0)) - origin[1]) ** 2) ** 0.5
            print(f"    shift {shift:.1f} mm")
            if shift < 50 or shift > 700:
                failures.append(f"shift {shift:.1f}")

        new_block = moved.get("block_id")
        if new_block:
            new_frame = send_command(sock, "get_object_info", {"id": new_block})
            send_command(sock, "select_objects", {"filters": {"name": [new_frame.get("name")]}})
        sized = send_command(sock, "set_opening", {"width": 1600, "sill": 800, "head": 2200})
        print(f"    {sized.get('message')} openings={sized.get('host_openings')} voids={sized.get('host_voids')}")
        if sized.get("host_openings") != 1 or sized.get("host_voids") != 1:
            failures.append(f"set openings={sized.get('host_openings')} voids={sized.get('host_voids')}")
    finally:
        sock.close()

    print("==> left the garage in the document; do not save")
    if failures:
        print(f"FAIL {len(failures)}")
        for item in failures:
            print(f"  - {item}")
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmokeError, OSError, socket.timeout) as exc:
        print(f"FAIL {exc}")
        sys.exit(1)
