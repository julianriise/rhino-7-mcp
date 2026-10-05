"""Push or pull one side of a room: the wall run on that side moves (F2)."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from rhinomcp.args import COMPASS

_WAYS = ("out", "in")


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def room_push_pull(
    ctx: Context,
    side: str,
    distance_mm: float,
    way: str = "out",
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Push or pull one side of a room ("push the north side of this room 500 mm
    out"). The side is the wall run whose face is that side of the room; it
    moves out (the room grows) or in, as move_wall moves it: the walls joined
    to it stretch or shorten along their own lines, in every wall record that
    touches, and the openings on it move with it. The floor slab and flat roof
    under those walls are rebuilt, and rooms are detected again. A shown
    daylight map is hidden as out of date.

    Refused as move_wall refuses, with nothing changed. Refuses X-EXIST.

    Parameters:
    - side: north, south, east or west: the room's side that moves.
    - distance_mm: How far, mm, above 0.
    - way: out (the room grows) or in. Default out.
    - id: Room GUID. Omit to use the one selected room.

    Returns:
    Dictionary with room, side, way, toward, distance_mm, openings_moved,
    followed (the walls that followed), records, rebuilt, ok, message.
    """
    try:
        if side not in COMPASS:
            return {"success": False, "message": "side is north, south, east or west."}
        if way not in _WAYS:
            return {"success": False, "message": "way is out or in."}
        if isinstance(distance_mm, bool) or not isinstance(distance_mm, (int, float)) or distance_mm <= 0:
            return {"success": False, "message": "distance_mm must be positive."}

        params: Dict[str, Any] = {"side": side, "distance_mm": distance_mm, "way": way}
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("room_push_pull", params)
        return {
            "success": True,
            "room": result.get("room"),
            "room_id": result.get("room_id"),
            "side": result.get("side"),
            "way": result.get("way"),
            "toward": result.get("toward"),
            "distance_mm": result.get("distance_mm"),
            "openings_moved": result.get("openings_moved", []),
            "followed": result.get("followed", []),
            "records": result.get("records", []),
            "rebuilt": result.get("rebuilt", ""),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in room_push_pull: {str(e)}")
        return {"success": False, "message": str(e)}
