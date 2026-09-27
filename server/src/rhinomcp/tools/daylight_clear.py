"""Remove the daylight overlay from A-ANALYSE."""

from typing import Any, Dict

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def daylight_clear(ctx: Context) -> Dict[str, Any]:
    """
    Delete the daylight overlay mesh (forsk:kind=analysis) from A-ANALYSE.

    Walls, openings, rooms, and sheets stay. clear_generated also removes it.

    Returns:
    Dictionary with count (deleted), remaining, and message.
    """
    try:
        rhino = get_rhino_connection()
        result = rhino.send_command("daylight_clear", {})
        return {
            "success": True,
            "count": result.get("count", 0),
            "remaining": result.get("remaining", 0),
            "message": result.get("message", "Daylight overlay cleared."),
        }
    except Exception as e:
        logger.error(f"Error in daylight_clear: {str(e)}")
        return {"success": False, "message": str(e)}
