"""Exterior render: the Perspective view looks at the building from outside."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def exterior_render(ctx: Context, direction: Optional[str] = None) -> Dict[str, Any]:
    """
    Exterior render ("show the house from the south", "exterior view from
    the north", "render the east facade"): the Perspective view stands
    outside and looks straight at the building from that side, level at eye
    height with a 24 mm lens, far enough back that the facade and the roof
    fit. Materials, soft shadows and the roof shown. Saved as the named view
    Exterior north (and so on).

    Parameters:
    - direction: the side the camera stands on: north, east, south or west. Default north.

    Returns view, eye, target, lens_mm, direction, message.
    """
    try:
        if direction is not None and direction not in ("north", "east", "south", "west"):
            return {"success": False, "message": "direction is north, east, south or west."}
        params: Dict[str, Any] = {"direction": direction} if direction else {}
        result = get_rhino_connection().send_command("exterior_render", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in exterior_render: {str(e)}")
        return {"success": False, "message": str(e)}
