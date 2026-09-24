"""Rebuild one host wall from its param record."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def rebuild_host_wall(
    ctx: Context,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Re-extrude one Forsk wall from forsk:path and recut that wall's openings.

    Openings are placed from each marker's world position. Cuts stay in
    memory until every one succeeds, then the host is replaced once. A
    failure leaves the document unchanged. The wall GUID stays when the
    result is one solid. Floor, roof, rooms, other walls, drawings, and
    layouts stay. This does not clear the model.

    Id may be a wall, an opening marker, or an opening frame. Omit it to
    use the current selection (exactly one). A wall with no forsk:path
    refuses until walls_from_layer bakes it again. An X-EXIST host refuses.

    Parameters:
    - id: Optional wall, marker, or frame GUID

    Returns:
    Dictionary with host_id, forsk_id, height, thickness, opening_count, ok.
    """
    try:
        rhino = get_rhino_connection()
        params: Dict[str, Any] = {}
        if id is not None and id != "":
            params["id"] = id

        result = rhino.send_command("rebuild_host_wall", params)
        return {
            "success": True,
            "host_id": result.get("host_id"),
            "forsk_id": result.get("forsk_id"),
            "level": result.get("level"),
            "height": result.get("height"),
            "thickness": result.get("thickness"),
            "path_points": result.get("path_points"),
            "opening_count": result.get("opening_count", 0),
            "marker_ids": result.get("marker_ids", []),
            "block_ids": result.get("block_ids", []),
            "warnings": result.get("warnings", []),
            "solid_volume": result.get("solid_volume"),
            "ok": result.get("ok", True),
            "message": result.get("message", "Host wall rebuilt"),
        }
    except Exception as e:
        logger.error(f"Error in rebuild_host_wall: {str(e)}")
        return {"success": False, "message": str(e)}
