"""Turn perspective colour-by-room-type on or off."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def rooms_colors(ctx: Context, on: Optional[bool] = None) -> Dict[str, Any]:
    """
    Colour room floors by type in the perspective view only.

    On by default. Off returns that view to flat white with black edges.
    Top and elevation views stay line drawings either way.

    Omit on to read the toggle. Pass true or false to set it.
    """
    try:
        params: Dict[str, Any] = {}
        if on is not None:
            if not isinstance(on, bool):
                return {"success": False, "message": "on must be true or false."}
            params["on"] = on
        result = get_rhino_connection().send_command("rooms_colors", params)
        return {
            "success": True,
            "on": result.get("on", True),
            "message": result.get("message", "Room colours"),
        }
    except Exception as e:
        logger.error(f"Error in rooms_colors: {str(e)}")
        return {"success": False, "message": str(e)}
