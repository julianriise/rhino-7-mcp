#!/usr/bin/env python3
"""Plan import (F7) smoke on the plan1 pack in forsk-private.

One detection per run, into a blank millimetre document:
  tectly     raw Tectly JSON -> forsk.plan_import.v0 (checked against the
             reference conversion within 10 mm), then imported
  pdfvector  the vector-PDF extractor's plan file, imported as it is

Import and check the counts and the receipt: the walls come in merged, one
closed outline per connected run with its holes, none overlapping another,
and the wall layer holds exactly those loops. Set the scale from a known
dimension: the sheet is A3 at 1:100, so the page image's two top corners are
42 000 mm apart. Repeat the step, scale away and back, and check no corner of
any wall outline drifted. Capture the plan with the underlay. Bake with the panel's
own sequence, run rooms_detect, capture again. The bake is checked against
what the import's receipt said it would do (outlines, blocks the bake skips,
uncut openings), never against numbers written here.

Prints at most nine lines: a PASS or FAIL line, then one line per step. The
last four say what the pictures should show: the underlay's display mode, one
floor, wall and roof per outline, every imported room still a room with a
label, and the plan image drawn in both views.
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
import zlib
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

# What the clean-up makes of each detection: the same numbers the headless
# plan1 tests pin (PlanImportTests). What the bake should then do is read from
# the import's receipt, not written here.
EXPECT = {
    "tectly": dict(
        walls_detected=41, walls=30, doors=7, windows=12, loose=0, uncut=0, rooms=11,
        unlabelled=3, outlines=2, outline_holes=3, wall_pieces=41, overlaps=0, free_walls=1, blocks_skipped=0,
        gaps_closed=3, diagonal=0, status="unconfirmed"),
    "pdfvector": dict(
        walls_detected=54, walls=47, doors=12, windows=10, loose=0, uncut=0, rooms=12,
        unlabelled=0, outside=0, outlines=1, outline_holes=12, wall_pieces=54, overlaps=0, free_walls=0,
        blocks_skipped=0, gaps_closed=0, diagonal=4, status="detected"),
}
CAPTURE_MARGIN_MM = 1500.0
# get_objects gives corners to 0.01 mm: any drift it can show is a failure.
DRIFT_MM = 0.001
# The slabs are wireframe in the Top view, and the isocurves across them read as walls.
SLAB_LAYERS = ["A-FLOR", "A-ROOF"]


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


def wall_loops(sock: socket.socket) -> dict[str, list]:
    """The corners of every imported wall outline and of every hole in one, by object name."""
    rows = send_command(sock, "get_objects", {
        "layer_filter": "wall", "limit": 200, "include_geometry": True, "include_attributes": True,
        "include_hidden": True,
    }).get("objects") or []
    return {
        row.get("name"): [(float(p[0]), float(p[1])) for p in ((row.get("geometry") or {}).get("points") or [])]
        for row in by_kind(rows, "wall") + by_kind(rows, "wall-hole")
    }


def capture(sock: socket.socket, bbox: list[float], hide: list[str]) -> bytes:
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
        "hide_layers": hide,
    })
    return base64.b64decode(shot.get("image_data") or "")


def png_rows(data: bytes) -> list[bytes]:
    """The rows of a PNG as stored (each still filtered): enough to tell two pictures of one view apart."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SmokeError("the capture is not a PNG")
    at, packed, width, height, per_pixel = 8, bytearray(), 0, 0, 0
    while at + 8 <= len(data):
        length = int.from_bytes(data[at:at + 4], "big")
        kind, body = data[at + 4:at + 8], data[at + 8:at + 8 + length]
        if kind == b"IHDR":
            width, height = int.from_bytes(body[0:4], "big"), int.from_bytes(body[4:8], "big")
            per_pixel = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(body[9], 0) * body[8] // 8
        elif kind == b"IDAT":
            packed += body
        at += 12 + length
    raw = zlib.decompress(bytes(packed))
    stride = width * per_pixel + 1
    if not width or not per_pixel or len(raw) != stride * height:
        raise SmokeError("the capture PNG is not one this smoke reads")
    return [raw[y * stride:(y + 1) * stride] for y in range(height)]


