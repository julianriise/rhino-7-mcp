"""Estimated daylight factor from tagged walls, openings, and rooms, painted on A-ANALYSE."""

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
    Estimate the daylight factor on the floor and paint it in the viewport.

    Reads Forsk-tagged walls (forsk:id, forsk:path, forsk:thickness, height),
    opening markers (forsk:host_id, forsk:width, forsk:sill, forsk:head, window
    or door), the roof overhang, and room markers from rooms_detect or
    rooms_from_layer. For each cell on an 850 mm work plane it adds the sky seen
    in 3D through the facade windows under a CIE overcast sky (walls, sills,
    heads, and the overhang block it) to the room's BRE split-flux reflected
    light, times glass transmittance 0.7. Paints one mesh on A-ANALYSE, 50 mm
    above the floor, one flat colour per cell on one DF scale for every room
    (dark blue 0 %, near white 5 % and up, Forsk sky ramp), with no mesh wires.
    A rerun replaces it. It never prints; daylight_clear removes it.

    Scope: an estimated daylight factor (CIE overcast), not a simulation. Not
    lux, not EN 17037 or TEK17, not a code check. No orientation or climate.
    Always tell the user the one-line disclaimer this tool returns.

    Parameters:
    - target: "floor" (every room) or "selection" (the selected room marker
      as one space). An empty selection is refused.
    - cell_size: grid cell in mm (default 400)

    Returns:
    Dictionary with spaces, windows, cells, df_max and df_mean (%), scope,
    disclaimer, id (mesh), deleted (old overlays), and message.
    """
    try:
        rhino = get_rhino_connection()
        scene = rhino.send_command("daylight_scene", {})
        result = forsk_daylight.evaluate(scene, target=target, cell_mm=cell_size)
        if not result["success"]:
            return result
        painted = rhino.send_command("daylight_paint", result.pop("paint"))
        return {
            **result,
            "id": painted.get("id"),
            "vertices": painted.get("vertices", 0),
            "wires": painted.get("wires"),
            "deleted": painted.get("deleted", 0),
            "layer": painted.get("layer", "A-ANALYSE"),
        }
    except Exception as e:
        logger.error(f"Error in daylight_from_model: {str(e)}")
        return {"success": False, "message": str(e)}
