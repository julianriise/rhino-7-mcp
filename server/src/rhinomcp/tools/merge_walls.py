"""Merge walls on one line, and clean up walls that go into each other."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def merge_walls(
    ctx: Context,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Merge walls ("merge these walls", "clean up the walls"). Walls end to end
    on one line, with the same thickness, height and level, become one wall.
    Walls that go into each other are cut apart where they meet, so each keeps
    its own run and id; when that does not work they become one wall. What the
    walls cover stays the same, so rooms, the floor and the roof stay as they
    are. Openings move to the wall that holds them. One undo.

    Draw wall and every wall edit merge on their own; use this for walls made
    before. Refuses X-EXIST.

    Parameters:
    - id: Wall GUID: merge around that wall only. Omit to use the selected
      walls, else every wall.

    Returns:
    Dictionary with merged (forsk_id, took_in), cut, openings_moved, ok, message.
    """
    try:
        params: Dict[str, Any] = {}
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("merge_walls", params)
        return {
            "success": True,
            "merged": result.get("merged", []),
            "cut": result.get("cut", []),
            "openings_moved": result.get("openings_moved", 0),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in merge_walls: {str(e)}")
        return {"success": False, "message": str(e)}
