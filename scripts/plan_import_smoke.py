#!/usr/bin/env python3
"""Plan import (F7) smoke on the plan1 pack in forsk-private.

One detection per run, into a blank millimetre document:
  tectly     raw Tectly JSON -> forsk.plan_import.v0 (checked against the
             reference conversion within 10 mm), then imported
  pdfvector  the vector-PDF extractor's plan file, imported as it is

Import and check the counts and the receipt. Set the scale from a known
dimension: the sheet is A3 at 1:100, so the page image's two top corners are
42 000 mm apart. Repeat the step, scale away and back, and check nothing
drifted. Capture the plan with the underlay. Bake with the panel's own
sequence, run rooms_detect, capture again. The bake is checked against what
the import said it would do (outlines, free walls, uncut openings).

Prints at most nine lines: a PASS or FAIL line, then one line per step.
The plan is a client drawing: it is read from forsk-private and never copied
into a repo. The plan file and captures go to /tmp.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/plan_import_smoke.py tectly|pdfvector PLAN1_DIR
"""

from __future__ import annotations

import base64
import json
import os
import socket
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "server" / "src"))

import forsk_plan_import  # noqa: E402

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))

# plan1's page: 3308 x 2339 px at 200 dpi, 1:100. Tectly got the 1423 x 1684
# crop whose top-left is at page px (694, 327). See the pack's README.
TECTLY_FRAME = dict(crop_size=(1423, 1684), crop_offset=(694, 327), page_size=(3308, 2339), dpi=200, scale=100)
# A3 is 420 mm wide: 42 000 mm on the plan at 1:100.
PAGE_WIDTH_MM = 42000.0

# The same numbers the headless plan1 tests pin (PlanImportTests).
EXPECT = {
    "tectly": dict(
        walls_detected=41, walls=30, doors=7, windows=12, loose=1, uncut=4, rooms=11,
        unlabelled=3, outlines=5, free_walls=3, diagonal=0, status="unconfirmed"),
    "pdfvector": dict(
        walls_detected=54, walls=47, doors=12, windows=10, loose=0, uncut=0, rooms=12,
        unlabelled=0, outside=0, outlines=1, free_walls=0, diagonal=4, status="detected"),
}
CAPTURE_MARGIN_MM = 1500.0
DRIFT_MM = 0.001


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


def clip(text: object, width: int = 170) -> str:
    one = " ".join(str(text or "").split())
    return one if len(one) <= width else one[: width - 3] + "..."


def find_layer(nodes: list, name: str) -> dict | None:
    for node in nodes or []:
        if node.get("name") == name:
            return node
        found = find_layer(node.get("children") or [], name)
        if found:
            return found
    return None


def layer_objects(sock: socket.socket, layer: str) -> list[dict]:
    found = send_command(sock, "get_objects", {
        "layer_filter": layer,
        "limit": 200,
        "include_geometry": False,
        "include_attributes": True,
        "include_hidden": True,
    })
    return found.get("objects") or []


def bbox_of(sock: socket.socket, object_id: str) -> list[float]:
    info = send_command(sock, "get_object_info", {"id": object_id})
    box = info.get("bounding_box") or [[0, 0, 0], [0, 0, 0]]
    return [float(box[0][0]), float(box[0][1]), float(box[1][0]), float(box[1][1])]


def by_kind(rows: list[dict], kind: str) -> list[dict]:
    return [row for row in rows if (row.get("attributes") or {}).get("forsk:import_kind") == kind]


