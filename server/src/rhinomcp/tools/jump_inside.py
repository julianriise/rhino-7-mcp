"""Jump inside a room: a first interior shot in the Perspective view."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def jump_inside(ctx: Context, room: Optional[str] = None) -> Dict[str, Any]:
    """
    Jump inside a room ("jump inside the bedroom", "show me the kitchen
    from inside", "gå inn i stua"): the Perspective view stands just inside
    the room's door at 1.2 m, looking across the room with a 24 mm lens, a
    first interior shot to adjust before a render. Saved as the named view
    "Interior: <room>".

    Parameters:
    - room: id, name or type. Omit for the picked room, else the only one.

    Returns view, room, eye, target, lens_mm, message.
    """
    try:
        params: Dict[str, Any] = {"room": room} if room else {}
        result = get_rhino_connection().send_command("jump_inside", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in jump_inside: {str(e)}")
        return {"success": False, "message": str(e)}
