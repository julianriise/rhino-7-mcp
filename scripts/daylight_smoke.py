"""Daylight (F4) smoke step, shared by the garage and office smokes.

Runs the same path as the daylight_from_model MCP tool: daylight_scene, the
vendored tracer in this process, daylight_paint. Then it captures the top
view, clears the overlay, and checks nothing is left for the Print checks.
Prints indented `daylight …` lines only, no `==>` header, so the plan
section the compare reads stays one section.
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


def overlays(send: Send) -> int:
    found = send("get_objects", {
        "layer_filter": "A-ANALYSE",
        "limit": 50,
        "include_geometry": False,
        "include_attributes": True,
    })
    return sum(
        1 for obj in found.get("objects") or []
        if (obj.get("attributes") or {}).get("forsk:kind") == "analysis"
    )


def paint(send: Send) -> tuple[forsk_daylight.DaylightRun, dict, int]:
    scene = send("daylight_scene", {})
    started = time.perf_counter()
    run = forsk_daylight.run_scene(scene)
    ms = round((time.perf_counter() - started) * 1000)
    return run, send("daylight_paint", run.paint_params()), ms


def capture(send: Send, png: str) -> str:
    shot = send("capture_viewport", {
        "viewport": "top",
        "width": 1000,
        "height": 700,
        "zoom_to_fit": True,
        "show_grid": False,
        "show_axes": False,
    })
    Path(png).write_bytes(base64.b64decode(shot.get("image_data") or ""))
    return f"{shot.get('width')}x{shot.get('height')}"


def run_step(send: Send, png: str, spaces: int, failures: list, rerun: bool = False) -> None:
    try:
        run, painted, ms = paint(send)
        print(f"    daylight spaces {run.spaces} windows {run.windows} cells {run.cells} in {ms} ms")
        print(f"    {forsk_daylight.DISCLAIMER}")
        count = overlays(send)
        print(
            f"    daylight mesh {painted.get('layer')} overlays {count} "
            f"painted {painted.get('cells')} scope {forsk_daylight.SCOPE}"
        )
        if run.spaces != spaces:
            failures.append(f"daylight spaces {run.spaces} expected {spaces}")
        if run.windows < 1 or run.cells < 1:
            failures.append(f"daylight windows {run.windows} cells {run.cells}")
        if count != 1 or painted.get("cells") != run.cells:
            failures.append(f"daylight mesh overlays {count} painted {painted.get('cells')}")
        if rerun:
            again, repainted, _ms = paint(send)
            count = overlays(send)
            print(f"    daylight rerun overlays {count} deleted {repainted.get('deleted')} cells {again.cells}")
            if count != 1 or repainted.get("deleted") != 1 or again.cells != run.cells:
                failures.append(f"daylight rerun overlays {count} deleted {repainted.get('deleted')}")
        print(f"    daylight capture {capture(send, png)} {png}")
    except Exception as exc:  # The Print checks below must still run.
        failures.append(f"daylight {exc}")
    finally:
        try:
            cleared = send("daylight_clear", {})
            left = overlays(send)
            print(f"    daylight clear remaining {cleared.get('remaining')} overlays {left}")
            if cleared.get("remaining") != 0 or left != 0:
                failures.append(f"daylight clear left {left}")
        except Exception as exc:
            failures.append(f"daylight clear {exc}")
