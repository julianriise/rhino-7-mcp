"""Daylight (sky-vis proxy) from tagged walls, openings, and rooms, painted on A-ANALYSE."""

import time
from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

import forsk_daylight


@mcp.tool()
def daylight_from_model(
    ctx: Context,
    target: str = "floor",
    cell_size: float = forsk_daylight.DEFAULT_CELL_MM,
) -> Dict[str, Any]:
    """
    Score daylight on the floor as a sky-visibility proxy and paint it in the viewport.

    Reads Forsk-tagged walls (forsk:id, forsk:path, forsk:thickness), opening
    markers (forsk:host_id, forsk:width, window or door), and room markers from
    rooms_from_layer. Runs the vendored Planwire tracer in this process and
    paints one welded, smoothly coloured mesh on A-ANALYSE, 50 mm above the
    floor (deep blue = low, near-white = high, Planwire ramp), with no mesh
    wires. A rerun replaces it. It never prints; daylight_clear removes it.

    Scope: relative 0–1 sky visibility on a 2D grid. Not illuminance (lux),
    not EN 17037 or TEK17, not a code check. No orientation or climate.
    Always tell the user the one-line disclaimer this tool returns.

    Parameters:
    - target: "floor" (every room) or "selection" (the selected room marker
      as one space). An empty selection is refused.
    - cell_size: grid cell in mm (default 400)

    Returns:
    Dictionary with spaces, windows, cells, score_max, score_mean, scope,
    disclaimer, id (mesh), deleted (old overlays), and message.
    """
    try:
        rhino = get_rhino_connection()
        scene = rhino.send_command("daylight_scene", {})
        started = time.perf_counter()
        run = forsk_daylight.run_scene(scene, target=target, cell_mm=cell_size)
        compute_ms = round((time.perf_counter() - started) * 1000)
        painted = rhino.send_command("daylight_paint", run.paint_params())
        summary = run.summary()
        return {
            "success": True,
            **summary,
            "compute_ms": compute_ms,
            "id": painted.get("id"),
            "vertices": painted.get("vertices", 0),
            "wires": painted.get("wires"),
            "deleted": painted.get("deleted", 0),
            "layer": painted.get("layer", "A-ANALYSE"),
            "warnings": list(scene.get("warnings") or []) + summary["notes"],
            "message": (
                f"Daylight on {run.spaces} space(s): {run.windows} window(s), "
                f"{run.cells} cells on A-ANALYSE. {forsk_daylight.DISCLAIMER}"
            ),
        }
    except forsk_daylight.DaylightTargetError as e:
        return {"success": False, "message": str(e), "disclaimer": forsk_daylight.DISCLAIMER}
    except Exception as e:
        logger.error(f"Error in daylight_from_model: {str(e)}")
        return {"success": False, "message": str(e)}
