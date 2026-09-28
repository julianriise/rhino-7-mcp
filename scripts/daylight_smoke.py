"""Daylight (F4) smoke step, shared by the garage and office smokes.

Runs the same path as the daylight_from_model MCP tool: daylight_scene, the
daylight factor estimate in this process, daylight_paint. Then it captures the Top
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
# The mesh reaches the inner wall faces within this.
GAP_MM = 1.0


def on_ramp(colors: list) -> bool:
    """Every vertex colour is a colour of Forsk's sky display ramp."""
    ramp = {forsk_daylight.display_rgb(i / 100000) for i in range(100001)}
    return all(tuple(c) in ramp for c in colors)


def flat(params: dict) -> bool:
    """Every face is one colour: its cell's, never blended with a neighbour's."""
    colors = params["colors"]
    return all(len({tuple(colors[i]) for i in face}) == 1 for face in params["faces"])


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
        summary = run.summary()
        cells = summary["cells"]
        print(
            f"    daylight spaces {run.spaces} windows {run.windows} cells {cells} in {ms} ms "
            f"df mean {summary['df_mean']:g} max {summary['df_max']:g}"
        )
        print(f"    {forsk_daylight.DISCLAIMER}")
        rows = analysis_objects(send)
        meshes = by_role(rows, "mesh")
        shading = "flat" if flat(params) else "smooth"
        gap = round(run.mesh_gap_mm(params), 1)
        ramp = forsk_daylight.RAMP if on_ramp(params["colors"]) else "off"
        print(
            f"    daylight mesh {painted.get('layer')} {shading} {painted.get('vertices')} vertices "
            f"wires {painted.get('wires')} gap {gap:g} ramp {ramp} overlays {len(meshes)} "
            f"painted {painted.get('faces')} scope {forsk_daylight.SCOPE}"
        )
        if len(rows) != len(meshes):
            failures.append(f"daylight overlay objects {len(rows)} expected {len(meshes)} mesh only")
        if run.spaces != spaces:
            failures.append(f"daylight spaces {run.spaces} expected {spaces}")
        if run.windows < 1 or cells < 1:
            failures.append(f"daylight windows {run.windows} cells {cells}")
        if (
            len(meshes) != 1
            or painted.get("faces") != len(params["faces"])
            or shading != "flat"
            or painted.get("vertices") != len(params["vertices"])
            or painted.get("wires") != "off"
        ):
            failures.append(
                f"daylight mesh overlays {len(meshes)} painted {painted.get('faces')} "
                f"{shading} {painted.get('vertices')} wires {painted.get('wires')}"
            )
        if gap > GAP_MM or ramp != forsk_daylight.RAMP:
            failures.append(f"daylight mesh gap {gap:g} ramp {ramp}")
        if rerun:
            again, _params, repainted, _ms = paint(send)
            rows = analysis_objects(send)
            print(f"    daylight rerun overlays {len(rows)} deleted {repainted.get('deleted')} cells {len(again.cells)}")
            if len(rows) != 1 or repainted.get("deleted") != 1 or len(again.cells) != cells:
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
