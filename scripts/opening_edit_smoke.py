#!/usr/bin/env python3
"""Live office smoke: import the pinned DXF into a blank millimetre copy, then edit.

Build opens a fresh copy of Large Objects - Millimeters.3dm, for example
/tmp/forsk-office-blank.3dm, and runs mcpstart. This script checks the DXF
SHA-256, imports it, bakes, and runs the opening checks. It does not save.
It refuses a changed or writable fixture, and it refuses the Downloads .3dm files.

Usage:
  RHINO_MCP_TIMEOUT=900 python3 scripts/opening_edit_smoke.py
"""

from __future__ import annotations

import hashlib
import json
import os
import socket
import subprocess
import sys
from pathlib import Path

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "900"))
FRAME_HEADER_SIZE = 4
MAX_FRAME_SIZE = 64 * 1024 * 1024
# tests/fixtures/office_2D.dxf in julianriise/forsk
OFFICE_DXF_SHA256 = "53a6791c04b7602327ccce28653944ad206a0f3eaa7c1629aa7655b754066f93"
OFFICE_SOURCE_LAYERS = (
    ("wall", 16),
    ("structural", 51),
    ("door", 28),
    ("window", 63),
    ("label", 16),
    ("furniture", 10),
    ("space_divider", 1),
)
# import_ACAD model_units. 2 brings this millimetre DXF in 1:1. 3 scales it by 10.
ACAD_UNITS_KEY = "User.Plug-Ins.6cd8563b-7bb0-4777-93f6-4dd10b3406a5.Settings.model_units"
ACAD_UNITS_MM = 2


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
    if want_previous_solid:
        try:
            moved_mm = float(row.get("moved_mm"))
        except (TypeError, ValueError):
            moved_mm = 0
        if moved_mm < 50:
            failures.append(f"{label} marker did not leave its previous plan position")


def layer_count(summary: dict, name: str) -> int:
    by_layer = summary.get("objects_by_layer") or {}
    for key, value in by_layer.items():
        if str(key).lower() == name.lower():
            return int(value)
    return 0


def office_dxf_path() -> Path:
    override = os.getenv("FORSK_OFFICE_DXF")
    if override:
        return Path(override)
    return Path(__file__).resolve().parents[2] / "forsk" / "tests" / "fixtures" / "office_2D.dxf"


def assert_office_dxf(path: Path) -> None:
    if not path.is_file():
        raise SmokeError(f"office fixture missing: {path}")
    if path.stat().st_mode & 0o222:
        raise SmokeError(f"office fixture is writable ({path}). chmod a-w it before the smoke")
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    print(f"==> office DXF {path}")
    print(f"    sha256 {digest}")
    if digest != OFFICE_DXF_SHA256:
        raise SmokeError(f"office DXF hash changed: {digest}")


def require_fresh_copy(summary: dict) -> None:
    meta = summary.get("meta_data") or {}
    units = str(meta.get("units") or "")
    if units.lower() not in ("millimeters", "millimetres"):
        raise SmokeError(f"units={units!r}")
    path = str(meta.get("path") or "")
    name = str(meta.get("name") or "")
    lowered = path.replace("\\", "/").lower()
    banned = {"office_2d.3dm", "office_3d.3dm", "office_2d.dxf"}
    if name.lower() in banned or "downloads/forsk-rhino" in lowered:
        raise SmokeError(f"refusing fixture file {path or name}")
    if "/tmp/" not in lowered and "/private/tmp/" not in lowered:
        raise SmokeError(f"open a temp copy of the millimetre template, not {path or name}")
    if "template files" in lowered:
        raise SmokeError("refusing to open the millimetre template in place")
    count = int(summary.get("object_count") or 0)
    if count != 0:
        raise SmokeError(f"document is not a fresh template copy (objects={count})")


