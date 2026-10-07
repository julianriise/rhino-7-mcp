"""Move or turn one piece of furniture."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from rhinomcp.args import is_pair


@mcp.tool()
def move_furniture(
    ctx: Context,
    id: Optional[str] = None,
    to: Optional[List[float]] = None,
    by: Optional[List[float]] = None,
    rotate: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Move or turn one piece of furniture ("move the sofa 500 left", "turn the
    bed 90 degrees", "flytt senga hit").

    Parameters:
    - id: the piece's GUID or forsk:id (F01). Omit for the picked piece,
      else the only one.
    - to: new centre [x, y] in mm.
    - by: move [dx, dy] in mm.
    - rotate: turn about its centre, degrees counter-clockwise.

    The piece must stay inside a room and clear of other pieces, else it is
    refused with the reason. Returns the piece as furniture_result.
    """
    try:
        if to is None and by is None and rotate is None:
            return {"success": False, "message": "Say where: to [x, y], by [dx, dy], or rotate in degrees."}
        for name, value in (("to", to), ("by", by)):
            if value is not None and not is_pair(value):
                return {"success": False, "message": f"{name} is [x, y] in mm."}
        params: Dict[str, Any] = {}
        for key, value in (("id", id), ("to", to), ("by", by), ("rotate", rotate)):
            if value is not None:
                params[key] = value
        result = get_rhino_connection().send_command("move_furniture", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in move_furniture: {str(e)}")
        return {"success": False, "message": str(e)}
