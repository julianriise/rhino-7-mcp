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
    Remove one or more facade openings and rebuild each host wall from its
    path. Deletes the marker, the frame, and the record. Does not add a
    filler plate. Id may be a marker or an opening block. Omit id to use
    the current selection, including two or more openings.
    Refuses a marker or host with forsk:kind=existing or on layer X-EXIST.
    If the rebuild fails, the openings are restored and the wall stays.

    Parameters:
    - id: Optional marker or opening-block GUID. Omit to remove every
      selected opening.

    Returns:
    Dictionary with deleted_marker_id, host_id, message, ok.
    message is the status line, such as "Removed 2 windows from w01".
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
            "message": result.get("message"),
        }
    except Exception as e:
        logger.error(f"Error in delete_opening: {str(e)}")
        return {"success": False, "message": str(e)}
