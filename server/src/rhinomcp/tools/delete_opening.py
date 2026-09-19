"""Delete a facade opening marker and close its hole on the host wall."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def delete_opening(
    ctx: Context,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Close one facade opening on its host wall and delete the A-OPEN marker.

    Parameters:
    - id: Optional opening marker GUID. Omit to use the current selection
      (exactly one forsk:kind=opening_marker).

    Returns:
    Dictionary with deleted_marker_id, host_id, ok, or success=False on refuse.
    """
    try:
        rhino = get_rhino_connection()
        params: Dict[str, Any] = {}
        if id is not None and id != "":
            params["id"] = id

        result = rhino.send_command("delete_opening", params)
        return {
            "success": True,
            "deleted_marker_id": result.get("deleted_marker_id"),
            "host_id": result.get("host_id"),
            "ok": result.get("ok", True),
        }
    except Exception as e:
        logger.error(f"Error in delete_opening: {str(e)}")
        return {"success": False, "message": str(e)}
