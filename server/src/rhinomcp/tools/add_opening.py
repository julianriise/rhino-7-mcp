"""Add a door or window opening on a vertical Forsk wall."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def add_opening(
    ctx: Context,
    opening_kind: str,
    host_id: Optional[str] = None,
    width: Optional[float] = None,
    sill: Optional[float] = None,
    head: Optional[float] = None,
    t: Optional[float] = None,
    distance_mm: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Cut a door or window into a vertical Forsk wall and add an A-OPEN marker.

    Defaults match plan bake: door 900×0–2100, window 1200×900–2100, t=0.5
    on the longest facade segment.

    Parameters:
    - opening_kind: "door" or "window"
    - host_id: Optional wall GUID. Omit to use the current selection
    - width / sill / head: Optional size overrides
    - t: Optional 0–1 along the facade segment (exclusive with distance_mm)
    - distance_mm: Optional mm from segment start (exclusive with t)

    Returns:
    Dictionary with marker_id, host_id, opening_kind, sizes, t, ok, message.
    """
    try:
        kind = (opening_kind or "").strip().lower()
        if kind not in ("door", "window"):
            return {"success": False, "message": "opening_kind must be door or window."}
        if t is not None and distance_mm is not None:
            return {"success": False, "message": "Specify t or distance_mm, not both."}
        if width is not None and width <= 0:
            return {"success": False, "message": "width must be positive."}
        if sill is not None and head is not None and head <= sill:
            return {"success": False, "message": "head must be greater than sill."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {"opening_kind": kind}
        if host_id is not None and host_id != "":
            params["host_id"] = host_id
        if width is not None:
            params["width"] = width
        if sill is not None:
            params["sill"] = sill
        if head is not None:
            params["head"] = head
        if t is not None:
            params["t"] = t
        if distance_mm is not None:
            params["distance_mm"] = distance_mm

        result = rhino.send_command("add_opening", params)
        return {
            "success": True,
            "marker_id": result.get("marker_id"),
            "host_id": result.get("host_id"),
            "opening_kind": result.get("opening_kind", kind),
            "width": result.get("width"),
            "sill": result.get("sill"),
            "head": result.get("head"),
            "t": result.get("t"),
            "ok": result.get("ok", True),
            "message": result.get("message", "Opening added"),
        }
    except Exception as e:
        logger.error(f"Error in add_opening: {str(e)}")
        return {"success": False, "message": str(e)}
