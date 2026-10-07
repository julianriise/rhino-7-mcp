"""Delete furniture."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def delete_furniture(
    ctx: Context,
    id: Optional[str] = None,
    room: Optional[str] = None,
    all: Optional[bool] = None,
) -> Dict[str, Any]:
    """
    Delete furniture ("remove the bed", "clear the furniture in the
    kitchen", "fjern alle møblene").

    Parameters:
    - id: the piece's GUID or forsk:id (F01).
    - room: every piece in this room (id, name or type).
    - all: every piece in the file.
    Nothing given: the picked pieces, else the only one.

    Returns deleted (GUIDs), count, message.
    """
    try:
        params: Dict[str, Any] = {}
        if id:
            params["id"] = id
        if room:
            params["room"] = room
        if all:
            params["all"] = True
        result = get_rhino_connection().send_command("delete_furniture", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in delete_furniture: {str(e)}")
        return {"success": False, "message": str(e)}
