"""Move a facade opening along its host wall segment."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def move_opening(
    ctx: Context,
    id: Optional[str] = None,
    delta_mm: Optional[float] = None,
    t: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Slide an opening along the same host facade segment.

    Provide delta_mm or absolute t, not both. Marker GUID is preserved.

    Parameters:
    - id: Optional opening marker GUID. Omit to use the current selection
    - delta_mm: Signed distance along the segment (mm)
    - t: Absolute 0–1 along the segment

    Returns:
    Dictionary with marker_id, host_id, t, ok, message.
    """
    try:
        if delta_mm is not None and t is not None:
            return {"success": False, "message": "Specify delta_mm or t, not both."}
        if delta_mm is None and t is None:
            return {"success": False, "message": "Specify delta_mm or t."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {}
        if id is not None and id != "":
            params["id"] = id
        if delta_mm is not None:
            params["delta_mm"] = delta_mm
        if t is not None:
            params["t"] = t

        result = rhino.send_command("move_opening", params)
        return {
            "success": True,
            "marker_id": result.get("marker_id"),
            "host_id": result.get("host_id"),
            "t": result.get("t"),
            "ok": result.get("ok", True),
            "message": result.get("message", "Opening moved"),
        }
    except Exception as e:
        logger.error(f"Error in move_opening: {str(e)}")
        return {"success": False, "message": str(e)}
