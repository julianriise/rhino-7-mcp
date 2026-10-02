"""Move one straight wall run across itself, then rebuild that host wall."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger

_COMPASS = ("north", "south", "east", "west")


def _is_pair(value: Any) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in value)
    )


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def move_wall(
    ctx: Context,
    toward: str,
    distance_mm: float,
    side: Optional[str] = None,
    at: Optional[List[float]] = None,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Move one straight wall run across itself ("move the north wall 500 mm
    north"), then rebuild that host wall from its path. A baked plan is one
    wall record, so the run is picked inside it: side for an outer wall, or
    at for the face nearest a point. Both faces move; the walls that meet the
    run stretch to follow along their own lines, in every wall record that
    touches it, and the openings on the run move with it. The floor
    slab and flat roof from that record are rebuilt, and rooms are detected
    again. A shown daylight map is hidden as out of date.

    A move that would close a room or cross a wall is refused with the depth
    it ran into. A refused rebuild leaves the document unchanged. Does not
    clear the model. Refuses X-EXIST.

    Parameters:
    - toward: north, south, east or west. An east–west wall moves north or south.
    - distance_mm: How far, mm, above 0.
    - side: north, south, east or west: the outer wall facing that way. Exclusive with at.
    - at: [x, y] in mm on or beside the wall, within 1000 mm. Exclusive with side.
    - id: Wall GUID. Omit to use the selected wall, else the only wall.

    Returns:
    Dictionary with host_id, wall, toward, distance_mm, faces_before,
    faces_after, openings_moved, host_openings, host_voids, ok, message.
    """
    try:
        if toward not in _COMPASS:
            return {"success": False, "message": "toward is north, south, east or west."}
        if isinstance(distance_mm, bool) or not isinstance(distance_mm, (int, float)) or distance_mm <= 0:
            return {"success": False, "message": "distance_mm must be positive."}
        if (side is None) == (at is None):
            return {"success": False, "message": "Give side or at, not both."}
        if side is not None and side not in _COMPASS:
            return {"success": False, "message": "side is north, south, east or west."}
        if at is not None and not _is_pair(at):
            return {"success": False, "message": "at is [x, y] in mm."}

        params: Dict[str, Any] = {"toward": toward, "distance_mm": distance_mm}
        if side is not None:
            params["side"] = side
        if at is not None:
            params["at"] = at
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("move_wall", params)
        return {
            "success": True,
            "host_id": result.get("host_id"),
            "forsk_id": result.get("forsk_id"),
            "wall": result.get("wall"),
            "toward": result.get("toward"),
            "distance_mm": result.get("distance_mm"),
            "faces_before": result.get("faces_before"),
            "faces_after": result.get("faces_after"),
            "openings_moved": result.get("openings_moved", []),
            "host_openings": result.get("host_openings"),
            "host_voids": result.get("host_voids"),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in move_wall: {str(e)}")
        return {"success": False, "message": str(e)}
