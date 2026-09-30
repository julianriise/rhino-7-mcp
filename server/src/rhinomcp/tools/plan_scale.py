"""Plan scale: an imported plan's scale from two points and a known length."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def plan_scale(
    ctx: Context,
    p1: Optional[List[float]] = None,
    p2: Optional[List[float]] = None,
    length_mm: Optional[float] = None,
    frame: str = "model",
) -> Dict[str, Any]:
    """
    Set an imported plan's scale from two points and the real length between them.

    The underlay and everything on the plan layers (wall, door, window, A-ROOM,
    label) scale together about the plan's top-left corner. The imported wall
    outlines are cleaned again from the detection at the new scale, so their
    thickness is rounded to 10 mm there; the same scale always gives the same
    walls, so repeating the step or scaling away and back does not drift. Wall
    outlines edited since the import scale as drawn. Refused once the 3D
    model is generated: clear_generated first.

    Parameters:
    - p1, p2: [x, y] in mm, on two ends of a length the user knows
    - length_mm: the real length between them. Omit to measure only: the result
      says how far apart the points are at the scale the plan has now
    - frame: "model" (points as the plan lies in the document now) or "source"
      (points in the detection file's own mm; the same call then always gives
      the same scale)

    Returns:
    Dictionary with measured_mm, factor (scale from the detection's own mm),
    status, scaled, and message; with a length also length_mm,
    previous_factor, relative, and walls_recleaned.
    """
    try:
        if p1 is None or p2 is None or len(p1) < 2 or len(p2) < 2:
            return {"success": False, "message": "plan_scale needs p1 and p2 as [x, y] in mm."}
        if frame not in ("model", "source"):
            return {"success": False, "message": "plan_scale frame must be model or source."}
        params: Dict[str, Any] = {"p1": list(p1[:2]), "p2": list(p2[:2]), "frame": frame}
        if length_mm is not None:
            if length_mm <= 0:
                return {"success": False, "message": "plan_scale length_mm must be above 0."}
            params["length_mm"] = length_mm
        result = get_rhino_connection().send_command("plan_scale", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in plan_scale: {str(e)}")
        return {"success": False, "message": str(e)}
