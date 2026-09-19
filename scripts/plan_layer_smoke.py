#!/usr/bin/env python3
"""Live plan-layer smoke: clear → floor → walls → roof → openings → tags → clear.

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


def attrs_of(info: dict) -> dict:
    raw = info.get("attributes") or {}
    return raw if isinstance(raw, dict) else {}


def object_gone(sock: socket.socket, oid: str) -> bool:
    try:
        send_command(sock, "get_object_info", {"id": oid})
        return False
    except SmokeError:
        return True


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

        print("==> clear_generated (include_untagged_prefixes for pre-tag leftovers)")
        cleared = send_command(sock, "clear_generated", {
            "include_untagged_prefixes": True,
        })
        print(f"    deleted={cleared.get('count')} dry_run={cleared.get('dry_run')}")

        print("==> floor_from_layer")
        floor = send_command(sock, "floor_from_layer", {
            "layer": "wall",
            "thickness": 400,
            "target_layer": "A-FLOR",
            "name_prefix": "floor-",
        })
        print(f"    {floor.get('message')} count={floor.get('count')}")
        floor_ids = floor.get("ids") or []
        if not floor_ids:
            print("FAIL: no floor slabs created")
            return 1

        floor_info = send_command(sock, "get_object_info", {"id": floor_ids[0]})
        floor_attrs = attrs_of(floor_info)
        print(f"    {floor_info.get('name')} forsk:kind={floor_attrs.get('forsk:kind')} "
              f"generated={floor_attrs.get('forsk:generated')}")
        if floor_attrs.get("forsk:kind") != "floor":
            failures.append(f"floor forsk:kind={floor_attrs.get('forsk:kind')}")
        if floor_attrs.get("forsk:generated") != "1":
            failures.append(f"floor forsk:generated={floor_attrs.get('forsk:generated')}")

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
        wall_count_first = len(ids)

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

        wall_info = send_command(sock, "get_object_info", {"id": ids[0]})
        wall_attrs = attrs_of(wall_info)
        print(f"    wall tags forsk:kind={wall_attrs.get('forsk:kind')} "
              f"generated={wall_attrs.get('forsk:generated')}")
        if wall_attrs.get("forsk:kind") != "wall":
            failures.append(f"wall forsk:kind={wall_attrs.get('forsk:kind')}")
        if wall_attrs.get("forsk:generated") != "1":
            failures.append(f"wall forsk:generated={wall_attrs.get('forsk:generated')}")

        print("==> default wall material (By Layer plaster)")
        wall_attrs_mat = send_command(sock, "get_object_attributes", {"id": ids[0]})
        print(f"    {wall_attrs_mat.get('name')} material_source={wall_attrs_mat.get('material_source')} "
              f"material_index={wall_attrs_mat.get('material_index')}")
        if wall_attrs_mat.get("material_source") != "MaterialFromLayer":
            failures.append(
                f"wall material_source={wall_attrs_mat.get('material_source')} expected MaterialFromLayer"
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

        print("==> roof_flat_from_walls")
        roof = send_command(sock, "roof_flat_from_walls", {})
        print(f"    {roof.get('message')} count={roof.get('count')} "
              f"bbox={roof.get('bbox')} warnings={roof.get('warnings')}")
        roof_ids = roof.get("ids") or []
        if (roof.get("count") or 0) < 1 or not roof_ids:
            failures.append("roof_flat_from_walls count < 1")
            roof_ids = []
        else:
            roof_info = send_command(sock, "get_object_info", {"id": roof_ids[0]})
            roof_attrs = attrs_of(roof_info)
            print(f"    {roof_info.get('name')} layer={roof_info.get('layer')} "
                  f"forsk:kind={roof_attrs.get('forsk:kind')} "
                  f"generated={roof_attrs.get('forsk:generated')} "
                  f"roof_type={roof_attrs.get('forsk:roof_type')} "
                  f"bbox={roof_info.get('bounding_box')}")
            if roof_info.get("layer") != "A-ROOF":
                failures.append(f"roof layer={roof_info.get('layer')} expected A-ROOF")
            if roof_attrs.get("forsk:kind") != "roof":
                failures.append(f"roof forsk:kind={roof_attrs.get('forsk:kind')}")
            if roof_attrs.get("forsk:generated") != "1":
                failures.append(f"roof forsk:generated={roof_attrs.get('forsk:generated')}")
            if roof_attrs.get("forsk:roof_type") != "flat":
                failures.append(f"roof forsk:roof_type={roof_attrs.get('forsk:roof_type')}")
            roof_bbox = roof_info.get("bounding_box") or roof.get("bbox")
            if roof_bbox and len(roof_bbox) == 2:
                top_z = float(roof_bbox[1][2])
                height = bbox_height(roof_bbox)
                if abs(top_z - 3000) > 5:
                    failures.append(f"roof top Z={top_z} expected ~3000")
                if abs(height - 200) > 5:
                    failures.append(f"roof height={height} expected ~200")
            else:
                failures.append("roof bbox missing")

        print("==> openings_from_layer door")
        doors = send_command(sock, "openings_from_layer", {"layer": "door"})
        print(f"    {doors.get('message')} cut={doors.get('cut_count')} "
              f"fail={doors.get('failed_count')} openings={doors.get('opening_count')} "
              f"markers={len(doors.get('marker_ids') or [])}")
        if doors.get("failures"):
            for f in doors["failures"][:8]:
                print(f"    door fail: {f}")
        if (doors.get("cut_count") or 0) < 1:
            failures.append("no door openings cut")

        print("==> openings_from_layer window")
        windows = send_command(sock, "openings_from_layer", {"layer": "window"})
        print(f"    {windows.get('message')} cut={windows.get('cut_count')} "
              f"fail={windows.get('failed_count')} openings={windows.get('opening_count')} "
              f"markers={len(windows.get('marker_ids') or [])}")
        if windows.get("failures"):
            for f in windows["failures"][:8]:
                print(f"    window fail: {f}")
        if (windows.get("cut_count") or 0) < 1:
            failures.append("no window openings cut")

        marker_ids = list(doors.get("marker_ids") or []) + list(windows.get("marker_ids") or [])
        if not marker_ids:
            open_objs = send_command(sock, "get_objects", {
                "layer_filter": "A-OPEN",
                "include_geometry": False,
                "limit": 50,
            })
            marker_ids = [o.get("id") for o in (open_objs.get("objects") or []) if o.get("id")]
        if not marker_ids:
            failures.append("no opening markers on A-OPEN")
        else:
            marker_info = send_command(sock, "get_object_info", {"id": marker_ids[0]})
            marker_attrs = attrs_of(marker_info)
            print(f"    marker {marker_info.get('name')} kind={marker_attrs.get('forsk:kind')} "
                  f"host={marker_attrs.get('forsk:host')} "
                  f"opening_kind={marker_attrs.get('forsk:opening_kind')}")
            if marker_attrs.get("forsk:kind") != "opening_marker":
                failures.append(f"marker forsk:kind={marker_attrs.get('forsk:kind')}")
            host = marker_attrs.get("forsk:host") or ""
            if len(host) < 32:
                failures.append(f"marker forsk:host missing/short: {host!r}")
            okind = (marker_attrs.get("forsk:opening_kind") or "").lower()
            if okind not in ("door", "window"):
                failures.append(f"marker forsk:opening_kind={marker_attrs.get('forsk:opening_kind')}")

        after = send_command(sock, "get_document_summary", {})
        furniture_after = layer_count(after, "furniture")
        wall_curves_after = layer_count(after, "wall")
        print(f"==> after create  objects={after.get('object_count')} "
              f"by_layer={after.get('objects_by_layer')}")
        if furniture_after != furniture_before:
            failures.append(f"furniture changed {furniture_before} -> {furniture_after}")
        if wall_curves_after != wall_curves:
            failures.append(f"source wall curves changed {wall_curves} -> {wall_curves_after}")

        wall_id = ids[0]
        before_info = send_command(sock, "get_object_info", {"id": wall_id})
        before_minx = before_info["bounding_box"][0][0]
        print(f"==> move {before_info.get('name')} +300 X")
        send_command(sock, "modify_object", {
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

        print("==> clear_generated defaults")
        cleared2 = send_command(sock, "clear_generated", {})
        print(f"    deleted={cleared2.get('count')}")
        for oid, label in [
            (ids[0], "wall"),
            (floor_ids[0], "floor"),
            (roof_ids[0] if roof_ids else None, "roof"),
            (marker_ids[0] if marker_ids else None, "marker"),
        ]:
            if not oid:
                continue
            if object_gone(sock, oid):
                print(f"    {label} {oid} gone")
            else:
                failures.append(f"{label} {oid} still present after clear_generated")

        after_clear = send_command(sock, "get_document_summary", {})
        wall_curves_final = layer_count(after_clear, "wall")
        print(f"==> after clear  by_layer={after_clear.get('objects_by_layer')}")
        if wall_curves_final != wall_curves:
            failures.append(
                f"source wall curves changed after clear {wall_curves} -> {wall_curves_final}"
            )
        if layer_count(after_clear, "A-WALL") > 0:
            failures.append(f"A-WALL still has {layer_count(after_clear, 'A-WALL')} object(s)")
        if layer_count(after_clear, "A-FLOR") > 0:
            failures.append(f"A-FLOR still has {layer_count(after_clear, 'A-FLOR')} object(s)")
        if layer_count(after_clear, "A-ROOF") > 0:
            failures.append(f"A-ROOF still has {layer_count(after_clear, 'A-ROOF')} object(s)")
        if layer_count(after_clear, "A-OPEN") > 0:
            failures.append(f"A-OPEN still has {layer_count(after_clear, 'A-OPEN')} object(s)")

        print("==> optional second walls_from_layer count check")
        walls2 = send_command(sock, "walls_from_layer", {
            "layer": "wall",
            "height": 3000,
            "target_layer": "A-WALL",
            "name_prefix": "wall-",
        })
        wall_count_second = walls2.get("count") or 0
        print(f"    second walls count={wall_count_second} (first={wall_count_first})")
        if wall_count_second != wall_count_first:
            failures.append(
                f"second walls count {wall_count_second} != first {wall_count_first}"
            )
        send_command(sock, "clear_generated", {})

        print()
        print(f"walls={wall_count_first} floor={len(floor_ids)} "
              f"roof={len(roof_ids)} "
              f"door_cuts={doors.get('cut_count')} "
              f"window_cuts={windows.get('cut_count')} "
              f"markers={len(marker_ids)}")
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