def read_acad_model_units() -> int:
    try:
        out = subprocess.check_output(
            ["defaults", "read", "com.mcneel.rhinoceros.7", ACAD_UNITS_KEY],
            text=True,
        )
    except (subprocess.CalledProcessError, ValueError) as exc:
        raise SmokeError(f"could not read ACAD model_units: {exc}") from exc
    return int(out.strip())


def write_acad_model_units(value: int) -> None:
    subprocess.check_call(
        ["defaults", "write", "com.mcneel.rhinoceros.7", ACAD_UNITS_KEY, "-int", str(value)],
    )


def import_office_dxf(sock: socket.socket, path: Path) -> None:
    command = f'_-Import "{path}" _Enter'
    result = send_command(sock, "run_command", {"command": command, "echo": False})
    if result.get("success") is not True:
        output = str(result.get("output") or "").strip()
        raise SmokeError(f"DXF import failed: {output or result!r}")


def assert_import_span(summary: dict) -> None:
    box = summary.get("model_bounding_box") or []
    if len(box) != 2:
        raise SmokeError("import has no bounding box")
    span_x = float(box[1][0]) - float(box[0][0])
    span_y = float(box[1][1]) - float(box[0][1])
    print(f"    span {span_x:.0f} x {span_y:.0f} mm")
    # The DXF wall extent is 30243 x 22510 mm. Other layers reach about 23500 in Y.
    if not (25000 <= span_x <= 40000 and 20000 <= span_y <= 30000):
        raise SmokeError(f"import scale is wrong: span {span_x:.0f} x {span_y:.0f} mm")


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


def longest_edge(path_json: str):
    try:
        outer = (json.loads(path_json) or {}).get("outer") or []
    except (TypeError, ValueError, json.JSONDecodeError):
        return None
    best = None
    best_len = 0.0
    count = len(outer)
    for i in range(count):
        a = outer[i]
        b = outer[(i + 1) % count]
        if len(a) < 2 or len(b) < 2:
            continue
        dx = float(b[0]) - float(a[0])
        dy = float(b[1]) - float(a[1])
        length = (dx * dx + dy * dy) ** 0.5
        if length < 350 or length <= best_len:
            continue
        best_len = length
        best = (float(a[0]), float(a[1]), float(b[0]), float(b[1]), length)
    return best


def distance_to_edge(edge, x: float, y: float) -> float:
    x0, y0, x1, y1, length = edge
    if length < 1e-6:
        return ((x - x0) ** 2 + (y - y0) ** 2) ** 0.5
    t = ((x - x0) * (x1 - x0) + (y - y0) * (y1 - y0)) / (length * length)
    t = 0.0 if t < 0 else 1.0 if t > 1 else t
    px = x0 + t * (x1 - x0)
    py = y0 + t * (y1 - y0)
    return ((x - px) ** 2 + (y - py) ** 2) ** 0.5


def opening_frame_name(marker_name: str) -> str:
    name = marker_name or ""
    if name.endswith("-block"):
        return name
    return name + "-block"


def wall_ids(sock: socket.socket) -> list:
    listed = send_command(sock, "get_objects", {
        "layer_filter": "A-WALL",
        "include_geometry": False,
        "limit": 50,
    })
    return sorted(str(obj.get("id") or "").lower() for obj in (listed.get("objects") or []))


def pick_delete_pair(sock: socket.socket, marker_ids: list, host_id: str, path: str, skip_id: str):
    edge = longest_edge(path)
    rows = []
    for mid in marker_ids:
        info = send_command(sock, "get_object_info", {"id": mid})
        raw = attrs_of(info)
        if raw.get("forsk:host") != host_id:
            continue
        if (raw.get("forsk:opening_kind") or "").lower() != "window":
            continue
        point = center(info.get("bounding_box"))
        if point is None:
            continue
        if edge is not None and distance_to_edge(edge, point[0], point[1]) > 400:
            continue
        rows.append({
            "id": mid,
            "name": info.get("name") or "",
            "center": point,
            "t": raw.get("forsk:t"),
            "width": raw.get("forsk:width"),
            "sill": raw.get("forsk:sill"),
            "head": raw.get("forsk:head"),
            "skip": mid == skip_id,
        })
    preferred = [row for row in rows if not row["skip"]]
    chosen = preferred if len(preferred) >= 2 else rows
    return chosen[:2]


