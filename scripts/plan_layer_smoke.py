#!/usr/bin/env python3
"""Live plan-layer smoke: walls_from_layer + openings_from_layer.

Requires Rhino 7 with mcpstart and the demo DXF/.3dm open (mm).
Does not go through the Python MCP server — talks framed TCP like mvp_smoke.py.

Usage:
  python3 scripts/plan_layer_smoke.py
"""

from __future__ import annotations

import json
import os
import socket
import sys
from pathlib import Path

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "180"))
FRAME_HEADER_SIZE = 4
MAX_FRAME_SIZE = 64 * 1024 * 1024
OUT_DIR = Path(__file__).resolve().parent / "smoke_output"


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


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode("utf-8")
    sock.sendall(len(payload).to_bytes(FRAME_HEADER_SIZE, "big") + payload)
    header = recv_exact(sock, FRAME_HEADER_SIZE)
    if header.startswith(b"{"):
        raise SmokeError("plugin sent unframed JSON")
    length = int.from_bytes(header, "big")
    if length <= 0 or length > MAX_FRAME_SIZE:
        raise SmokeError(f"invalid frame length {length}")
    response = json.loads(recv_exact(sock, length).decode("utf-8"))
    if response.get("status") == "error":
        raise SmokeError(f"{cmd_type}: {response.get('message', 'unknown error')}")
    if response.get("status") != "success":
        raise SmokeError(f"{cmd_type}: unexpected response {response!r}")
    return response.get("result") or {}


def layer_count(summary: dict, name: str) -> int:
    by_layer = summary.get("objects_by_layer") or {}
    for key, value in by_layer.items():
        if str(key).lower() == name.lower():
            return int(value)
    return 0


def bbox_height(bbox) -> float:
    if not bbox or len(bbox) != 2:
        return 0.0
    return float(bbox[1][2]) - float(bbox[0][2])


