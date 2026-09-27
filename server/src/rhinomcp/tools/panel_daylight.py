"""The Forsk panel's Daylight chip over the bridge, for the smokes."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def panel_daylight(ctx: Context, action: str = "state", text: Optional[str] = None) -> Dict[str, Any]:
    """
    Drive the Forsk panel's Daylight chip the way the button does. Prefer
    daylight_from_model and daylight_clear for daylight itself.

    Parameters:
    - action: "state" (read the chip), "run" (panel daylight on the floor), or
      "clear" (remove the overlay)
    - text: optional chat message; the result then carries the panel intent

    Returns:
    Dictionary with visible, enabled, label (the chip after the action),
    intent when text is given, and ok and line after run or clear.
    """
    try:
        params: Dict[str, Any] = {"action": action}
        if text is not None:
            params["text"] = text
        result = get_rhino_connection().send_command("panel_daylight", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in panel_daylight: {str(e)}")
        return {"success": False, "message": str(e)}
