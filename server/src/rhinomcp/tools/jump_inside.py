"""Jump inside a room: a first interior shot in the Perspective view."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def jump_inside(ctx: Context, room: Optional[str] = None, direction: Optional[str] = None) -> Dict[str, Any]:
    """
    Jump inside a room ("jump inside the bedroom", "show me the kitchen
    from inside", "gå inn i stua"): a level, straight-on shot like interior
    photography. The Perspective view stands just inside the wall behind it
    and looks across the room the chosen way at 1.2 m with a 24 mm lens.
    Saved as a named view called after the room.

    Parameters:
    - room: id, name or type. Omit for the picked room, else the only one.
    - direction: north, east, south or west. Default north.

    Returns view, room, eye, target, lens_mm, direction, message.
    """
    try:
        if direction is not None and direction not in ("north", "east", "south", "west"):
            return {"success": False, "message": "direction is north, east, south or west."}
        params: Dict[str, Any] = {"room": room} if room else {}
        if direction:
            params["direction"] = direction
        result = get_rhino_connection().send_command("jump_inside", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in jump_inside: {str(e)}")
        return {"success": False, "message": str(e)}
