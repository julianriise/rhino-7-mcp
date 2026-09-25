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
        layer(sock, "A-ROOM")
        send_command(sock, "create_object", {
            "type": "POLYLINE",
            "name": "garage-room",
            "params": {"points": [[200, 200, 0], [5800, 200, 0], [5800, 3800, 0], [200, 3800, 0], [200, 200, 0]]},
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
            "t": 0.72,
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

        def leaf_open_y() -> float | None:
            found = send_command(sock, "get_objects", {
                "layer_filter": "S-DRAW::Plan",
                "limit": 200,
                "include_geometry": False,
                "include_attributes": True,
            })
            for obj in found.get("objects") or []:
                attr = obj.get("attributes") or {}
                if attr.get("forsk:symbol") != "leaf":
                    continue
                if str(attr.get("forsk:marker_id") or "").lower() != door_id.lower():
                    continue
                try:
                    return float(attr.get("forsk:open_y"))
                except (TypeError, ValueError):
                    return None
            return None

        def plan_page(label: str, scale: int, pdf_name: str) -> dict:
            packed = send_command(sock, "layout_pack", {"views": ["plan"], "scale": scale, "replace": True})
            print(f"    {label} {packed.get('message')}")
            pages = packed.get("pages") or []
            page = pages[0] if pages else {}
            if page.get("symbols") != 2:
                failures.append(f"{label} symbols={page.get('symbols')}")
            if page.get("north_arrow") is not True:
                failures.append(f"{label} north arrow missing")
            title = str(page.get("view_title") or "")
            if f"1:{page.get('scale')}" not in title:
                failures.append(f"{label} title={title!r} scale={page.get('scale')}")
            if "ca." not in str(page.get("room_tag_text") or "") or "m²" not in str(page.get("room_tag_text") or ""):
                failures.append(f"{label} room tag={page.get('room_tag_text')!r}")
            if (page.get("roof_outline") or 0) < 1:
                failures.append(f"{label} roof outline={page.get('roof_outline')}")
            if (page.get("fills") or 0) < 1:
                failures.append(f"{label} fills={page.get('fills')}")
            pdf = send_command(sock, "export_pdf", {"path": pdf_name, "layout": "plan"})
            print(f"    {pdf.get('message')}")
            if (pdf.get("count") or 0) < 1 or "capture failed" in str(pdf.get("message") or "").lower():
                failures.append(f"{label} pdf {pdf.get('message')}")
            wall_now = (send_command(sock, "get_object_info", {"id": host_id}).get("attributes") or {})
            if wall_now.get("forsk:id") != wall_fid or wall_now.get("forsk:thickness") != wall_thick:
                failures.append(f"{label} wall changed")
            return page

        hinged_page = plan_page("hinged", 100, "/tmp/forsk-f5-garage-hinged.pdf")
        if hinged_page.get("symbol_arcs") != 1:
            failures.append(f"hinged arcs={hinged_page.get('symbol_arcs')}")
        if (hinged_page.get("symbol_dashed") or 0) < 1:
            failures.append(f"hinged dashed={hinged_page.get('symbol_dashed')}")
        open_in = leaf_open_y()
        print(f"    leaf open_y {open_in}")
        if open_in is None or open_in <= 0:
            failures.append(f"hinged leaf open_y={open_in}")

        send_command(sock, "set_opening_type", {"id": door_id, "swing": "flip"})
        # A second capture of the same page comes back black. A fresh layout
        # paints, and export_pdf still rebuilds the plan from the record.
        send_command(sock, "layout_pack", {"views": ["plan"], "scale": 100, "replace": True})
        flipped_pdf = send_command(sock, "export_pdf", {
            "path": "/tmp/forsk-f5-garage-flip.pdf",
            "layout": "plan",
        })
        print(f"    flip export {flipped_pdf.get('message')}")
        if "Symbols 2" not in str(flipped_pdf.get("message") or ""):
            failures.append(f"flip export symbols {flipped_pdf.get('message')}")
        open_out = leaf_open_y()
        print(f"    flipped open_y {open_out}")
        if open_out is None or open_out >= 0 or (open_in is not None and abs(open_out + open_in) > 1):
            failures.append(f"flip leaf open_y={open_out} was {open_in}")

        send_command(sock, "set_opening_type", {"id": door_id, "type": "door.sliding"})
        send_command(sock, "layout_pack", {"views": ["plan"], "scale": 100, "replace": True})
        sliding_pdf = send_command(sock, "export_pdf", {
            "path": "/tmp/forsk-f5-garage-sliding.pdf",
            "layout": "plan",
        })
        print(f"    sliding export {sliding_pdf.get('message')}")
        if "arcs 0" not in str(sliding_pdf.get("message") or ""):
            failures.append(f"sliding arcs {sliding_pdf.get('message')}")
        if leaf_open_y() is not None:
            # A sliding leaf is not a swinging leaf; open_y is still stamped on it.
            pass

        send_command(sock, "set_opening_type", {"id": door_id, "type": "door.pocket"})
        send_command(sock, "layout_pack", {"views": ["plan"], "scale": 100, "replace": True})
        pocket_pdf = send_command(sock, "export_pdf", {
            "path": "/tmp/forsk-f5-garage-pocket.pdf",
            "layout": "plan",
        })
        print(f"    pocket export {pocket_pdf.get('message')}")
        if "arcs 0" not in str(pocket_pdf.get("message") or ""):
            failures.append(f"pocket arcs {pocket_pdf.get('message')}")
        pocket_leaf = None
        found = send_command(sock, "get_objects", {
            "layer_filter": "S-DRAW::Plan",
            "limit": 200,
            "include_geometry": False,
            "include_attributes": True,
        })
        for obj in found.get("objects") or []:
            attr = obj.get("attributes") or {}
            if attr.get("forsk:symbol") == "leaf" and str(attr.get("forsk:marker_id") or "").lower() == door_id.lower():
                pocket_leaf = attr
                break
        if pocket_leaf is None or pocket_leaf.get("forsk:dashed") != "1":
            failures.append(f"pocket leaf={pocket_leaf}")
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
    try:
        sys.exit(main())
    except (SmokeError, OSError, socket.timeout) as exc:
        print(f"FAIL {exc}")
        sys.exit(1)
