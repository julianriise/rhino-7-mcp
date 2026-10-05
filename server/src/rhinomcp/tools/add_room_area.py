"""Draw a room's area by its corners, or redraw a room the same way."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def add_room_area(
    ctx: Context,
    points: List[List[float]],
    replace: bool = False,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Draw a room's area by its corners. AI detection misses rooms, so the user
    gives the wall corners around the room in order. They become one closed
    outline on A-ROOM at the floor's height; rooms are detected again, the
    new room is named from the labels inside and gets a plate.

    With replace, the room given by id (or the one selected) is replaced by
    the new outline and keeps a type the user set.

    Refused, with nothing changed: under three corners, under 1 m², or an
    outline that crosses itself.

    Parameters:
    - points: [[x, y], ...] corners in mm, at least three, in order.
    - replace: Replace a room with this outline. Default false.
    - id: With replace, the room GUID. Omit to use the one selected room.

    Returns:
    Dictionary with id, name, area_m2, corners, replaced, rooms, ok, message.
    """
    try:
        if not isinstance(points, list) or len(points) < 3:
            return {"success": False, "message": "An area needs three corners or more."}
        for p in points:
            if not isinstance(p, (list, tuple)) or len(p) != 2:
                return {"success": False, "message": "Each corner is [x, y] in mm."}

        params: Dict[str, Any] = {"points": [[float(p[0]), float(p[1])] for p in points]}
        if replace:
            params["replace"] = True
            if id:
                params["id"] = id

        result = get_rhino_connection().send_command("add_room_area", params)
        return {
            "success": True,
            "id": result.get("id"),
            "name": result.get("name"),
            "area_m2": result.get("area_m2"),
            "corners": result.get("corners"),
            "replaced": result.get("replaced"),
            "rooms": result.get("rooms"),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in add_room_area: {str(e)}")
        return {"success": False, "message": str(e)}
