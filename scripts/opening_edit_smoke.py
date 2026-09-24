#!/usr/bin/env python3
"""Live F2 smoke: bake, move one window ~500 mm, set its size, force a miss.

Talks framed TCP to mcpstart. Leaves the bake in the document. Do not save.

Usage:
  RHINO_MCP_TIMEOUT=900 python3 scripts/opening_edit_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "900"))
FRAME_HEADER_SIZE = 4
MAX_FRAME_SIZE = 64 * 1024 * 1024


class SmokeError(RuntimeError):
    pass


def recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise SmokeError(f"connection closed mid-message ({len(buf)}/{n})")
        buf.extend(chunk)
    return bytes(buf)


def send_raw(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode("utf-8")
    sock.sendall(len(payload).to_bytes(FRAME_HEADER_SIZE, "big") + payload)
    header = recv_exact(sock, FRAME_HEADER_SIZE)
    if header.startswith(b"{"):
        raise SmokeError("plugin sent unframed JSON")
    length = int.from_bytes(header, "big")
    if length <= 0 or length > MAX_FRAME_SIZE:
        raise SmokeError(f"invalid frame length {length}")
    return json.loads(recv_exact(sock, length).decode("utf-8"))


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    response = send_raw(sock, cmd_type, params)
    if response.get("status") == "error":
        raise SmokeError(f"{cmd_type}: {response.get('message', 'unknown error')}")
    if response.get("status") != "success":
        raise SmokeError(f"{cmd_type}: unexpected response {response!r}")
    return response.get("result") or {}


def attrs_of(info: dict) -> dict:
    raw = info.get("attributes") or {}
    return raw if isinstance(raw, dict) else {}


def center(bbox):
    if not bbox or len(bbox) != 2:
        return None
    a, b = bbox
    return (
        (float(a[0]) + float(b[0])) * 0.5,
        (float(a[1]) + float(b[1])) * 0.5,
        (float(a[2]) + float(b[2])) * 0.5,
    )


def dist_xy(a, b) -> float:
    return ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5


def check_host(label, result, marker_id, origin, expect, failures, want_shift=False, want_previous_solid=False):
    openings = result.get("host_openings")
    voids = result.get("host_voids")
    max_frame = result.get("max_frame_mm")
    print(f"    {label}: openings={openings} voids={voids} max_frame_mm={max_frame}")
    if openings != expect:
        failures.append(f"{label} openings={openings} expected {expect}")
    if voids != expect:
        failures.append(f"{label} voids={voids} expected {expect}")
    try:
        frame = float(max_frame)
    except (TypeError, ValueError):
        frame = -1
    if frame < 0 or frame > 25:
        failures.append(f"{label} max_frame_mm={max_frame} expected 0–25")
    row = None
    for item in result.get("markers") or []:
        if str(item.get("id")).lower() == str(marker_id).lower():
            row = item
            break
    if row is None:
        failures.append(f"{label} missing marker {marker_id}")
        return
    if row.get("inside") is not False:
        failures.append(f"{label} marker is still inside the wall")
    try:
        frame_mm = float(row.get("frame_mm"))
    except (TypeError, ValueError):
        frame_mm = -1
    if frame_mm < 0 or frame_mm > 25:
        failures.append(f"{label} frame_mm={row.get('frame_mm')}")
    if want_shift and origin is not None:
        shift = dist_xy(origin, (float(row["x"]), float(row["y"]), float(row["z"])))
        print(f"    {label} void shift {shift:.1f} mm")
        if shift < 50 or shift > 700:
            failures.append(f"{label} void shift {shift:.1f} mm, expected about 500")
    if want_previous_solid and row.get("previous_inside") is not True:
        failures.append(f"{label} old position is not solid again (previous_inside={row.get('previous_inside')})")


def layer_count(summary: dict, name: str) -> int:
    by_layer = summary.get("objects_by_layer") or {}
    for key, value in by_layer.items():
        if str(key).lower() == name.lower():
            return int(value)
    return 0


def bake(sock: socket.socket) -> dict:
    print("==> floor_from_layer")
    floor = send_command(sock, "floor_from_layer", {})
    print(f"    {floor.get('message')} count={floor.get('count')}")
    print("==> walls_from_layer")
    walls = send_command(sock, "walls_from_layer", {})
    print(f"    {walls.get('message')} count={walls.get('count')}")
    print("==> roof_flat_from_walls")
    roof = send_command(sock, "roof_flat_from_walls", {})
    print(f"    {roof.get('message')} count={roof.get('count')}")
    print("==> openings_from_layer door")
    doors = send_command(sock, "openings_from_layer", {"layer": "door"})
    print(f"    {doors.get('message')} cut={doors.get('cut_count')}")
    print("==> openings_from_layer window")
    windows = send_command(sock, "openings_from_layer", {"layer": "window"})
    print(f"    {windows.get('message')} cut={windows.get('cut_count')}")
    return {
        "floor_ids": floor.get("ids") or [],
        "wall_ids": walls.get("ids") or [],
        "roof_ids": roof.get("ids") or [],
        "window_markers": windows.get("marker_ids") or [],
        "window_blocks": windows.get("block_ids") or [],
        "door_cuts": doors.get("cut_count") or 0,
        "window_cuts": windows.get("cut_count") or 0,
    }


def pick_window(sock: socket.socket, marker_ids: list, block_ids: list):
    chosen = None
    fallback = None
    for i, mid in enumerate(marker_ids):
        info = send_command(sock, "get_object_info", {"id": mid})
        raw = attrs_of(info)
        if (raw.get("forsk:opening_kind") or "").lower() != "window":
            continue
        try:
            t = float(raw.get("forsk:t") or -1)
        except (TypeError, ValueError):
            t = -1
        block = block_ids[i] if i < len(block_ids) else ""
        item = {"marker": mid, "block": block, "info": info, "t": t}
        if fallback is None:
            fallback = item
        if 0.15 <= t <= 0.85:
            chosen = item
            break
    return chosen or fallback


def fingerprint(sock: socket.socket, marker_id: str, wall_id: str, sibling_id: str | None):
    marker = send_command(sock, "get_object_info", {"id": marker_id})
    wall = send_command(sock, "get_object_info", {"id": wall_id})
    sibling = None
    if sibling_id:
        sibling = send_command(sock, "get_object_info", {"id": sibling_id})
    return marker, wall, sibling


def main() -> int:
    failures = []
    sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    sock.settimeout(TIMEOUT)
    try:
        summary = send_command(sock, "get_document_summary", {})
        meta = summary.get("meta_data") or {}
        units = str(meta.get("units") or "")
        print(f"==> document {meta.get('name')} units={units} objects={summary.get('object_count')}")
        if units.lower() not in ("millimeters", "millimetres"):
            failures.append(f"units={units!r}")
            raise SmokeError("document is not millimetres")

        walls_now = layer_count(summary, "A-WALL")
        baked = None
        if walls_now < 1:
            baked = bake(sock)
            marker_ids = baked["window_markers"]
            block_ids = baked["window_blocks"]
        else:
            print(f"==> A-WALL already has {walls_now} objects; editing the open bake")
            listed = send_command(sock, "get_objects", {
                "layer_filter": "A-OPEN::Block",
                "include_geometry": False,
                "limit": 200,
            })
            marker_ids = []
            block_ids = []
            for obj in listed.get("objects") or []:
                info = send_command(sock, "get_object_info", {"id": obj.get("id")})
                raw = attrs_of(info)
                if (raw.get("forsk:opening_kind") or "").lower() != "window":
                    continue
                mid = raw.get("forsk:marker_id")
                if not mid:
                    continue
                block_ids.append(obj.get("id"))
                marker_ids.append(mid)

        window = pick_window(sock, marker_ids, block_ids)
        if window is None:
            failures.append("no window marker")
            raise SmokeError("no window")

        marker_id = window["marker"]
        block_id = window["block"]
        before = attrs_of(window["info"])
        host_id = before.get("forsk:host") or ""
        print(f"==> window {window['info'].get('name')} t={window['t']} host={host_id}")
        if len(host_id) < 32:
            failures.append(f"window host missing: {host_id!r}")

        wall_before = send_command(sock, "get_object_info", {"id": host_id})
        wall_attrs = attrs_of(wall_before)
        wall_fid = wall_attrs.get("forsk:id")
        wall_thick = wall_attrs.get("forsk:thickness")
        wall_path = wall_attrs.get("forsk:path") or ""
        origin = center(window["info"].get("bounding_box"))
        if origin is None:
            failures.append("window bbox missing")

        sibling_id = None
        sibling_origin = None
        for mid in marker_ids:
            if mid == marker_id:
                continue
            info = send_command(sock, "get_object_info", {"id": mid})
            raw = attrs_of(info)
            if raw.get("forsk:host") != host_id:
                continue
            sibling_id = mid
            sibling_origin = center(info.get("bounding_box"))
            break

        frame_name = None
        if block_id:
            block_info = send_command(sock, "get_object_info", {"id": block_id})
            frame_name = block_info.get("name")
        if not frame_name:
            frame_name = window["info"].get("name")

        expected = 77
        if baked:
            expected = int(baked.get("door_cuts") or 0) + int(baked.get("window_cuts") or 0)
        print(f"==> select {frame_name} then move this window 500 mm")
        selected = send_command(sock, "select_objects", {"filters": {"name": [frame_name]}})
        print(f"    selected {selected.get('count')}")
        if selected.get("count") != 1:
            failures.append(f"select count={selected.get('count')} name={frame_name}")
        moved = send_command(sock, "move_opening", {"delta_mm": 500})
        print(f"    {moved.get('message')} t={moved.get('t')} host={moved.get('host_id')}")
        if moved.get("ok") is not True:
            failures.append(f"move ok={moved.get('ok')}")
        if moved.get("marker_id") != marker_id:
            failures.append(f"marker id {moved.get('marker_id')} != {marker_id}")
        if moved.get("host_id") != host_id:
            failures.append(f"host id {moved.get('host_id')} != {host_id}")

        after_move = send_command(sock, "get_object_info", {"id": marker_id})
        moved_center = center(after_move.get("bounding_box"))
        if origin and moved_center:
            shift = dist_xy(origin, moved_center)
            print(f"    shift {shift:.1f} mm")
            if shift < 50:
                failures.append(f"opening shifted {shift:.1f} mm, expected about 500")
            if shift > 700:
                failures.append(f"opening shifted {shift:.1f} mm, expected about 500")
        wall_after = send_command(sock, "get_object_info", {"id": host_id})
        wall_after_attrs = attrs_of(wall_after)
        if wall_after_attrs.get("forsk:kind") != "wall":
            failures.append("host missing after move")
        if wall_after_attrs.get("forsk:id") != wall_fid:
            failures.append(f"forsk:id {wall_after_attrs.get('forsk:id')} != {wall_fid}")
        if wall_after_attrs.get("forsk:thickness") != wall_thick:
            failures.append(
                f"thickness {wall_after_attrs.get('forsk:thickness')} != {wall_thick}"
            )
        if (wall_after_attrs.get("forsk:path") or "") != wall_path:
            failures.append("forsk:path changed on move")
        check_host("move", moved, marker_id, origin, expected, failures, want_shift=True, want_previous_solid=True)
        if sibling_id and sibling_origin:
            sibling_after = send_command(sock, "get_object_info", {"id": sibling_id})
            sibling_center = center(sibling_after.get("bounding_box"))
            if sibling_center is None or dist_xy(sibling_origin, sibling_center) > 5:
                failures.append("sibling opening moved")
            if attrs_of(sibling_after).get("forsk:host") != host_id:
                failures.append("sibling left the host")

        print("==> set_opening width 1400 sill 1000 head 2200")
        new_block = moved.get("block_id")
        if new_block:
            new_frame = send_command(sock, "get_object_info", {"id": new_block})
            new_name = new_frame.get("name") or frame_name
            selected = send_command(sock, "select_objects", {"filters": {"name": [new_name]}})
            print(f"    reselected {new_name} count={selected.get('count')}")
            if selected.get("count") != 1:
                failures.append(f"reselect count={selected.get('count')} name={new_name}")
        sized = send_command(sock, "set_opening", {
            "width": 1400,
            "sill": 1000,
            "head": 2200,
        })
        print(f"    {sized.get('message')} host={sized.get('host_id')}")
        if sized.get("ok") is not True:
            failures.append(f"set ok={sized.get('ok')}")
        if sized.get("host_id") != host_id:
            failures.append(f"set host {sized.get('host_id')} != {host_id}")
        sized_info = send_command(sock, "get_object_info", {"id": marker_id})
        sized_attrs = attrs_of(sized_info)
        for key, expect in (("forsk:width", "1400"), ("forsk:sill", "1000"), ("forsk:head", "2200")):
            if str(sized_attrs.get(key)) != expect:
                failures.append(f"{key}={sized_attrs.get(key)!r} expected {expect}")
        wall_sized = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
        if wall_sized.get("forsk:thickness") != wall_thick:
            failures.append("thickness changed on set")
        if wall_sized.get("forsk:id") != wall_fid:
            failures.append("forsk:id changed on set")
        check_host("set", sized, marker_id, origin, expected, failures, want_shift=True)

        shot_marker, shot_wall, shot_sibling = fingerprint(sock, marker_id, host_id, sibling_id)

        print("==> forced miss sill 8000 head 9000")
        missed = send_raw(sock, "set_opening", {
            "id": marker_id,
            "sill": 8000,
            "head": 9000,
        })
        print(f"    status={missed.get('status')} {missed.get('message')}")
        if missed.get("status") != "error":
            failures.append(f"forced miss status={missed.get('status')}")
        back_marker, back_wall, back_sibling = fingerprint(sock, marker_id, host_id, sibling_id)
        if attrs_of(back_marker).get("forsk:width") != attrs_of(shot_marker).get("forsk:width"):
            failures.append("miss changed width")
        if attrs_of(back_marker).get("forsk:sill") != attrs_of(shot_marker).get("forsk:sill"):
            failures.append("miss changed sill")
        if attrs_of(back_marker).get("forsk:head") != attrs_of(shot_marker).get("forsk:head"):
            failures.append("miss changed head")
        c0 = center(shot_marker.get("bounding_box"))
        c1 = center(back_marker.get("bounding_box"))
        if c0 and c1 and dist_xy(c0, c1) > 1:
            failures.append(f"miss moved the marker {dist_xy(c0, c1):.1f} mm")
        if (attrs_of(back_wall).get("forsk:path") or "") != (attrs_of(shot_wall).get("forsk:path") or ""):
            failures.append("miss changed forsk:path")
        if attrs_of(back_wall).get("forsk:id") != attrs_of(shot_wall).get("forsk:id"):
            failures.append("miss changed wall id")
        if shot_sibling and back_sibling:
            s0 = center(shot_sibling.get("bounding_box"))
            s1 = center(back_sibling.get("bounding_box"))
            if s0 and s1 and dist_xy(s0, s1) > 1:
                failures.append("miss moved a sibling")

        if baked and baked["floor_ids"]:
            floor = send_command(sock, "get_object_info", {"id": baked["floor_ids"][0]})
            if attrs_of(floor).get("forsk:kind") != "floor":
                failures.append("floor missing after edits")
        if baked and baked["roof_ids"]:
            roof = send_command(sock, "get_object_info", {"id": baked["roof_ids"][0]})
            if attrs_of(roof).get("forsk:kind") != "roof":
                failures.append("roof missing after edits")

        print("==> layout_pack plan + export_pdf")
        packed = send_command(sock, "layout_pack", {"views": ["plan"], "replace": True})
        print(f"    {packed.get('message')} count={packed.get('count')}")
        if (packed.get("count") or 0) < 1:
            failures.append(f"layout_pack count={packed.get('count')} message={packed.get('message')}")
            print("FAIL print; stopping")
        else:
            pdf_path = "/tmp/forsk-f2-plan.pdf"
            pdf = send_command(sock, "export_pdf", {"path": pdf_path, "layout": "plan"})
            print(f"    {pdf.get('message')} count={pdf.get('count')} path={pdf.get('path')}")
            message = str(pdf.get("message") or "")
            if (pdf.get("count") or 0) < 1 or "capture failed" in message.lower():
                failures.append(f"export_pdf count={pdf.get('count')} message={message}")
                print("FAIL print; stopping")
    finally:
        sock.close()

    print("==> left the bake in the document; do not save")
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