def part_set(info: dict) -> set[str]:
    raw = str((info.get("attributes") or {}).get("forsk:parts") or "")
    return {part for part in raw.split(",") if part}


def row_for(rows, marker_id: str) -> dict | None:
    want = str(marker_id).lower()
    for item in rows or []:
        if str(item.get("id") or "").lower() == want:
            return item
    return None


def check_opening_types(
    sock: socket.socket,
    moved_id: str,
    added_id: str,
    host_id: str,
    wall_fid: str,
    wall_thick: str,
    counts: int,
    failures: list,
) -> None:
    if not moved_id or not added_id:
        failures.append("type swap needs the moved window and the added window")
        return

    before_moved = send_command(sock, "get_object_info", {"id": moved_id})
    before_added = send_command(sock, "get_object_info", {"id": added_id})
    moved_attrs = attrs_of(before_moved)
    added_attrs = attrs_of(before_added)
    if moved_attrs.get("forsk:opening_type") != "window.side_hung":
        failures.append(f"baked type={moved_attrs.get('forsk:opening_type')!r}")
    if moved_attrs.get("forsk:hand") != "L" or moved_attrs.get("forsk:swing") != "in":
        failures.append(
            f"baked hand={moved_attrs.get('forsk:hand')!r} swing={moved_attrs.get('forsk:swing')!r}"
        )

    def kept(label: str, result: dict, marker_id: str, previous: dict, origin) -> None:
        print(
            f"    {result.get('message')} openings={result.get('host_openings')} "
            f"voids={result.get('host_voids')} frame={result.get('max_frame_mm')}"
        )
        if result.get("host_id") != host_id:
            failures.append(f"{label} host {result.get('host_id')} != {host_id}")
        if result.get("host_openings") != counts or result.get("host_voids") != counts:
            failures.append(
                f"{label} openings={result.get('host_openings')} voids={result.get('host_voids')} expected {counts}"
            )
        try:
            if float(result.get("max_frame_mm")) > 25:
                failures.append(f"{label} max_frame_mm={result.get('max_frame_mm')}")
        except (TypeError, ValueError):
            failures.append(f"{label} max_frame_mm missing")
        row = row_for(result.get("markers"), marker_id)
        if row is None or row.get("inside") is not False:
            failures.append(f"{label} marker is not a void")
        wall = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
        if wall.get("forsk:id") != wall_fid:
            failures.append(f"{label} wall id {wall.get('forsk:id')!r}")
        if wall.get("forsk:thickness") != wall_thick:
            failures.append(f"{label} thickness {wall.get('forsk:thickness')!r}")
        now = send_command(sock, "get_object_info", {"id": marker_id})
        now_attrs = attrs_of(now)
        for key in ("forsk:width", "forsk:sill", "forsk:head"):
            if str(now_attrs.get(key)) != str(previous.get(key)):
                failures.append(f"{label} {key} {now_attrs.get(key)!r} != {previous.get(key)!r}")
        now_center = center(now.get("bounding_box"))
        if origin and now_center and dist_xy(origin, now_center) > 1:
            failures.append(f"{label} moved {dist_xy(origin, now_center):.1f} mm")

    print("==> swap one window to top_hung")
    top = send_command(sock, "set_opening_type", {"id": moved_id, "type": "window.top_hung"})
    if top.get("message") != f"Changed 1 window to top-hung on {wall_fid}":
        failures.append(f"top_hung message={top.get('message')!r}")
    kept("top_hung", top, moved_id, moved_attrs, center(before_moved.get("bounding_box")))
    top_attr = attrs_of(send_command(sock, "get_object_info", {"id": moved_id}))
    if top_attr.get("forsk:opening_type") != "window.top_hung":
        failures.append(f"top_hung type={top_attr.get('forsk:opening_type')!r}")
    if top_attr.get("forsk:hand"):
        failures.append(f"top_hung stamped hand={top_attr.get('forsk:hand')!r}")
    if top_attr.get("forsk:swing") != "in":
        failures.append(f"top_hung swing={top_attr.get('forsk:swing')!r}")
    top_block = send_command(sock, "get_object_info", {"id": top.get("block_id")}) if top.get("block_id") else {}
    top_parts = part_set(top_block)
    if "sash" not in top_parts:
        failures.append(f"top_hung parts={sorted(top_parts)}")

    print("==> swap one window to fixed")
    fixed = send_command(sock, "set_opening_type", {"id": added_id, "type": "window.fixed"})
    if fixed.get("message") != f"Changed 1 window to fixed on {wall_fid}":
        failures.append(f"fixed message={fixed.get('message')!r}")
    kept("fixed", fixed, added_id, added_attrs, center(before_added.get("bounding_box")))
    fixed_attr = attrs_of(send_command(sock, "get_object_info", {"id": added_id}))
    if fixed_attr.get("forsk:opening_type") != "window.fixed":
        failures.append(f"fixed type={fixed_attr.get('forsk:opening_type')!r}")
    if fixed_attr.get("forsk:hand") or fixed_attr.get("forsk:swing"):
        failures.append(
            f"fixed hand={fixed_attr.get('forsk:hand')!r} swing={fixed_attr.get('forsk:swing')!r}"
        )
    fixed_block = send_command(sock, "get_object_info", {"id": fixed.get("block_id")}) if fixed.get("block_id") else {}
    fixed_parts = part_set(fixed_block)
    if "sash" in fixed_parts or "glass" not in fixed_parts:
        failures.append(f"fixed parts={sorted(fixed_parts)}")

    print("==> forced type miss")
    shot_top = attrs_of(send_command(sock, "get_object_info", {"id": moved_id}))
    shot_fixed = attrs_of(send_command(sock, "get_object_info", {"id": added_id}))
    shot_wall = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
    shot_top_center = center(send_command(sock, "get_object_info", {"id": moved_id}).get("bounding_box"))
    door_on_window = send_raw(sock, "set_opening_type", {"id": moved_id, "type": "door.sliding"})
    print(f"    door on window status={door_on_window.get('status')} {door_on_window.get('message')}")
    if door_on_window.get("status") != "error":
        failures.append(f"door on window status={door_on_window.get('status')}")
    if "That is a window" not in str(door_on_window.get("message") or ""):
        failures.append(f"door on window message={door_on_window.get('message')!r}")
    flip_fixed = send_raw(sock, "set_opening_type", {"id": added_id, "swing": "flip"})
    print(f"    flip fixed status={flip_fixed.get('status')} {flip_fixed.get('message')}")
    if flip_fixed.get("status") != "error":
        failures.append(f"flip fixed status={flip_fixed.get('status')}")
    if str(flip_fixed.get("message") or "") != "Fixed windows have no swing.":
        failures.append(f"flip fixed message={flip_fixed.get('message')!r}")
    back_top = attrs_of(send_command(sock, "get_object_info", {"id": moved_id}))
    back_fixed = attrs_of(send_command(sock, "get_object_info", {"id": added_id}))
    back_wall = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
    for key in ("forsk:opening_type", "forsk:hand", "forsk:swing", "forsk:width", "forsk:sill", "forsk:head"):
        if back_top.get(key) != shot_top.get(key):
            failures.append(f"type miss changed {key} on top_hung")
        if back_fixed.get(key) != shot_fixed.get(key):
            failures.append(f"type miss changed {key} on fixed")
    if back_wall.get("forsk:id") != shot_wall.get("forsk:id") or back_wall.get("forsk:thickness") != shot_wall.get("forsk:thickness"):
        failures.append("type miss changed the wall")
    back_center = center(send_command(sock, "get_object_info", {"id": moved_id}).get("bounding_box"))
    if shot_top_center and back_center and dist_xy(shot_top_center, back_center) > 1:
        failures.append("type miss moved the window")


