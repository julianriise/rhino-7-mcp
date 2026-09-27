"""Daylight (F4) smoke step, shared by the garage and office smokes.

Runs the same path as the daylight_from_model MCP tool: daylight_scene, the
vendored tracer in this process, daylight_paint. Then it captures the Top
view zoomed to the mesh, clears it, and checks nothing is left for the
Print checks. Prints indented `daylight …` lines only, no `==>` header, so
the plan section the compare reads stays one section.
"""

from __future__ import annotations

import base64
import sys
import time
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "server" / "src"))

import forsk_daylight  # noqa: E402

Send = Callable[[str, dict], dict]
CAPTURE_MARGIN = 0.05


def analysis_objects(send: Send) -> list[dict]:
    found = send("get_objects", {
        "layer_filter": "A-ANALYSE",
        "limit": 50,
        "include_geometry": False,
        "include_attributes": True,
    })
    return [
        obj for obj in found.get("objects") or []
        if (obj.get("attributes") or {}).get("forsk:kind") == "analysis"
    ]


def by_role(rows: list[dict], role: str) -> list[dict]:
    return [obj for obj in rows if (obj.get("attributes") or {}).get("forsk:role") == role]


def paint(send: Send):
    scene = send("daylight_scene", {})
    started = time.perf_counter()
    run = forsk_daylight.run_scene(scene)
    ms = round((time.perf_counter() - started) * 1000)
    params = run.paint_params()
    return run, params, send("daylight_paint", params), ms


def capture(send: Send, png: str, bbox: list) -> str:
    x0, y0, x1, y1 = (float(v) for v in bbox)
    mx, my = (x1 - x0) * CAPTURE_MARGIN, (y1 - y0) * CAPTURE_MARGIN
    shot = send("capture_viewport", {
        "viewport": "top",
        "width": 1000,
        "height": 1000,
        "zoom_bbox": [x0 - mx, y0 - my, x1 + mx, y1 + my],
        "show_grid": False,
        "show_axes": False,
    })
    Path(png).write_bytes(base64.b64decode(shot.get("image_data") or ""))
    return f"{shot.get('width')}x{shot.get('height')}"


def run_step(send: Send, png: str, spaces: int, failures: list, rerun: bool = False) -> None:
    try:
        run, params, painted, ms = paint(send)
        print(f"    daylight spaces {run.spaces} windows {run.windows} cells {run.cells} in {ms} ms")
        print(f"    {forsk_daylight.DISCLAIMER}")
        rows = analysis_objects(send)
        meshes = by_role(rows, "mesh")
        welded = "welded" if painted.get("welded") else "unwelded"
        print(
            f"    daylight mesh {painted.get('layer')} {welded} {painted.get('vertices')} vertices "
            f"wires {painted.get('wires')} overlays {len(meshes)} painted {painted.get('cells')} "
            f"scope {forsk_daylight.SCOPE}"
        )
        if len(rows) != len(meshes):
            failures.append(f"daylight overlay objects {len(rows)} expected {len(meshes)} mesh only")
        if run.spaces != spaces:
            failures.append(f"daylight spaces {run.spaces} expected {spaces}")
        if run.windows < 1 or run.cells < 1:
            failures.append(f"daylight windows {run.windows} cells {run.cells}")
        if (
            len(meshes) != 1
            or painted.get("cells") != run.cells
            or not painted.get("welded")
            or painted.get("vertices") != len(params["vertices"])
            or painted.get("wires") != "off"
        ):
            failures.append(
                f"daylight mesh overlays {len(meshes)} painted {painted.get('cells')} "
                f"{welded} {painted.get('vertices')} wires {painted.get('wires')}"
            )
        if rerun:
            again, _params, repainted, _ms = paint(send)
            rows = analysis_objects(send)
            print(f"    daylight rerun overlays {len(rows)} deleted {repainted.get('deleted')} cells {again.cells}")
            if len(rows) != 1 or repainted.get("deleted") != 1 or again.cells != run.cells:
                failures.append(f"daylight rerun overlays {len(rows)} deleted {repainted.get('deleted')}")
        print(f"    daylight capture {capture(send, png, painted.get('bbox'))} {png}")
    except Exception as exc:  # The Print checks below must still run.
        failures.append(f"daylight {exc}")
    finally:
        try:
            cleared = send("daylight_clear", {})
            left = len(analysis_objects(send))
            print(f"    daylight clear remaining {cleared.get('remaining')} overlays {left}")
            if cleared.get("remaining") != 0 or left != 0:
                failures.append(f"daylight clear left {left}")
        except Exception as exc:
            failures.append(f"daylight clear {exc}")
