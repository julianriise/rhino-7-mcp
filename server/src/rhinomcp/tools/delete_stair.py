"""Delete a stair."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def delete_stair(ctx: Context, id: Optional[str] = None) -> Dict[str, Any]:
    """
    Delete a stair's solid and record ("delete the stair", "fjern trappa").

    Parameters:
    - id: the stair's GUID or forsk:id (S01). Omit to remove every picked stair, else the only one.

    Returns deleted (GUIDs), count, message.
    """
    try:
        params: Dict[str, Any] = {"id": id} if id else {}
        result = get_rhino_connection().send_command("delete_stair", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in delete_stair: {str(e)}")
        return {"success": False, "message": str(e)}
