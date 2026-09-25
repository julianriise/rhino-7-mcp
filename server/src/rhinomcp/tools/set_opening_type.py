"""Set an opening's type, hand, or swing and rebuild its host."""

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


TYPES = {
    "door.hinged_single",
    "door.hinged_double",
    "door.sliding",
    "door.pocket",
    "window.fixed",
    "window.side_hung",
    "window.top_hung",
}
HANDS = {"L", "R", "flip"}
SWINGS = {"in", "out", "flip"}


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def set_opening_type(
    ctx: Context,
    id: Optional[str] = None,
    type: Optional[str] = None,
    hand: Optional[str] = None,
    swing: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Set type, hand, and/or swing on the selected openings, then rebuild
    each host wall once.

    Pass only the fields to change. Width, sill, head, and position stay.
    Id may be a marker or an opening frame. Omit it to use the current
    selection, including two or more openings. Do not guess the last
    opening created. "make this a sliding door", "top-hung window",
    "flip swing", and "change hand" use this tool.
    Refuses forsk:kind=existing or layer X-EXIST. A refused change leaves
    the document unchanged. The same type does not rebuild.
    clear_generated and a rebake reset types, because the DXF has no type.

    Parameters:
    - id: Optional marker or opening-frame GUID
    - type: door.hinged_single, door.hinged_double, door.sliding,
      door.pocket, window.fixed, window.side_hung, or window.top_hung
    - hand: L, R, or flip
    - swing: in, out, or flip

    Returns:
    Dictionary with marker_id, host_id, opening_type, ok, message.
    """
    try:
        if type is None and hand is None and swing is None:
            return {"success": False, "message": "Specify type, hand, or swing."}
        if type is not None and type not in TYPES:
            return {"success": False, "message": "Unknown opening type."}
        if hand is not None and hand not in HANDS:
            return {"success": False, "message": "hand must be L, R, or flip."}
        if swing is not None and swing not in SWINGS:
            return {"success": False, "message": "swing must be in, out, or flip."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {}
        if id is not None and id != "":
            params["id"] = id
        if type is not None:
            params["type"] = type
        if hand is not None:
            params["hand"] = hand
        if swing is not None:
            params["swing"] = swing

        result = rhino.send_command("set_opening_type", params)
        out: Dict[str, Any] = {
            "success": True,
            "marker_id": result.get("marker_id"),
            "host_id": result.get("host_id"),
            "opening_type": result.get("opening_type"),
            "ok": result.get("ok", True),
            "message": result.get("message", "Opening type set"),
        }
        for key in (
            "hand",
            "swing",
            "block_id",
            "host_openings",
            "host_voids",
            "max_frame_mm",
            "marker_ids",
        ):
            if key in result and result.get(key) is not None:
                out[key] = result.get(key)
        return out
    except Exception as e:
        logger.error(f"Error in set_opening_type: {str(e)}")
        return {"success": False, "message": str(e)}