def convert_tectly(plan1: Path, out: Path, failures: list) -> str:
    raw = json.loads((plan1 / "plan1_tectly_raw.json").read_text(encoding="utf-8"))
    want = json.loads((plan1 / "plan1_forsk.json").read_text(encoding="utf-8"))
    got = forsk_plan_import.tectly_to_plan(raw, source_file="plan1_tectly_raw.json", **TECTLY_FRAME)
    out.write_text(json.dumps(got, indent=1, ensure_ascii=False), encoding="utf-8")
    worst = 0.0
    if len(got["walls"]) != len(want["walls"]):
        failures.append(f"convert walls {len(got['walls'])} reference {len(want['walls'])}")
    for mine, ref in zip(got["walls"], want["walls"]):
        for key in ("start", "end"):
            worst = max(worst, abs(mine[key][0] - ref[key][0]), abs(mine[key][1] - ref[key][1]))
        worst = max(worst, abs(mine["thickness"] - ref["thickness"]))
    if worst > 10:
        failures.append(f"convert {worst:.0f} mm from the reference")
    return (
        f"convert {len(got['walls'])} walls {len(got['openings'])} openings {len(got['rooms'])} rooms, "
        f"{worst:.0f} mm from the reference at most"
    )


def capture(sock: socket.socket, png: str, bbox: list[float]) -> None:
    shot = send_command(sock, "capture_viewport", {
        "viewport": "top",
        "width": 1000,
        "height": 1000,
        "zoom_bbox": [
            bbox[0] - CAPTURE_MARGIN_MM, bbox[1] - CAPTURE_MARGIN_MM,
            bbox[2] + CAPTURE_MARGIN_MM, bbox[3] + CAPTURE_MARGIN_MM,
        ],
        "show_grid": False,
        "show_axes": False,
    })
    Path(png).write_bytes(base64.b64decode(shot.get("image_data") or ""))