def fingerprint(sock: socket.socket, marker_id: str, wall_id: str, sibling_id: str | None):
    marker = send_command(sock, "get_object_info", {"id": marker_id})
    wall = send_command(sock, "get_object_info", {"id": wall_id})
    sibling = None
    if sibling_id:
        sibling = send_command(sock, "get_object_info", {"id": sibling_id})
    return marker, wall, sibling


def main() -> int:
    dxf = office_dxf_path()
    assert_office_dxf(dxf)
    failures = []
    sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    sock.settimeout(TIMEOUT)
    try:
        summary = send_command(sock, "get_document_summary", {})
        meta = summary.get("meta_data") or {}
        units = str(meta.get("units") or "")
        doc_name = str(meta.get("name") or "")
        print(f"==> document {doc_name} units={units} objects={summary.get('object_count')}")
        require_fresh_copy(summary)
        if "office" not in doc_name.lower():
            raise SmokeError(
                f"temp copy name {doc_name!r} must contain 'office' so the 77-opening check runs"
            )

        print(f"==> import {dxf.name}")
        previous_units = read_acad_model_units()
        try:
            if previous_units != ACAD_UNITS_MM:
                print(f"    ACAD model_units {previous_units} -> {ACAD_UNITS_MM}")
                write_acad_model_units(ACAD_UNITS_MM)
            import_office_dxf(sock, dxf)
            summary = send_command(sock, "get_document_summary", {})
            print(f"    objects={summary.get('object_count')}")
            assert_import_span(summary)
            for name, expect in OFFICE_SOURCE_LAYERS:
                got = layer_count(summary, name)
                print(f"    {name}={got}")
                if got != expect:
                    raise SmokeError(f"{name}={got} expected {expect}")
        finally:
            # Always write the saved value. A second read here can throw and
            # leave the preference on the import setting.
            write_acad_model_units(previous_units)

        baked = bake(sock)
        marker_ids = baked["window_markers"]
        block_ids = baked["window_blocks"]

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
        print(f"    wall {wall_fid} thickness={wall_thick}")
        try:
            thick_mm = float(wall_thick)
        except (TypeError, ValueError):
            thick_mm = -1.0
        if abs(thick_mm - 200.0) > 0.5:
            failures.append(f"thickness {wall_thick} != 200")
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
        size_expect = {"forsk:width": "1400", "forsk:sill": "1000", "forsk:head": "2200"}
        if sized.get("host_openings") is None and "already" in str(sized.get("message") or "").lower():
            print("==> that size is already on the marker; set width 1500")
            sized = send_command(sock, "set_opening", {
                "width": 1500,
                "sill": 1000,
                "head": 2200,
            })
            print(f"    {sized.get('message')} host={sized.get('host_id')}")
            size_expect["forsk:width"] = "1500"
        if sized.get("ok") is not True:
            failures.append(f"set ok={sized.get('ok')}")
        if sized.get("host_id") != host_id:
            failures.append(f"set host {sized.get('host_id')} != {host_id}")
        sized_info = send_command(sock, "get_object_info", {"id": marker_id})
        sized_attrs = attrs_of(sized_info)
        for key, expect in size_expect.items():
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

        print("==> select 2 windows and delete_opening")
        doc_name = str(meta.get("name") or "")
        if "office" in doc_name.lower() and expected != 77:
            failures.append(f"office openings before delete={expected}, expected 77")
        pair = pick_delete_pair(sock, marker_ids, host_id, wall_path, marker_id)
        if len(pair) != 2:
            failures.append(f"delete pair={len(pair)}")
        else:
            names = [opening_frame_name(row["name"]) for row in pair]
            walls_before = wall_ids(sock)
            selected = send_command(sock, "select_objects", {"filters": {"name": names}})
            print(f"    selected {names} count={selected.get('count')}")
            if selected.get("count") != 2:
                failures.append(f"delete select count={selected.get('count')} names={names}")
            removed = send_command(sock, "delete_opening", {})
            print(f"    {removed.get('message')} openings={removed.get('host_openings')} voids={removed.get('host_voids')} plates={removed.get('plate_count')}")
            message = str(removed.get("message") or "")
            if message != f"Removed 2 windows from {wall_fid}":
                failures.append(f"delete message={message!r}")
            if removed.get("host_id") != host_id:
                failures.append(f"delete host {removed.get('host_id')} != {host_id}")
            if removed.get("plate_count") != 0:
                failures.append(f"plate_count={removed.get('plate_count')}")
            if removed.get("host_openings") != expected - 2 or removed.get("host_voids") != expected - 2:
                failures.append(
                    f"delete openings={removed.get('host_openings')} voids={removed.get('host_voids')} expected {expected - 2}"
                )
            if "office" in doc_name.lower() and (
                removed.get("host_openings") != 75 or removed.get("host_voids") != 75
            ):
                failures.append(
                    f"office delete openings={removed.get('host_openings')} voids={removed.get('host_voids')} expected 75"
                )
            deleted_rows = removed.get("deleted") or []
            if len(deleted_rows) != 2:
                failures.append(f"deleted rows={len(deleted_rows)}")
            for row in deleted_rows:
                if row.get("inside") is not True:
                    failures.append(f"deleted center is air id={row.get('id')}")
            for row in pair:
                gone = send_raw(sock, "get_object_info", {"id": row["id"]})
                if gone.get("status") != "error":
                    failures.append(f"marker {row['id']} still exists")
            wall_deleted = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
            if wall_deleted.get("forsk:id") != wall_fid:
                failures.append("delete changed wall id")
            if wall_deleted.get("forsk:thickness") != wall_thick:
                failures.append(
                    f"delete thickness {wall_deleted.get('forsk:thickness')} != {wall_thick}"
                )
            if wall_ids(sock) != walls_before:
                failures.append(f"A-WALL ids changed on delete: {wall_ids(sock)}")

            print("==> add one window back on the same wall")
            try:
                back_t = float(pair[0]["t"])
            except (TypeError, ValueError):
                back_t = 0.5
            added = send_command(sock, "add_opening", {
                "opening_kind": "window",
                "host_id": host_id,
                "t": back_t,
                "width": float(pair[0]["width"] or 1200),
                "sill": float(pair[0]["sill"] or 900),
                "head": float(pair[0]["head"] or 2100),
            })
            print(f"    {added.get('message')} openings={added.get('host_openings')} voids={added.get('host_voids')}")
            if added.get("host_id") != host_id:
                failures.append(f"add host {added.get('host_id')} != {host_id}")
            if added.get("host_openings") != expected - 1 or added.get("host_voids") != expected - 1:
                failures.append(
                    f"add openings={added.get('host_openings')} voids={added.get('host_voids')} expected {expected - 1}"
                )
            new_id = str(added.get("marker_id") or "").lower()
            new_row = None
            for item in added.get("markers") or []:
                if str(item.get("id") or "").lower() == new_id:
                    new_row = item
                    break
            if new_row is None:
                failures.append("added marker missing from host report")
            elif new_row.get("inside") is not False:
                failures.append("added opening is not a void")
            wall_added = attrs_of(send_command(sock, "get_object_info", {"id": host_id}))
            if wall_added.get("forsk:thickness") != wall_thick:
                failures.append("add changed thickness")
            if wall_added.get("forsk:id") != wall_fid:
                failures.append("add changed wall id")
            if wall_ids(sock) != walls_before:
                failures.append("A-WALL ids changed on add")

            print(f"==> opening types, counts stay {expected - 1}")
            check_opening_types(
                sock,
                marker_id,
                str(added.get("marker_id") or ""),
                host_id,
                wall_fid,
                wall_thick,
                expected - 1,
                failures,
            )

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
