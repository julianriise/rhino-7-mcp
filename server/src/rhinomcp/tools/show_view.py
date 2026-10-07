"""The view picker: Perspective, Plan or an elevation in the active viewport."""

from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("perspective", "plan", "north", "east", "south", "west")


@mcp.tool()
def show_view(ctx: Context, view: str) -> Dict[str, Any]:
    """
    Show a view in the active viewport ("show the south elevation", "plan
    view", "vis perspektiv"): perspective, plan, north, east, south or west.
    Elevations look as the sheets do (north looks south at the north facade).
    Zooms to the selection, else the building; plans and elevations take the
    line look and the plan the 1.2 m cut. The viewport layout never changes.

    Returns view, label, viewport, display, zoom, message.
    """
    try:
        key = (view or "").strip().lower()
        if key not in _VIEWS:
            return {"success": False, "message": "Unknown view. Use perspective, plan, north, east, south or west."}
        result = get_rhino_connection().send_command("show_view", {"view": key})
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in show_view: {str(e)}")
        return {"success": False, "message": str(e)}
