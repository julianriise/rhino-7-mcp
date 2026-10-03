"""Room areas by floor and use, read only."""

from typing import Any, Dict

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def area_stats(ctx: Context) -> Dict[str, Any]:
    """
    The tagged rooms as the room list lists them, by floor and by use, plus
    BRA and BTA where the wall records give them. Does not change the model.

    Returns:
    Dictionary with summary, message, floors, uses, rooms, more and omitted,
    as the plugin returns them.
    """
    try:
        result = get_rhino_connection().send_command("area_stats", {})
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in area_stats: {str(e)}")
        return {"success": False, "message": str(e)}
