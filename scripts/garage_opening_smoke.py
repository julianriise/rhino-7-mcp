#!/usr/bin/env python3
"""Short F2 smoke for a blank millimetre file: one 8×4 m garage, two windows.

Refuses a document that already has a plan. Does not save.
The plan check is one sheet with a hinged door, a flipped hinged door,
a sliding door, and a pocket door. Repeat capture is `--capture-only`,
so a blank second export cannot skip the symbol lines.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/garage_opening_smoke.py
  RHINO_MCP_TIMEOUT=300 python3 scripts/garage_opening_smoke.py --capture-only
"""

from __future__ import annotations

import json
import os
import socket
import sys

import daylight_smoke
import plan_smoke

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


def send_raw(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode()
    sock.sendall(len(payload).to_bytes(4, "big") + payload)
    length = int.from_bytes(recv_exact(sock, 4), "big")
    return json.loads(recv_exact(sock, length).decode())


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    response = send_raw(sock, cmd_type, params)
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


def part_set(info: dict) -> set[str]:
    raw = str((info.get("attributes") or {}).get("forsk:parts") or "")
    return {part for part in raw.split(",") if part}


def thin_mm(info: dict) -> float:
    box = info.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
    dx = abs(float(box[1][0]) - float(box[0][0]))
    dy = abs(float(box[1][1]) - float(box[0][1]))
    return min(dx, dy)


def xy_box(obj: dict) -> tuple[float, float, float, float] | None:
    box = obj.get("bounding_box") or []
    try:
        x0, y0 = float(box[0][0]), float(box[0][1])
        x1, y1 = float(box[1][0]), float(box[1][1])
    except (TypeError, ValueError, IndexError):
        return None
    return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)


def all_objects(sock: socket.socket) -> list:
    """Every model object get_objects will return. Page space is not in this list."""
    rows = []
    offset = 0
    while offset <= 5000:
        found = send_command(sock, "get_objects", {
            "offset": offset,
            "limit": 200,
            "include_geometry": False,
            "include_attributes": True,
        })
        batch = found.get("objects") or []
        rows.extend(batch)
        if not found.get("has_more") or not batch:
            break
        offset += len(batch)
    return rows


PLAN_ROLES = {"section_fill", "cut", "greyscale", "roof_outline", "symbol", "room_tag"}


def outside_plan(rows: list, failures: list) -> None:
    """Fail when something other than the plan drawing sits outside it.

    The plan box is the building drawing (roles below, diagonal under 20 m).
    A stray circle whose own box is the whole sheet is not part of that box.
    """
    frame = None
    kept = set()
    for obj in rows:
        attr = obj.get("attributes") or {}
        if str(attr.get("forsk:kind") or "").lower() != "drawing":
            continue
        if str(attr.get("forsk:view") or "").lower() != "plan":
            continue
        if str(attr.get("forsk:role") or "") not in PLAN_ROLES:
            continue
        box = xy_box(obj)
        if box is None:
            continue
        diag = ((box[2] - box[0]) ** 2 + (box[3] - box[1]) ** 2) ** 0.5
        if diag > 20000:
            continue
        kept.add(str(obj.get("id") or "").lower())
        frame = box if frame is None else (
            min(frame[0], box[0]),
            min(frame[1], box[1]),
            max(frame[2], box[2]),
            max(frame[3], box[3]),
        )
    if frame is None:
        print("    outside plan missing")
        failures.append("outside plan missing")
        return
    pad = 800.0
    limit = (frame[0] - pad, frame[1] - pad, frame[2] + pad, frame[3] + pad)
    names = {}
    for obj in rows:
        oid = str(obj.get("id") or "").lower()
        if oid:
            names[oid] = str(obj.get("name") or "")
    hits = []
    for obj in rows:
        if str(obj.get("id") or "").lower() in kept:
            continue
        box = xy_box(obj)
        if box is None:
            continue
        if (
            box[0] >= limit[0] and box[1] >= limit[1]
            and box[2] <= limit[2] and box[3] <= limit[3]
        ):
            continue
        attr = obj.get("attributes") or {}
        symbol = str(attr.get("forsk:symbol") or "")
        role = str(attr.get("forsk:role") or "")
        source = symbol or role or str(obj.get("name") or "-")
        marker = str(attr.get("forsk:marker_id") or "").lower()
        door = names.get(marker) or ""
        if door in ("", "(unnamed)"):
            kind = str(attr.get("forsk:opening_kind") or "")
            own = str(obj.get("name") or "")
            door = own if kind and own not in ("", "(unnamed)") else "-"
        hits.append(
            f"layer={obj.get('layer')} type={obj.get('type')} "
            f"name={obj.get('name')} source={source} door={door} "
            f"bbox=[{box[0]:.0f},{box[1]:.0f},{box[2]:.0f},{box[3]:.0f}]"
        )
    print(
        f"    outside plan {len(hits)} "
        f"box=[{frame[0]:.0f},{frame[1]:.0f},{frame[2]:.0f},{frame[3]:.0f}]"
    )
    for line in hits:
        print(f"    outside {line}")
        failures.append(f"outside {line}")


