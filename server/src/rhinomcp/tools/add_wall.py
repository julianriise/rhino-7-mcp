"""Add one straight wall on a centreline: joined to the wall it touches, or standing on its own."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


def _is_pair(value: Any) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in value)
    )


def _is_mm(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and value > 0


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def add_wall(
    ctx: Context,
    from_point: Optional[List[float]] = None,
    to_point: Optional[List[float]] = None,
    line_id: Optional[str] = None,
    thickness: Optional[float] = None,
    height: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Add one straight wall ("add a wall from 5000,200 to 5000,3800", "make
    this line a wall") on its centreline. Each end that stops short of a wall
    face by at most 300 mm runs on to it. The wall joins the one wall record
    it touches and that host is rebuilt (a partition across a room splits
    it); a wall that touches none, or two or more separate records, stands
    as a record of its own (w02), sharing its ends with those it touches.

    Refused, with nothing changed: one that would run across an opening.
    The floor slab and flat roof from that record are rebuilt, and rooms are
    detected again. A shown daylight map is hidden as out of date.

    Parameters (from_point with to_point, or line_id):
    - from_point, to_point: The centreline's ends [x, y] in model mm.
    - line_id: Id of a straight line the user drew: its start to its end.
    - thickness: mm, at most 600. Default: the nearest wall's thickness, else 200.
    - height: mm, for a wall standing on its own (one that joins takes the
      record's height). Default: the nearest wall's height, else 3000.

    Returns:
    Dictionary with host_id, forsk_id, joined, from, to, thickness, holes,
    host_openings, host_voids, ok, message.
    """
    try:
        if (from_point is None) != (to_point is None):
            return {"success": False, "message": "Give both from_point and to_point."}
        if from_point is not None and line_id:
            return {"success": False, "message": "Give from_point and to_point, or line_id, not both."}
        if from_point is None and not line_id:
            return {"success": False, "message": "Give the wall's centreline: from_point and to_point, or line_id."}
        if from_point is not None and not (_is_pair(from_point) and _is_pair(to_point)):
            return {"success": False, "message": "from_point and to_point are [x, y] in mm."}
        if thickness is not None and (not _is_mm(thickness) or thickness > 600):
            return {"success": False, "message": "thickness is above 0 and at most 600 mm."}
        if height is not None and not _is_mm(height):
            return {"success": False, "message": "height must be positive."}

        params: Dict[str, Any] = {}
        if from_point is not None:
            params["from"] = from_point
            params["to"] = to_point
        if line_id:
            params["line_id"] = line_id
        if thickness is not None:
            params["thickness"] = thickness
        if height is not None:
            params["height"] = height

        result = get_rhino_connection().send_command("add_wall", params)
        return {
            "success": True,
            "host_id": result.get("host_id"),
            "forsk_id": result.get("forsk_id"),
            "joined": result.get("joined"),
            "from": result.get("from"),
            "to": result.get("to"),
            "thickness": result.get("thickness"),
            "height": result.get("height"),
            "holes": result.get("holes"),
            "host_openings": result.get("host_openings"),
            "host_voids": result.get("host_voids"),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in add_wall: {str(e)}")
        return {"success": False, "message": str(e)}
