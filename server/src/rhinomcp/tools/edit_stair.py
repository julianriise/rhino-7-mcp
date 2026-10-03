"""Change a stair's width, step height, going or rise, or flip it."""

from typing import Any, Dict, Optional, Union

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from rhinomcp.tools.add_stair import stair_rise


@mcp.tool()
def edit_stair(
    ctx: Context,
    id: Optional[str] = None,
    width: Optional[float] = None,
    riser_max: Optional[float] = None,
    going: Optional[float] = None,
    rise: Optional[Union[float, str]] = None,
    flip: Optional[bool] = None,
) -> Dict[str, Any]:
    """
    Change a stair and rebuild it in place: "make the stair 1000 wide"
    (width), "steps 170 high" (riser_max), "going 280" (going), "rise 2750"
    or "auto", "flip the stair" (flip: the same footprint climbed the other
    way). The risers re-plan, all equal. A wider stair grows away from the
    wall it stands against.

    Parameters:
    - id: the stair's GUID or forsk:id (S01). Omit for the picked stair, else the only one.

    Returns id, forsk_id, risers, riser, going, width, rise, rise_auto, run,
    rule, comfort, message.
    """
    try:
        sizes = {"width": width, "riser_max": riser_max, "going": going}
        for name, value in sizes.items():
            if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0):
                return {"success": False, "message": f"{name} must be a positive number in mm."}
        why = stair_rise(rise)
        if why:
            return {"success": False, "message": why}
        params: Dict[str, Any] = {k: v for k, v in sizes.items() if v is not None}
        if rise is not None:
            params["rise"] = rise
        if flip:
            params["flip"] = True
        if not params:
            return {"success": False, "message": "Say what to change: width, step height, going, rise or flip."}
        if id:
            params["id"] = id
        result = get_rhino_connection().send_command("edit_stair", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in edit_stair: {str(e)}")
        return {"success": False, "message": str(e)}