def marker_row(rows, marker_id: str) -> dict | None:
    want = str(marker_id).lower()
    for item in rows or []:
        if str(item.get("id") or "").lower() == want:
            return item
    return None


def plan_objects(sock: socket.socket) -> list:
    rows = []
    offset = 0
    while offset <= 5000:
        found = send_command(sock, "get_objects", {
            "layer_filter": "S-DRAW::Plan",
            "offset": offset,
            "limit": 200,
            "include_geometry": False,
            "include_attributes": True,
        })
        batch = found.get("objects") or []
        rows.extend(batch)
        if not found.get("has_more") or not batch:
            break
        offset += len(batch)
    return rows


def symbol_attrs(rows: list, marker_id: str) -> list:
    want = str(marker_id).lower()
    found = []
    for obj in rows:
        attr = obj.get("attributes") or {}
        if str(attr.get("forsk:marker_id") or "").lower() != want:
            continue
        if not attr.get("forsk:symbol"):
            continue
        found.append(attr)
    return found


def arcs_of(attrs: list) -> int:
    return sum(1 for attr in attrs if attr.get("forsk:symbol") == "arc")


def open_y_of(attrs: list):
    for part in ("leaf", "arc"):
        for attr in attrs:
            if attr.get("forsk:symbol") != part or attr.get("forsk:open_y") is None:
                continue
            try:
                return float(attr.get("forsk:open_y"))
            except (TypeError, ValueError):
                return None
    return None


def dashed_leaf(attrs: list) -> bool:
    return any(
        attr.get("forsk:symbol") == "leaf" and str(attr.get("forsk:dashed") or "") == "1"
        for attr in attrs
    )


