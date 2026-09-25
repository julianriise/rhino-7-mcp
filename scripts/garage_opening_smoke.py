#!/usr/bin/env python3
"""Short F2 smoke for a blank millimetre file: one 6×4 m garage, two windows.

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


def near_mm(value, expect: float) -> bool:
    try:
        return abs(float(value) - expect) < 0.5
    except (TypeError, ValueError):
        return False


def center_of(info: dict) -> tuple[float, float]:
    box = info.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
    return (
        (float(box[0][0]) + float(box[1][0])) / 2,
        (float(box[0][1]) + float(box[1][1])) / 2,
    )


def marker_for_frame(sock: socket.socket, frame: dict, markers: list) -> str:
    mid = str((frame.get("attributes") or {}).get("forsk:marker_id") or "")
    if mid:
        return mid
    fx, fy = center_of(frame)
    best = str(markers[0])
    best_d = float("inf")
    for candidate in markers:
        info = send_command(sock, "get_object_info", {"id": candidate})
        x, y = center_of(info)
        dist = ((x - fx) ** 2 + (y - fy) ** 2) ** 0.5
        if dist < best_d:
            best = str(candidate)
            best_d = dist
    return best


def marker_row(rows, marker_id: str) -> dict | None:
    want = str(marker_id).lower()
    for item in rows or []:
        if str(item.get("id") or "").lower() == want:
            return item
    return None


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
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-window-b",
            "params": {"points": [[4200, -50, 0], [5400, -50, 0], [5400, 250, 0], [4200, 250, 0], [4200, -50, 0]]},
        })
        windows = send_command(sock, "openings_from_layer", {"layer": "window"})
        print(f"    {windows.get('message')} cut={windows.get('cut_count')}")
        if (windows.get("cut_count") or 0) != 2:
            failures.append(f"cut_count={windows.get('cut_count')}")
        blocks = windows.get("block_ids") or []
        markers = windows.get("marker_ids") or []
        if len(blocks) < 2 or len(markers) < 2:
            raise SmokeError("need two window frames")
        frame = send_command(sock, "get_object_info", {"id": blocks[0]})
        frame_b = send_command(sock, "get_object_info", {"id": blocks[1]})
        moved_id = marker_for_frame(sock, frame, markers)
        marker = send_command(sock, "get_object_info", {"id": moved_id})
        box = marker.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
        origin = ((box[0][0] + box[1][0]) / 2, (box[0][1] + box[1][1]) / 2)
        host_id = (marker.get("attributes") or {}).get("forsk:host")
        wall = send_command(sock, "get_object_info", {"id": host_id})
        wall_attr = wall.get("attributes") or {}
        wall_fid = wall_attr.get("forsk:id")
        wall_thick = wall_attr.get("forsk:thickness")
        print(f"    wall {wall_fid} thickness={wall_thick}")
        if not near_mm(wall_thick, 200):
            failures.append(f"thickness {wall_thick} != 200")
        back_t = (marker.get("attributes") or {}).get("forsk:t")

        name = frame.get("name")
        print(f"==> select {name} and move 500 mm")
        selected = send_command(sock, "select_objects", {"filters": {"name": [name]}})
        print(f"    selected {selected.get('count')}")
        if selected.get("count") != 1:
            failures.append(f"select count={selected.get('count')}")
        moved = send_command(sock, "move_opening", {"delta_mm": 500})
        print(f"    {moved.get('message')} openings={moved.get('host_openings')} voids={moved.get('host_voids')}")
        if moved.get("host_openings") != 2 or moved.get("host_voids") != 2:
            failures.append(f"move openings={moved.get('host_openings')} voids={moved.get('host_voids')}")
        returned = str(moved.get("marker_id") or "")
        if returned and returned.lower() != str(moved_id).lower():
            failures.append(f"moved id {returned} != {moved_id}")
        row = marker_row(moved.get("markers"), returned or moved_id)
        if row is None:
            failures.append(f"moved opening {moved_id} missing from the report")
        else:
            if row.get("inside") is not False:
                failures.append("garage void missing after move")
            shift = ((float(row.get("x", 0)) - origin[0]) ** 2 + (float(row.get("y", 0)) - origin[1]) ** 2) ** 0.5
            print(f"    shift {shift:.1f} mm id={returned or moved_id}")
            if shift < 50 or shift > 700:
                failures.append(f"shift {shift:.1f}")

        new_block = moved.get("block_id")
        if new_block:
            new_frame = send_command(sock, "get_object_info", {"id": new_block})
            send_command(sock, "select_objects", {"filters": {"name": [new_frame.get("name")]}})
        sized = send_command(sock, "set_opening", {"width": 1600, "sill": 800, "head": 2200})
        print(f"    {sized.get('message')} openings={sized.get('host_openings')} voids={sized.get('host_voids')}")
        if sized.get("host_openings") != 2 or sized.get("host_voids") != 2:
            failures.append(f"set openings={sized.get('host_openings')} voids={sized.get('host_voids')}")

        print("==> select both windows and delete them")
        names = [frame.get("name"), frame_b.get("name")]
        selected = send_command(sock, "select_objects", {"filters": {"name": names}})
        print(f"    selected {names} count={selected.get('count')}")
        if selected.get("count") != 2:
            failures.append(f"delete select count={selected.get('count')}")
        removed = send_command(sock, "delete_opening", {})
        print(f"    {removed.get('message')} openings={removed.get('host_openings')} voids={removed.get('host_voids')} plates={removed.get('plate_count')}")
        if removed.get("message") != f"Removed 2 windows from {wall_fid}":
            failures.append(f"delete message={removed.get('message')!r}")
        if removed.get("host_openings") != 0 or removed.get("host_voids") != 0:
            failures.append(f"delete openings={removed.get('host_openings')} voids={removed.get('host_voids')}")
        if removed.get("plate_count") != 0:
            failures.append(f"plate_count={removed.get('plate_count')}")
        if removed.get("host_id") != host_id:
            failures.append(f"delete host {removed.get('host_id')} != {host_id}")
        for row in removed.get("deleted") or []:
            if row.get("inside") is not True:
                failures.append(f"deleted center is air id={row.get('id')}")
        wall_after = send_command(sock, "get_object_info", {"id": host_id})
        after_attr = wall_after.get("attributes") or {}
        if after_attr.get("forsk:id") != wall_fid:
            failures.append("delete changed wall id")
        if after_attr.get("forsk:thickness") != wall_thick:
            failures.append(f"delete thickness {after_attr.get('forsk:thickness')} != {wall_thick}")

        print("==> add one window back")
        try:
            t_value = float(back_t)
        except (TypeError, ValueError):
            t_value = 0.4
        added = send_command(sock, "add_opening", {
            "opening_kind": "window",
            "host_id": host_id,
            "t": t_value,
        })
        print(f"    {added.get('message')} openings={added.get('host_openings')} voids={added.get('host_voids')}")
        if added.get("host_openings") != 1 or added.get("host_voids") != 1:
            failures.append(f"add openings={added.get('host_openings')} voids={added.get('host_voids')}")
        new_id = str(added.get("marker_id") or "").lower()
        new_row = None
        for item in added.get("markers") or []:
            if str(item.get("id") or "").lower() == new_id:
                new_row = item
                break
        if new_row is None or new_row.get("inside") is not False:
            failures.append("added opening is not a void")
        if added.get("host_id") != host_id:
            failures.append("add changed host id")
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