def main() -> int:
    print(f"connecting to {HOST}:{PORT} timeout={TIMEOUT}s ...")
    try:
        sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    except OSError as exc:
        print(f"could not connect: {exc}", file=sys.stderr)
        return 2
    sock.settimeout(TIMEOUT)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    failures: list[str] = []

    try:
        summary = send_command(sock, "get_document_summary", {})
        meta = summary.get("meta_data") or {}
        units = str(meta.get("units") or "")
        print(f"==> document  name={meta.get('name')} units={units} "
              f"objects={summary.get('object_count')} layers={summary.get('layer_count')}")
        print(f"    by_layer={summary.get('objects_by_layer')}")
        if "millimet" not in units.lower():
            print(f"FAIL units={units} (need millimetres)")
            return 1

        furniture_before = layer_count(summary, "furniture")
        wall_curves = layer_count(summary, "wall")
        door_objs = layer_count(summary, "door")
        window_objs = layer_count(summary, "window")
        print(f"    wall_curves={wall_curves} door={door_objs} window={window_objs} "
              f"furniture={furniture_before}")
        if wall_curves == 0:
            print("FAIL: no objects on layer 'wall'")
            return 1

        existing_walls = send_command(sock, "get_objects", {
            "layer_filter": "A-WALL",
            "include_geometry": False,
            "limit": 200,
        })
        existing_ids = [o.get("id") for o in (existing_walls.get("objects") or []) if o.get("id")]
        if existing_ids:
            print(f"==> clearing {len(existing_ids)} existing A-WALL solid(s)")
            for eid in existing_ids:
                send_command(sock, "delete_object", {"id": eid})

        print("==> walls_from_layer")
        walls = send_command(sock, "walls_from_layer", {
            "layer": "wall",
            "height": 3000,
            "target_layer": "A-WALL",
            "name_prefix": "wall-",
        })
        print(f"    {walls.get('message')} count={walls.get('count')} "
              f"closed={walls.get('closed')} skipped={walls.get('skipped')} "
              f"warnings={walls.get('warnings')}")
        ids = walls.get("ids") or []
        if not ids:
            print("FAIL: no wall solids created")
            return 1

        heights = []
        for wid in ids:
            info = send_command(sock, "get_object_info", {"id": wid})
            h = bbox_height(info.get("bounding_box"))
            heights.append(h)
            print(f"    {info.get('name')} layer={info.get('layer')} "
                  f"type={info.get('type')} height={h:.1f} bbox={info.get('bounding_box')}")
            if info.get("layer") != "A-WALL":
                failures.append(f"{info.get('name')} not on A-WALL")
            if abs(h - 3000) > 5:
                failures.append(f"{info.get('name')} height {h} != 3000")

        print("==> default wall material (By Layer plaster)")
        wall_attrs = send_command(sock, "get_object_attributes", {"id": ids[0]})
        print(f"    {wall_attrs.get('name')} material_source={wall_attrs.get('material_source')} "
              f"material_index={wall_attrs.get('material_index')}")
        if wall_attrs.get("material_source") != "MaterialFromLayer":
            failures.append(
                f"wall material_source={wall_attrs.get('material_source')} expected MaterialFromLayer"
            )
        if walls.get("material_name") and walls.get("material_name") != "M-PLASTER":
            failures.append(f"wall material_name={walls.get('material_name')} expected M-PLASTER")

        print("==> set_layer_material A-WALL wood, then plaster")
        wood = send_command(sock, "set_layer_material", {
            "layer_name": "A-WALL",
            "preset": "wood",
        })
        print(f"    {wood.get('message')} material={wood.get('material_name')} "
              f"objects={wood.get('objects_updated')}")
        if wood.get("material_name") != "M-WOOD":
            failures.append(f"wood override material={wood.get('material_name')}")
        plaster = send_command(sock, "set_layer_material", {
            "layer_name": "A-WALL",
            "preset": "plaster",
        })
        print(f"    {plaster.get('message')} material={plaster.get('material_name')}")
        if plaster.get("material_name") != "M-PLASTER":
            failures.append(f"plaster restore material={plaster.get('material_name')}")

        print("==> openings_from_layer door")
        doors = send_command(sock, "openings_from_layer", {"layer": "door"})
        print(f"    {doors.get('message')} cut={doors.get('cut_count')} "
              f"fail={doors.get('failed_count')} openings={doors.get('opening_count')}")
        if doors.get("failures"):
            for f in doors["failures"][:8]:
                print(f"    door fail: {f}")
        if (doors.get("cut_count") or 0) < 1:
            failures.append("no door openings cut")

        print("==> openings_from_layer window")
        windows = send_command(sock, "openings_from_layer", {"layer": "window"})
        print(f"    {windows.get('message')} cut={windows.get('cut_count')} "
              f"fail={windows.get('failed_count')} openings={windows.get('opening_count')}")
        if windows.get("failures"):
            for f in windows["failures"][:8]:
                print(f"    window fail: {f}")
        if (windows.get("cut_count") or 0) < 1:
            failures.append("no window openings cut")

        after = send_command(sock, "get_document_summary", {})
        furniture_after = layer_count(after, "furniture")
        wall_curves_after = layer_count(after, "wall")
        print(f"==> after  objects={after.get('object_count')} "
              f"by_layer={after.get('objects_by_layer')}")
        if furniture_after != furniture_before:
            failures.append(f"furniture changed {furniture_before} -> {furniture_after}")
        if wall_curves_after != wall_curves:
            failures.append(f"source wall curves changed {wall_curves} -> {wall_curves_after}")

        wall_id = ids[0]
        before_info = send_command(sock, "get_object_info", {"id": wall_id})
        before_minx = before_info["bounding_box"][0][0]
        print(f"==> move {before_info.get('name')} +300 X")
        moved = send_command(sock, "modify_object", {
            "id": wall_id,
            "translation": [300, 0, 0],
        })
        after_move = send_command(sock, "get_object_info", {"id": wall_id})
        after_minx = after_move["bounding_box"][0][0]
        dx = after_minx - before_minx
        print(f"    dx={dx:.1f} bbox={after_move.get('bounding_box')}")
        if abs(dx - 300) > 5:
            failures.append(f"move dx={dx} expected 300")
        send_command(sock, "undo", {"steps": 1})
        print("    undone move (walls+openings kept)")

        print("==> capture_viewport perspective")
        import base64
        cap = send_command(sock, "capture_viewport", {
            "viewport": "perspective",
            "width": 800,
            "height": 600,
            "zoom_to_fit": True,
        })
        path = OUT_DIR / "plan_layer_perspective.png"
        png = cap.get("image_data") or ""
        if png:
            path.write_bytes(base64.b64decode(png))
            print(f"    wrote {path}")
        else:
            print(f"    capture keys={list(cap.keys())}")

        print()
        print(f"walls={len(ids)} door_cuts={doors.get('cut_count')} "
              f"window_cuts={windows.get('cut_count')} "
              f"door_fail={doors.get('failed_count')} "
              f"window_fail={windows.get('failed_count')}")
        if failures:
            print("FAIL")
            for f in failures:
                print(f"  - {f}")
            return 1
        print("PASS")
        return 0
    except SmokeError as exc:
        print(f"FAIL {exc}")
        return 1
    finally:
        sock.close()


if __name__ == "__main__":
    sys.exit(main())
