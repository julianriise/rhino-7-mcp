"""Delete one straight wall run and the openings in it, then rebuild that host wall."""

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
def delete_wall(
    ctx: Context,
    side: Optional[str] = None,
    at: Optional[List[float]] = None,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Delete one straight wall run ("delete the north wall", "remove the
    partition between the rooms") and the openings in it, then rebuild that
    host wall from the path left. A baked plan is one wall record, so the run
    is picked inside it: side for an outer wall, or at for the face nearest a
    point. Taking a partition out joins the two rooms; taking an outer wall
    out opens the ring. A wall standing on its own is deleted whole.

    Refused, with nothing changed: a delete that would leave a record in
    two separate pieces (not in this version), and one that would leave
    another opening past the end of its wall. The floor slab and flat roof
    from that record are rebuilt, and rooms are detected again. A shown
    daylight map is hidden as out of date. Refuses X-EXIST.

    Parameters:
    - side: north, south, east or west: the outer wall facing that way. Exclusive with at.
    - at: [x, y] in mm on or beside the wall, within 1000 mm. Exclusive with side.
    - id: Wall GUID. Omit to use the selected wall, else the only wall.

    Returns:
    Dictionary with host_id, record_deleted, openings_deleted, holes,
    host_openings, host_voids, ok, message.
    """
    try:
        if (side is None) == (at is None):
            return {"success": False, "message": "Give side or at, not both."}
        if side is not None and side not in _COMPASS:
            return {"success": False, "message": "side is north, south, east or west."}
        if at is not None and not _is_pair(at):
            return {"success": False, "message": "at is [x, y] in mm."}

        params: Dict[str, Any] = {}
        if side is not None:
            params["side"] = side
        if at is not None:
            params["at"] = at
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("delete_wall", params)
        return {
            "success": True,
            "host_id": result.get("host_id"),
            "forsk_id": result.get("forsk_id"),
            "wall": result.get("wall"),
            "record_deleted": result.get("record_deleted", False),
            "openings_deleted": result.get("openings_deleted", []),
            "holes": result.get("holes"),
            "followed": result.get("followed", []),
            "records": result.get("records", []),
            "rebuilt": result.get("rebuilt", ""),
            "host_openings": result.get("host_openings"),
            "host_voids": result.get("host_voids"),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in delete_wall: {str(e)}")
        return {"success": False, "message": str(e)}
