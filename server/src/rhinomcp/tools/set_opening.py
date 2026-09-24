"""Set width, sill, or head on one opening and rebuild its host."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def set_opening(
    ctx: Context,
    id: Optional[str] = None,
    width: Optional[float] = None,
    sill: Optional[float] = None,
    head: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Set width, sill, and/or head on one opening, then rebuild that host wall.

    Pass only the fields to change. The marker stays. The frame is rebuilt
    with the host. This does not clear the model. Id may be the marker or
    the opening frame. Omit it to use the current selection. Do not guess
    the last opening created. Refuses forsk:kind=existing or layer X-EXIST.
    A refused rebuild leaves the document unchanged.

    Parameters:
    - id: Optional marker or opening-frame GUID
    - width: Opening width in mm
    - sill: Bottom Z in mm
    - head: Top Z in mm

    Returns:
    Dictionary with marker_id, host_id, width, sill, head, t, ok, message.
    """
    try:
        if width is None and sill is None and head is None:
            return {"success": False, "message": "Specify width, sill, or head."}
        if width is not None and width <= 0:
            return {"success": False, "message": "width must be positive."}
        if sill is not None and head is not None and head <= sill:
            return {"success": False, "message": "head must be greater than sill."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {}
        if id is not None and id != "":
            params["id"] = id
        if width is not None:
            params["width"] = width
        if sill is not None:
            params["sill"] = sill
        if head is not None:
            params["head"] = head

        result = rhino.send_command("set_opening", params)
        return {
            "success": True,
            "marker_id": result.get("marker_id"),
            "host_id": result.get("host_id"),
            "width": result.get("width"),
            "sill": result.get("sill"),
            "head": result.get("head"),
            "t": result.get("t"),
            "ok": result.get("ok", True),
            "message": result.get("message", "Opening size set"),
            "block_id": result.get("block_id"),
        }
    except Exception as e:
        logger.error(f"Error in set_opening: {str(e)}")
        return {"success": False, "message": str(e)}
