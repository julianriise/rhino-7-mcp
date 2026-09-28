"""Rooms from the model: the regions between the Forsk walls, on A-ROOM."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


@mcp.tool()
def rooms_detect(ctx: Context) -> Dict[str, Any]:
    """
    Make rooms / find rooms from the walls. The same run as the Forsk panel's
    Make rooms chip.

    Rooms are the closed regions between the Forsk walls, closed where a door
    sits in a gap between two wall ends and split by curves on space_divider.
    Each becomes a closed curve on A-ROOM with a forsk:room_id (rd-01, …) that
    survives re-runs. Closed curves already drawn on A-ROOM win; detection only
    fills what they leave. Regions under 1 m² are skipped. A gap with no door
    leaves its region open, reported with the reason. Room markers are then
    rebuilt from every closed A-ROOM curve, as rooms_from_layer does.

    Returns:
    Dictionary with ids (markers), rooms (id, name, area_m2, x, y), count,
    detected, area_m2, open (reason, x, y), warnings, and message.
    """
    try:
        result = get_rhino_connection().send_command("rooms_detect", {})
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in rooms_detect: {str(e)}")
        return {"success": False, "message": str(e)}