def underlay_rows(shown: bytes, hidden: bytes) -> tuple[int, int]:
    """Rows of the view that change when X-PLAN goes off, and the rows there are. 0: the plan image is not drawn."""
    a, b = png_rows(shown), png_rows(hidden)
    if len(a) != len(b):
        raise SmokeError(f"two captures of one view differ in size: {len(a)} and {len(b)} rows")
    return sum(1 for mine, other in zip(a, b) if mine != other), len(a)


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
    # The wall layer holds the merged outlines and their holes, and nothing else of the import.
    drawn = layer_objects(sock, "wall")
    outers, holes = len(by_kind(drawn, "wall")), len(by_kind(drawn, "wall-hole"))
    lines.append(
        f"import overlaps {imported.get('overlaps')}, outlines {imported.get('outlines')} with {imported.get('outline_holes')} holes "
        f"from {imported.get('wall_pieces')} wall pieces ({outers} + {holes} loops on wall), "
        f"walls {imported.get('walls')}/{imported.get('walls_detected')} doors {imported.get('doors')} "
        f"windows {imported.get('windows')} loose {imported.get('loose')} uncut {imported.get('uncut')} "
        f"rooms {imported.get('rooms')} unlabelled {imported.get('unlabelled')} walls open around {imported.get('outside')} "
        f"free {imported.get('free_walls')} gaps closed {imported.get('gaps_closed')} "
        f"diagonal {imported.get('diagonal')} review {len(review)}"
    )
    if outers != imported.get("outlines") or holes != imported.get("outline_holes") or len(drawn) != outers + holes:
        failures.append(
            f"wall layer holds {len(drawn)} objects, {outers} outlines and {holes} holes, for "
            f"{imported.get('outlines')} outlines and {imported.get('outline_holes')} holes")
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
        f"underlay shown in {underlay.get('display')} whatever the view's mode, X-PLAN locked {layer.get('locked')} "
        f"objects {len(pictures)} {width:.0f} x {float(underlay.get('height_mm') or 0):.0f} mm, "
        f"chip {chip.get('import_label')} visible {chip.get('import_visible')}, intent {chip.get('intent')}"
    )
    if layer.get("locked") is not True or len(pictures) != 1 or abs(width - 42011.6) > 1.0:
        failures.append(f"underlay locked {layer.get('locked')} objects {len(pictures)} width {width:.1f}")
    if not underlay.get("display"):
        failures.append("underlay has no display mode of its own: a wireframe view shows nothing of it")
    if chip.get("import_label") != "Set scale" or chip.get("import_visible") is not True or chip.get("intent") != "import":
        failures.append(f"chip {chip.get('import_label')} visible {chip.get('import_visible')} intent {chip.get('intent')}")

    # 3. Scale from the known page width, in the detection's own mm.
    ends = {"p1": [0, 0], "p2": [width, 0], "frame": "source"}
    measured = send_command(sock, "plan_scale", ends)
    first = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    # Every corner of every wall outline and hole, before and after: one wall is not the plan.
    before = wall_loops(sock)
    repeat = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    away = send_command(sock, "plan_scale", {**ends, "length_mm": 2 * PAGE_WIDTH_MM})
    back = send_command(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    after = wall_loops(sock)
    moved = [
        max(abs(a[0] - b[0]), abs(a[1] - b[1]))
        for key, ring in before.items() for a, b in zip(ring, after.get(key) or [])
    ]
    whole = len(before) == int(imported.get("outlines") or 0) + int(imported.get("outline_holes") or 0) and all(
        len(ring) >= 4 and len(after.get(key) or []) == len(ring) for key, ring in before.items())
    drift = max(moved) if whole and moved else -1.0
    pictures = by_kind(layer_objects(sock, "X-PLAN"), "underlay")
    page = bbox_of(sock, pictures[0].get("id")) if pictures else [0, 0, 0, 0]
    chip = send_command(sock, "panel_daylight", {"action": "state"})
    lines.append(
        f"scale {float(measured.get('measured_mm') or 0):.1f} -> {PAGE_WIDTH_MM:.0f} mm x{float(first.get('relative') or 0):.6f} "
        f"moved {first.get('scaled')}, repeat x{float(repeat.get('relative') or 0):.9f}, "
        f"x2 and back {len(before)} wall loops drift {drift:.2f} mm at most, page {page[2] - page[0]:.2f} mm wide, "
        f"status {back.get('status')}, chip visible {chip.get('import_visible')}"
    )
    if abs(float(measured.get("measured_mm") or 0) - width) > 0.1 or measured.get("scaled") != 0:
        failures.append(f"scale measure {measured.get('measured_mm')} scaled {measured.get('scaled')}")
    if abs(float(first.get("relative") or 0) - PAGE_WIDTH_MM / width) > 1e-9:
        failures.append(f"scale relative {first.get('relative')}")
    if first.get("scaled") != imported.get("objects"):
        failures.append(f"scale moved {first.get('scaled')} of {imported.get('objects')}")
    if abs(float(repeat.get("relative") or 0) - 1.0) > 1e-12:
        failures.append(f"scale repeat moved the plan x{repeat.get('relative')}")
    if abs(float(away.get("relative") or 0) - 2.0) > 1e-9 or abs(float(back.get("relative") or 0) - 0.5) > 1e-9:
        failures.append(f"scale x2 {away.get('relative')} back {back.get('relative')}")
    if drift < 0 or drift > DRIFT_MM or abs(page[2] - page[0] - PAGE_WIDTH_MM) > 0.01 or abs(page[0]) > 0.01 or abs(page[3]) > 0.01:
        failures.append(f"scale drift {drift} page {page}")
    if back.get("status") != "user" or chip.get("import_visible") is not False:
        failures.append(f"scale status {back.get('status')} chip visible {chip.get('import_visible')}")

    # 4. The plan as reviewed: underlay under the 2D geometry.
    factor = PAGE_WIDTH_MM / width
    data = json.loads(plan.read_text(encoding="utf-8"))
    xs = [p[0] * factor for wall in data["walls"] for p in (wall["start"], wall["end"])]
    ys = [p[1] * factor for wall in data["walls"] for p in (wall["start"], wall["end"])]
    zoom = [min(xs), min(ys), max(xs), max(ys)]
    review_shot = capture(sock, zoom, [])
    Path(f"/tmp/forsk-smoke-import-{source}-1000.png").write_bytes(review_shot)
    review_rows = underlay_rows(review_shot, capture(sock, zoom, ["X-PLAN"]))

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
    # The receipt said what the bake would make: a floor, a roof and a wall
    # solid per outline (a wall run standing free is one), less the blocks too
    # wide to be a wall, and every opening cut but the uncut ones.
    outlines = int(imported.get("outlines") or 0)
    skipped = int(imported.get("blocks_skipped") or 0)
    uncut = int(imported.get("uncut") or 0)
    lines.append(
        f"bake floor {floor.get('count')} walls {baked.get('count')} roof {roof.get('count')} for {outlines} "
        f"outline{'' if outlines == 1 else 's'} ({imported.get('gaps_closed')} gaps closed, {imported.get('free_walls')} standing free), "
        f"doors {doors.get('cut_count')}/{imported.get('doors')} windows {windows.get('cut_count')}/{imported.get('windows')} "
        f"failed {failed}, room markers {markers.get('count')}"
    )
    if floor.get("count") != outlines or roof.get("count") != outlines or baked.get("count") != outlines - skipped:
        failures.append(
            f"bake floor {floor.get('count')} roof {roof.get('count')} expected {outlines}, "
            f"walls {baked.get('count')} expected {outlines - skipped}")
    if cut != openings - uncut or failed != uncut:
        failures.append(f"bake cut {cut} expected {openings - uncut}, failed {failed} expected {uncut}")
    if markers.get("count") != imported.get("rooms"):
        failures.append(f"bake room markers {markers.get('count')} expected {imported.get('rooms')}")

    # 6. Rooms: the imported outlines win, detection fills the rest. The scale is refused once 3D exists.
    # Every imported room is still a room, walls closed round it or not, and
    # has a label. kept counts the regions the walls close that an imported
    # outline already stands for: detection found them and added nothing.
    rooms = send_command(sock, "rooms_detect", {})
    refused = send_raw(sock, "plan_scale", {**ends, "length_mm": PAGE_WIDTH_MM})
    had = int(imported.get("rooms") or 0)
    survive = int(rooms.get("count") or 0) - int(rooms.get("detected") or 0)
    labels = len(by_kind(layer_objects(sock, "label"), "label"))
    lines.append(
        f"rooms {survive} of {had} imported are rooms after the bake, {labels} labelled "
        f"({imported.get('unlabelled')} as Rom), walls close round {rooms.get('kept')}, "
        f"{rooms.get('detected')} detected on top, open {len(rooms.get('open') or [])}, "
        f"scale after bake {refused.get('status')}: " + clip(rooms.get("message"), 40)
    )
    if survive != had:
        failures.append(f"rooms {survive} of {had} imported rooms survive the bake")
    if labels != had:
        failures.append(f"rooms {labels} labels for {had} rooms")
    # A room the walls stay open around is named in the receipt's review, so the user knows which to close.
    if int(imported.get("outside") or 0) > 0 and not any("The walls do not close around" in row for row in review):
        failures.append(f"rooms the walls are open around {imported.get('outside')} and the review does not name them")
    if refused.get("status") != "error" or "clear_generated" not in str(refused.get("message")):
        failures.append("plan_scale ran on a generated model")

    # 7. The baked view, slabs off. In both views the plan image is drawn: turn X-PLAN off and rows change.
    baked_shot = capture(sock, zoom, SLAB_LAYERS)
    Path(f"/tmp/forsk-smoke-import-{source}-baked-1000.png").write_bytes(baked_shot)
    baked_rows = underlay_rows(baked_shot, capture(sock, zoom, SLAB_LAYERS + ["X-PLAN"]))
    lines.append(
        f"views plan image drawn: {review_rows[0]} of {review_rows[1]} rows change with X-PLAN off before the bake, "
        f"{baked_rows[0]} of {baked_rows[1]} after; baked view has the slabs off, so no isocurve reads as a wall"
    )
    if review_rows[0] == 0 or baked_rows[0] == 0:
        failures.append(f"the plan image is not drawn: rows changed {review_rows[0]} before the bake, {baked_rows[0]} after")


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
    except (SmokeError, OSError, KeyError, ValueError, zlib.error) as exc:
        failures.append(clip(exc, 200))
    print(f"import {source}: " + ("PASS" if not failures else "FAIL " + "; ".join(failures)))
    for line in lines:
        print("  " + line)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