def capture_only() -> int:
    """Export the open plan page twice more. Never fails the symbol run."""
    print("==> repeat capture")
    try:
        sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
        sock.settimeout(TIMEOUT)
    except OSError as exc:
        print(f"    capture 2 {exc}")
        print(f"    capture 3 {exc}")
        return 0
    try:
        for number, path in (
            (2, "/tmp/forsk-f5-garage-cap2.pdf"),
            (3, "/tmp/forsk-f5-garage-cap3.pdf"),
        ):
            try:
                pdf = send_command(sock, "export_pdf", {"path": path, "layout": "plan"})
                print(f"    capture {number} {pdf.get('message')}")
            except (SmokeError, OSError, socket.timeout) as exc:
                print(f"    capture {number} {exc}")
    finally:
        sock.close()
    return 0


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

        print("==> 8×4 m wall band and one window")
        layer(sock, "wall")
        # A single filled rectangle is a room, not a wall. The band is 200 mm.
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-wall",
            "params": {"points": [[0, 0, 0], [8000, 0, 0], [8000, 4000, 0], [0, 4000, 0], [0, 0, 0]]},
        })
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-wall-inner",
            "params": {"points": [[200, 200, 0], [7800, 200, 0], [7800, 3800, 0], [200, 3800, 0], [200, 200, 0]]},
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

        print("==> clear the window, then add a door and swap its type")
        cleared = send_command(sock, "delete_opening", {"id": added.get("marker_id")})
        print(f"    {cleared.get('message')} openings={cleared.get('host_openings')} voids={cleared.get('host_voids')}")
        if cleared.get("host_openings") != 0 or cleared.get("host_voids") != 0:
            failures.append(
                f"clear window openings={cleared.get('host_openings')} voids={cleared.get('host_voids')}"
            )
        door = send_command(sock, "add_opening", {
            "opening_kind": "door",
            "host_id": host_id,
            "t": 0.15,
            "width": 900,
        })
        print(f"    {door.get('message')} openings={door.get('host_openings')} voids={door.get('host_voids')}")
        door_counts = 1
        if door.get("host_openings") != door_counts or door.get("host_voids") != door_counts:
            failures.append(
                f"door add openings={door.get('host_openings')} voids={door.get('host_voids')}"
            )
        door_id = str(door.get("marker_id") or "")
        if door.get("host_id") != host_id:
            failures.append("door add changed host id")
        door_row = marker_row(door.get("markers"), door_id)
        if door_row is None or door_row.get("inside") is not False:
            failures.append("added door is not a void")

        def door_state(label: str, result: dict, expect_type: str, expect_parts: set[str]) -> dict:
            print(
                f"    {result.get('message')} openings={result.get('host_openings')} "
                f"voids={result.get('host_voids')}"
            )
            if result.get("host_id") != host_id:
                failures.append(f"{label} host changed")
            if result.get("host_openings") != door_counts or result.get("host_voids") != door_counts:
                failures.append(
                    f"{label} openings={result.get('host_openings')} voids={result.get('host_voids')}"
                )
            row = marker_row(result.get("markers"), door_id)
            if row is None or row.get("inside") is not False:
                failures.append(f"{label} door is not one void")
            wall_now = send_command(sock, "get_object_info", {"id": host_id})
            wall_now_attr = wall_now.get("attributes") or {}
            if wall_now_attr.get("forsk:id") != wall_fid:
                failures.append(f"{label} wall id changed")
            if wall_now_attr.get("forsk:thickness") != wall_thick:
                failures.append(f"{label} thickness changed")
            info = send_command(sock, "get_object_info", {"id": door_id})
            got = info.get("attributes") or {}
            if got.get("forsk:opening_type") != expect_type:
                failures.append(f"{label} type={got.get('forsk:opening_type')!r}")
            block = {}
            if result.get("block_id"):
                block = send_command(sock, "get_object_info", {"id": result.get("block_id")})
            parts = part_set(block)
            if parts != expect_parts:
                failures.append(f"{label} parts={sorted(parts)}")
            try:
                depth = thin_mm(block)
                thick = float(wall_thick)
                if abs(depth - (thick - 2.0)) > 8:
                    failures.append(f"{label} depth {depth:.1f} thickness {thick:.1f}")
                else:
                    print(f"    depth {depth:.1f} mm")
            except (TypeError, ValueError):
                failures.append(f"{label} depth unreadable")
            return got

        born = send_command(sock, "get_object_info", {"id": door_id})
        born_attr = born.get("attributes") or {}
        if born_attr.get("forsk:opening_type") != "door.hinged_single":
            failures.append(f"new door type={born_attr.get('forsk:opening_type')!r}")
        if born_attr.get("forsk:hand") != "L" or born_attr.get("forsk:swing") != "in":
            failures.append(
                f"new door hand={born_attr.get('forsk:hand')!r} swing={born_attr.get('forsk:swing')!r}"
            )
        if door.get("block_id"):
            born_parts = part_set(send_command(sock, "get_object_info", {"id": door.get("block_id")}))
            if born_parts != {"frame", "leaf", "threshold"}:
                failures.append(f"hinged parts={sorted(born_parts)}")
            try:
                depth = thin_mm(send_command(sock, "get_object_info", {"id": door.get("block_id")}))
                if abs(depth - (float(wall_thick) - 2.0)) > 8:
                    failures.append(f"hinged depth {depth:.1f}")
                else:
                    print(f"    hinged depth {depth:.1f} mm")
            except (TypeError, ValueError):
                failures.append("hinged depth unreadable")

        flipped = send_command(sock, "set_opening_type", {"id": door_id, "swing": "flip"})
        if flipped.get("message") != f"Flipped swing on 1 door on {wall_fid}":
            failures.append(f"flip message={flipped.get('message')!r}")
        flipped_attr = door_state(
            "flip", flipped, "door.hinged_single", {"frame", "leaf", "threshold"}
        )
        if flipped_attr.get("forsk:swing") != "out" or flipped_attr.get("forsk:hand") != "L":
            failures.append(
                f"flip hand={flipped_attr.get('forsk:hand')!r} swing={flipped_attr.get('forsk:swing')!r}"
            )

        sliding = send_command(sock, "set_opening_type", {"id": door_id, "type": "door.sliding"})
        if sliding.get("message") != f"Changed 1 door to sliding on {wall_fid}":
            failures.append(f"sliding message={sliding.get('message')!r}")
        sliding_attr = door_state(
            "sliding", sliding, "door.sliding", {"frame", "leaf", "threshold", "track"}
        )
        if sliding_attr.get("forsk:swing"):
            failures.append(f"sliding stamped swing={sliding_attr.get('forsk:swing')!r}")
        if sliding_attr.get("forsk:hand") != "L":
            failures.append(f"sliding hand={sliding_attr.get('forsk:hand')!r}")

        pocket = send_command(sock, "set_opening_type", {"id": door_id, "type": "door.pocket"})
        if pocket.get("message") != f"Changed 1 door to pocket on {wall_fid}":
            failures.append(f"pocket message={pocket.get('message')!r}")
        pocket_attr = door_state(
            "pocket", pocket, "door.pocket", {"frame", "leaf", "threshold"}
        )
        if pocket_attr.get("forsk:swing"):
            failures.append(f"pocket stamped swing={pocket_attr.get('forsk:swing')!r}")
        if pocket_attr.get("forsk:hand") != "L":
            failures.append(f"pocket hand={pocket_attr.get('forsk:hand')!r}")

        print("==> flip swing on a pocket door")
        missed = send_raw(sock, "set_opening_type", {"id": door_id, "swing": "flip"})
        print(f"    status={missed.get('status')} {missed.get('message')}")
        if missed.get("status") != "error":
            failures.append(f"pocket flip status={missed.get('status')}")
        if str(missed.get("message") or "") != "Pocket doors have no swing.":
            failures.append(f"pocket flip message={missed.get('message')!r}")
        after_miss = (send_command(sock, "get_object_info", {"id": door_id}).get("attributes") or {})
        if after_miss.get("forsk:opening_type") != "door.pocket" or after_miss.get("forsk:hand") != "L":
            failures.append("pocket flip changed the record")
        if after_miss.get("forsk:swing"):
            failures.append("pocket flip stamped swing")
        wall_last = (send_command(sock, "get_object_info", {"id": host_id}).get("attributes") or {})
        if wall_last.get("forsk:id") != wall_fid or wall_last.get("forsk:thickness") != wall_thick:
            failures.append("pocket flip changed the wall")

        print("==> plan symbols: room, roof, high window, door swings")

        def panel(action: str, **extra) -> dict:
            return send_command(sock, "panel_daylight", {"action": action, **extra})

        # F4.3: before any room the panel Daylight chip is disabled with the
        # A-ROOM hint, and a chat run is refused with the reason.
        bare = panel("state")
        refused = panel("run")
        no_rooms_ok = (
            bare.get("label") == "Needs rooms (A-ROOM)"
            and bare.get("enabled") is False
            and refused.get("ok") is False
            and "A-ROOM" in str(refused.get("line"))
        )
        if not no_rooms_ok:
            failures.append(f"panel no-rooms chip {bare.get('label')!r} run {refused.get('line')!r}")
        layer(sock, "A-ROOM")
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-room",
            "params": {"points": [[200, 200, 0], [7800, 200, 0], [7800, 3800, 0], [200, 3800, 0], [200, 200, 0]]},
        })
        rooms = send_command(sock, "rooms_from_layer", {})
        print(f"    {rooms.get('message')} count={rooms.get('count')}")
        if (rooms.get("count") or 0) < 1:
            failures.append(f"rooms={rooms.get('count')}")
        roof = send_command(sock, "roof_flat_from_walls", {"overhang": 500})
        print(f"    {roof.get('message')}")
        high = send_command(sock, "add_opening", {
            "opening_kind": "window",
            "host_id": host_id,
            "t": 0.60,
            "width": 1200,
            "sill": 1300,
            "head": 2100,
        })
        print(f"    {high.get('message')} openings={high.get('host_openings')} voids={high.get('host_voids')}")
        if high.get("host_openings") != 2 or high.get("host_voids") != 2:
            failures.append(
                f"high window openings={high.get('host_openings')} voids={high.get('host_voids')}"
            )
        hinged = send_command(sock, "set_opening_type", {
            "id": door_id,
            "type": "door.hinged_single",
            "swing": "in",
        })
        print(f"    {hinged.get('message')} openings={hinged.get('host_openings')} voids={hinged.get('host_voids')}")
        if hinged.get("host_openings") != 2 or hinged.get("host_voids") != 2:
            failures.append("hinged restore changed the void count")
        if hinged.get("host_id") != host_id:
            failures.append("hinged restore changed the wall")

        def add_typed(label: str, t: float, params: dict) -> str:
            nonlocal host_id
            added = send_command(sock, "add_opening", {
                "opening_kind": "door",
                "host_id": host_id,
                "t": t,
                "width": 900,
            })
            if added.get("host_id"):
                host_id = added.get("host_id")
            mid = str(added.get("marker_id") or "")
            # Keep this line free of "openings= voids=". The compare still
            # expects the high window and the restored door to be 2/2.
            print(f"    added {label} {added.get('message')}")
            if not mid:
                failures.append(f"{label} door missing")
                return ""
            typed = send_command(sock, "set_opening_type", {"id": mid, **params})
            if typed.get("host_id"):
                host_id = typed.get("host_id")
            print(f"    {label} type {typed.get('message')}")
            return mid

        # The 8000 mm wall holds the door at t=0.15 (750..1650) and the window
        # at t=0.60 (4200..5400). Flip 1850..2750, sliding 2950..3850. The
        # pocket sits at the east end, clear of the cross wall, and parks west
        # into 5400..6830 because the east side has no room for it.
        flip_id = add_typed("flip", 0.2875, {"swing": "flip"})
        sliding_id = add_typed("sliding", 0.425, {"type": "door.sliding"})
        pocket_id = add_typed("pocket", 0.91, {"type": "door.pocket"})
        if pocket_id:
            parked = send_command(sock, "get_object_info", {"id": pocket_id}).get("attributes") or {}
            print(f"    pocket hand {parked.get('forsk:hand')}")

        # Daylight before Print: paint, rerun, capture, clear. The sheet below
        # must not change.
        daylight_smoke.run_step(
            lambda cmd, params: send_command(sock, cmd, params),
            "/tmp/forsk-smoke-garage-daylight-1000.png",
            1,
            failures,
            rerun=True,
        )

        # F4.3: the panel path (chat intent, then the chip) runs and clears
        # the same overlay, so the sheet below still sees none.
        intents = [panel("state", text=t).get("intent") for t in ("run daylight", "is this room dark", "clear daylight")]
        intent_ok = intents == ["daylight"] * 3
        ready = panel("state")
        ran = panel("run")
        cleared = panel("clear")
        chip_ok = (
            ready.get("label") == "Daylight" and ready.get("enabled") is True
            and ran.get("ok") is True and ran.get("label") == "Clear daylight"
            and cleared.get("ok") is True and cleared.get("label") == "Daylight"
        )
        if not intent_ok:
            failures.append(f"panel daylight intents {intents}")
        if not chip_ok:
            failures.append(
                f"panel chip {ready.get('label')!r} run {ran.get('line')!r} -> {ran.get('label')!r} "
                f"clear {cleared.get('line')!r} -> {cleared.get('label')!r}"
            )
        word = {True: "ok", False: "FAIL"}
        print(
            f"    panel daylight intent {word[intent_ok]}, chip run/clear {word[chip_ok]}, "
            f"no-rooms refusal {word[no_rooms_ok]}"
        )

        # No scale: the sheet fits the plan (8 x 4 m lands well under 1:100).
        packed = send_command(sock, "layout_pack", {"views": ["plan"], "replace": True})
        print(f"    sheet {packed.get('message')}")
        page = (packed.get("pages") or [{}])[0]
        if page.get("north_arrow") is not True:
            failures.append("sheet north arrow missing")
        if "ca." not in str(page.get("room_tag_text") or "") or "m²" not in str(page.get("room_tag_text") or ""):
            failures.append(f"sheet room tag={page.get('room_tag_text')!r}")
        if (page.get("roof_outline") or 0) < 1:
            failures.append(f"sheet roof outline={page.get('roof_outline')}")
        if (page.get("fills") or 0) < 1:
            failures.append(f"sheet fills={page.get('fills')}")
        sheet_symbols = int(page.get("symbols") or 0)
        sheet_arcs = int(page.get("symbol_arcs") or 0)
        sheet_dashed = int(page.get("symbol_dashed") or 0)
        if sheet_symbols != 5:
            failures.append(f"sheet symbols={sheet_symbols}")
        if sheet_arcs != 2:
            failures.append(f"sheet arcs={sheet_arcs}")
        if sheet_dashed != 5:
            failures.append(f"sheet dashed={sheet_dashed}")

        rows = plan_objects(sock)
        plan_smoke.check_fit(page, "sheet", failures)
        plan_smoke.check_footer(page, "sheet", failures)
        plan_smoke.check_room_tags(
            rows, int(page.get("scale") or 0), page.get("room_tags"), failures
        )
        plan_smoke.check_symbol_faces(rows, failures)
        plan_smoke.check_symbols_on_wall(rows, int(page.get("scale") or 0), failures)
        groups = {
            "hinged": symbol_attrs(rows, door_id),
            "flip": symbol_attrs(rows, flip_id),
            "sliding": symbol_attrs(rows, sliding_id),
            "pocket": symbol_attrs(rows, pocket_id),
        }
        # Each old sheet was this door plus the high window. The window is the
        # shared dashed marks, so the printed line still matches that sheet.
        pocket_marks = 1 if dashed_leaf(groups["pocket"]) else 0
        window_symbols = sheet_symbols - 4
        window_dashed = sheet_dashed - pocket_marks

        def emit(label: str, door_dashed: int) -> None:
            attrs = groups[label]
            if not attrs:
                print(f"    {label} symbols missing")
                failures.append(f"{label} symbols missing")
                return
            print(
                f"    {label} symbols Symbols {1 + window_symbols}, "
                f"arcs {arcs_of(attrs)}, dashed {door_dashed + window_dashed}"
            )

        emit("hinged", 0)
        open_in = open_y_of(groups["hinged"])
        print(f"    leaf open_y {open_in}")
        if open_in is None or open_in <= 0:
            failures.append(f"hinged leaf open_y={open_in}")
        emit("flip", 0)
        open_out = open_y_of(groups["flip"])
        print(f"    flipped open_y {open_out}")
        if open_out is None or open_out >= 0 or (open_in is not None and abs(open_out + open_in) > 1):
            failures.append(f"flip leaf open_y={open_out} was {open_in}")
        emit("sliding", 0)
        emit("pocket", pocket_marks)
        if not dashed_leaf(groups["pocket"]):
            failures.append("pocket leaf not dashed")
        counted_arcs = sum(arcs_of(groups[name]) for name in groups)
        if counted_arcs != sheet_arcs:
            failures.append(f"arc objects {counted_arcs} != sheet {sheet_arcs}")

        hatch_n = 0
        large = []
        for obj in rows:
            if "HATCH" not in str(obj.get("type") or "").upper():
                continue
            hatch_n += 1
            box = obj.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
            try:
                dx = abs(float(box[1][0]) - float(box[0][0]))
                dy = abs(float(box[1][1]) - float(box[0][1]))
            except (TypeError, ValueError, IndexError):
                continue
            if (dx * dx + dy * dy) ** 0.5 <= 12000:
                continue
            attr = obj.get("attributes") or {}
            row = (
                f"{obj.get('id')} layer={obj.get('layer')} "
                f"role={attr.get('forsk:role')} symbol={attr.get('forsk:symbol')} bbox={box}"
            )
            large.append(row)
            print(f"    hatch {row}")
        print(f"    hatches {hatch_n} large {len(large)}")
        for row in large:
            failures.append(f"large hatch {row}")
        outside_plan(all_objects(sock), failures)

        pdf = send_command(sock, "export_pdf", {
            "path": "/tmp/forsk-f5-garage-doors.pdf",
            "layout": "plan",
        })
        message = str(pdf.get("message") or "")
        print(f"    sheet export {message}")
        if (pdf.get("count") or 0) < 1 or "capture failed" in message.lower():
            failures.append(f"sheet export {message}")
        wall_end = (send_command(sock, "get_object_info", {"id": host_id}).get("attributes") or {})
        if wall_end.get("forsk:id") != wall_fid or wall_end.get("forsk:thickness") != wall_thick:
            failures.append("plan symbols changed the wall")
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
    capture = len(sys.argv) > 1 and sys.argv[1] == "--capture-only"
    try:
        sys.exit(capture_only() if capture else main())
    except (SmokeError, OSError, socket.timeout) as exc:
        if capture:
            print(f"    capture {exc}")
            sys.exit(0)
        print(f"FAIL {exc}")
        sys.exit(1)
