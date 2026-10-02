"""Split whole wall records into one record per straight run."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def split_walls(
    ctx: Context,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Split each wall record that holds more than one straight run into one
    record per run ("split the walls so I can click one"). Files made before
    this version keep the whole plan's walls as one record, so a click picks
    every wall. The union stays the same, so rooms, the floor and the joined
    wall edits read as before. Each opening goes to the wall that holds it
    and is cut again; the old record is deleted. One undo.

    A record whose split is refused stays whole and comes back in refused
    with the reason: a curved wall, or an opening across two of its walls.
    Refuses X-EXIST.

    Parameters:
    - id: Wall GUID: split that record only. Omit to split every whole record.

    Returns:
    Dictionary with split (forsk_id, into, openings), walls, openings_moved,
    refused (forsk_id, why), ok, message.
    """
    try:
        params: Dict[str, Any] = {}
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("split_walls", params)
        return {
            "success": True,
            "split": result.get("split", []),
            "walls": result.get("walls", 0),
            "openings_moved": result.get("openings_moved", 0),
            "refused": result.get("refused", []),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in split_walls: {str(e)}")
        return {"success": False, "message": str(e)}
