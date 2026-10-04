"""Add a straight stair: one closed sawtooth solid with equal risers."""

from typing import Any, Dict, List, Optional, Union

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_COMPASS = ("north", "south", "east", "west")


def _is_pair(value: Any) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in value)
    )


def _is_mm(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and value > 0


def stair_rise(rise: Any) -> Optional[str]:
    """Why a rise is not a number above 0 or "auto", else None."""
    if rise is None or rise == "auto" or _is_mm(rise):
        return None
    return 'rise is a number in mm, or "auto".'


@mcp.tool()
def add_stair(
    ctx: Context,
    from_point: Optional[List[float]] = None,
    to_point: Optional[List[float]] = None,
    along_wall: Optional[bool] = None,
    wall_id: Optional[str] = None,
    side: Optional[str] = None,
    at: Optional[List[float]] = None,
    width: Optional[float] = None,
    riser_max: Optional[float] = None,
    going: Optional[float] = None,
    rise: Optional[Union[float, str]] = None,
    against: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Add a straight stair ("add a straight stair", "legg til en trapp", "add
    a stair along this wall"): one closed solid you can walk, a sawtooth body
    from the floor up, on layer A-STAIR. Risers = ceil(rise / riser_max), all
    exactly equal so the last lands on the upper floor; treads = risers - 1;
    run = treads x going. The plan sheet draws it with tread lines, a walking
    line with an arrow, the break at the plan cut and the steps above dashed.

    Placement, first that applies:
    - from_point and to_point: the foot of the first riser on the stair's
      centre line, then a point it climbs toward.
    - along_wall (the one picked wall) or wall_id: along that wall's room
      side, from the end nearer at.
    - nothing: along the longest room wall that holds the run.

    Parameters:
    - width: mm, default 900 (800 or more is the residential guidance).
    - riser_max: highest step, mm, default 180.
    - going: tread depth, mm, default 260.
    - rise: total rise in mm, or "auto" (default): the walls' height. The
      floor slab hangs below the walking surface, so it is not added. The
      rise follows the walls when they are baked again.
    - against: "left" or "right", the side of the stair that stands against a
      wall, looking up it. Only with from_point and to_point.

    A size outside the comfort rule (2R+G 600-640, riser 180 or less) still
    builds and the message says so. Returns id, forsk_id, risers, riser,
    going, width, rise, rise_auto, run, rule, comfort, message.
    """
    try:
        if (from_point is None) != (to_point is None):
            return {"success": False, "message": "Give from_point and to_point together."}
        for name, value in (("from_point", from_point), ("to_point", to_point), ("at", at)):
            if value is not None and not _is_pair(value):
                return {"success": False, "message": f"{name} is [x, y] in mm."}
        if side is not None and side not in _COMPASS:
            return {"success": False, "message": "side is north, south, east or west."}
        for name, value in (("width", width), ("riser_max", riser_max), ("going", going)):
            if value is not None and not _is_mm(value):
                return {"success": False, "message": f"{name} must be a positive number in mm."}
        if against is not None and (against not in ("left", "right") or from_point is None):
            return {"success": False, "message": "against is left or right, with from_point and to_point."}
        why = stair_rise(rise)
        if why:
            return {"success": False, "message": why}

        params: Dict[str, Any] = {}
        if from_point is not None:
            params["from"] = from_point
            params["to"] = to_point
        if along_wall:
            params["along_wall"] = True
        if wall_id:
            params["wall_id"] = wall_id
        for key, value in (("side", side), ("at", at), ("width", width), ("riser_max", riser_max),
                           ("going", going), ("rise", rise), ("against", against)):
            if value is not None:
                params[key] = value
        result = get_rhino_connection().send_command("add_stair", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in add_stair: {str(e)}")
        return {"success": False, "message": str(e)}