def run(source: str, plan1: Path, sock: socket.socket, failures: list, lines: list) -> None:
    expect = EXPECT[source]
    image = plan1 / "plan1_200.png"
    if source == "tectly":
        plan = Path("/tmp/forsk-smoke-import-tectly.json")
        lines.append(convert_tectly(plan1, plan, failures))
    else:
        plan = plan1 / "plan1_pdfvector.json"

    summary = send_command(sock, "get_document_summary", {})
    units = str((summary.get("meta_data") or {}).get("units") or "")
    if units.lower() not in ("millimeters", "millimetres"):
        raise SmokeError(f"units={units!r}")
    if int(summary.get("object_count") or 0) > 40:
        raise SmokeError("document is not blank")

    # 1. Import: counts and receipt.
    imported = send_command(sock, "plan_import", {"image_path": str(image), "plan_path": str(plan)})
    scale = imported.get("scale") or {}
    got = {key: imported.get(key) for key in expect if key != "status"}
    got["status"] = scale.get("status")
    wrong = {key: (got[key], want) for key, want in expect.items() if got[key] != want}
    if wrong:
        failures.append("import " + ", ".join(f"{key} {have} expected {want}" for key, (have, want) in wrong.items()))
    review = imported.get("review") or []
    lines.append(
        f"import walls {imported.get('walls')}/{imported.get('walls_detected')} doors {imported.get('doors')} "
        f"windows {imported.get('windows')} loose {imported.get('loose')} uncut {imported.get('uncut')} "
        f"rooms {imported.get('rooms')} unlabelled {imported.get('unlabelled')} outside {imported.get('outside')} "
        f"outlines {imported.get('outlines')} free {imported.get('free_walls')} diagonal {imported.get('diagonal')} "
        f"review {len(review)}"
    )
    message = str(imported.get("message") or "")
    lines.append("receipt " + clip(message))
    if "Scale" not in message or ("Review:" not in message and "Nothing to review" not in message):
        failures.append("receipt has no scale or review part")
    for row in imported.get("warnings") or []:
        failures.append(f"import warning {row}")

    # 2. Underlay: one locked picture on X-PLAN, the page's size; the panel offers Set scale.
    underlay = imported.get("underlay") or {}
    width = float(underlay.get("width_mm") or 0)
    layer = find_layer(send_command(sock, "get_document_summary", {}).get("layer_hierarchy") or [], "X-PLAN") or {}
    pictures = by_kind(layer_objects(sock, "X-PLAN"), "underlay")
    chip = send_command(sock, "panel_daylight", {"action": "state", "text": "set the scale of the plan"})
    lines.append(
        f"underlay X-PLAN locked {layer.get('locked')} objects {len(pictures)} "
        f"{width:.0f} x {float(underlay.get('height_mm') or 0):.0f} mm, chip {chip.get('import_label')} "
        f"visible {chip.get('import_visible')}, intent {chip.get('intent')}"
    )
    if layer.get("locked") is not True or len(pictures) != 1 or abs(width - 42011.6) > 1.0:
        failures.append(f"underlay locked {layer.get('locked')} objects {len(pictures)} width {width:.1f}")
    if chip.get("import_label") != "Set scale" or chip.get("import_visible") is not True or chip.get("intent") != "import":
        failures.append(f"chip {chip.get('import_label')} visible {chip.get('import_visible')} intent {chip.get('intent')}")

    # 3. Scale from the known page width, in the detection's own mm.
    ends = {"p1": [0, 0], "p2": [width, 0], "frame": "source"}
    measured = send_command(sock, "plan_scale", ends)
    first = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    walls = by_kind(layer_objects(sock, "wall"), "wall")
    wall_id = walls[0].get("id") if walls else None
    before = bbox_of(sock, wall_id) if wall_id else []
    repeat = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    away = send_command(sock, "plan_scale", {**ends, "length_mm": 2 * PAGE_WIDTH_MM})
    back = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    after = bbox_of(sock, wall_id) if wall_id else []
    drift = max((abs(a - b) for a, b in zip(before, after)), default=-1.0)
    pictures = by_kind(layer_objects(sock, "X-PLAN"), "underlay")
    page = bbox_of(sock, pictures[0].get("id")) if pictures else [0, 0, 0, 0]
    thick = [float((w.get("attributes") or {}).get("forsk:import_thickness") or 0) for w in walls]
    chip = send_command(sock, "panel_daylight", {"action": "state"})
    lines.append(
        f"scale {float(measured.get('measured_mm') or 0):.1f} -> {PAGE_WIDTH_MM:.0f} mm x{float(first.get('relative') or 0):.6f} "
        f"moved {first.get('scaled')} rounded {first.get('walls_rounded')}, repeat x{float(repeat.get('relative') or 0):.9f}, "
        f"x2 and back drift {drift:.4f} mm, page {page[2] - page[0]:.2f} mm wide, status {back.get('status')}, "
        f"chip visible {chip.get('import_visible')}"
    )
    if abs(float(measured.get("measured_mm") or 0) - width) > 0.1 or measured.get("scaled") != 0:
        failures.append(f"scale measure {measured.get('measured_mm')} scaled {measured.get('scaled')}")
    if abs(float(first.get("relative") or 0) - PAGE_WIDTH_MM / width) > 1e-9:
        failures.append(f"scale relative {first.get('relative')}")
    if first.get("scaled") != imported.get("objects") or first.get("walls_rounded") != imported.get("walls"):
        failures.append(
            f"scale moved {first.get('scaled')} of {imported.get('objects')} rounded {first.get('walls_rounded')}")
    if abs(float(repeat.get("relative") or 0) - 1.0) > 1e-12:
        failures.append(f"scale repeat moved the plan x{repeat.get('relative')}")
    if abs(float(away.get("relative") or 0) - 2.0) > 1e-9 or abs(float(back.get("relative") or 0) - 0.5) > 1e-9:
        failures.append(f"scale x2 {away.get('relative')} back {back.get('relative')}")
    if drift < 0 or drift > DRIFT_MM or abs(page[2] - page[0] - PAGE_WIDTH_MM) > 0.01 or abs(page[0]) > 0.01 or abs(page[3]) > 0.01:
        failures.append(f"scale drift {drift} page {page}")
    if any(t <= 0 or abs(t - round(t / 10) * 10) > 1e-6 for t in thick) or len(thick) != imported.get("walls"):
        failures.append("wall thickness is not on 10 mm after the scale")
    if back.get("status") != "user" or chip.get("import_visible") is not False:
        failures.append(f"scale status {back.get('status')} chip visible {chip.get('import_visible')}")

    # 4. The plan as reviewed: underlay under the 2D geometry.
    factor = PAGE_WIDTH_MM / width
    data = json.loads(plan.read_text(encoding="utf-8"))
    xs = [p[0] * factor for wall in data["walls"] for p in (wall["start"], wall["end"])]
    ys = [p[1] * factor for wall in data["walls"] for p in (wall["start"], wall["end"])]
    zoom = [min(xs), min(ys), max(xs), max(ys)]
    review_png = f"/tmp/forsk-smoke-import-{source}-1000.png"
    capture(sock, review_png, zoom)

    # 5. Bake, the panel's sequence. Checked against what the import said the bake would do.
    floor = send_command(sock, "floor_from_layer", {"layer": "wall", "thickness": 400})
    baked = send_command(sock, "walls_from_layer", {"layer": "wall", "height": 3000})
    roof = send_command(sock, "roof_flat_from_walls", {})
    doors = send_command(sock, "openings_from_layer", {"layer": "door"})
    windows = send_command(sock, "openings_from_layer", {"layer": "window"})
    markers = send_command(sock, "rooms_from_layer", {})
    cut = int(doors.get("cut_count") or 0) + int(windows.get("cut_count") or 0)
    failed = int(doors.get("failed_count") or 0) + int(windows.get("failed_count") or 0)
    openings = int(imported.get("doors") or 0) + int(imported.get("windows") or 0)
    outlines = int(imported.get("outlines") or 0)
    free = int(imported.get("free_walls") or 0)
    uncut = int(imported.get("uncut") or 0)
    lines.append(
        f"bake floor {floor.get('count')} walls {baked.get('count')} roof {roof.get('count')} "
        f"doors {doors.get('cut_count')}/{imported.get('doors')} windows {windows.get('cut_count')}/{imported.get('windows')} "
        f"failed {failed} room markers {markers.get('count')}"
    )
    if floor.get("count") != outlines or baked.get("count") != outlines - free:
        failures.append(
            f"bake floor {floor.get('count')} expected {outlines}, walls {baked.get('count')} expected {outlines - free}")
    if cut != openings - uncut or failed != uncut:
        failures.append(f"bake cut {cut} expected {openings - uncut}, failed {failed} expected {uncut}")
    if markers.get("count") != imported.get("rooms"):
        failures.append(f"bake room markers {markers.get('count')} expected {imported.get('rooms')}")

    # 6. Rooms: the imported outlines win, detection fills the rest. The scale is refused once 3D exists.
    rooms = send_command(sock, "rooms_detect", {})
    refused = send_raw(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    lines.append(
        f"rooms {rooms.get('count')} detected {rooms.get('detected')} kept {rooms.get('kept')} "
        f"open {len(rooms.get('open') or [])}, scale after bake {refused.get('status')}: "
        + clip(rooms.get("message"), 90)
    )
    if int(rooms.get("count") or 0) < int(imported.get("rooms") or 0):
        failures.append(f"rooms {rooms.get('count')} expected at least {imported.get('rooms')}")
    if refused.get("status") != "error" or "clear_generated" not in str(refused.get("message")):
        failures.append("plan_scale ran on a generated model")

    capture(sock, f"/tmp/forsk-smoke-import-{source}-baked-1000.png", zoom)


def main() -> int:
    if len(sys.argv) != 3 or sys.argv[1] not in EXPECT:
        print("usage: plan_import_smoke.py tectly|pdfvector PLAN1_DIR", file=sys.stderr)
        return 1
    source, plan1 = sys.argv[1], Path(sys.argv[2])
    failures: list = []
    lines: list = []
    try:
        sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
        sock.settimeout(TIMEOUT)
        try:
            run(source, plan1, sock, failures, lines)
        finally:
            sock.close()
    except (SmokeError, OSError, KeyError, ValueError) as exc:
        failures.append(clip(exc, 200))
    print(f"import {source}: " + ("PASS" if not failures else "FAIL " + "; ".join(failures)))
    for line in lines:
        print("  " + line)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
