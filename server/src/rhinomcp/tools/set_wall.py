"""Set one straight wall's thickness or length, or every wall's height, with no face clicked."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def set_wall(
    ctx: Context,
    thickness_mm: Optional[float] = None,
    length_mm: Optional[float] = None,
    height_mm: Optional[float] = None,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Set a wall's size the way the Properties panel does ("make w03 150
    thick", "make this wall 3600 long", "make the walls 2700 high"). Give one
    of thickness_mm, length_mm or height_mm.

    thickness_mm makes one straight wall that thick: an outer wall keeps its
    outside face, any other wall keeps its centreline. length_mm moves the
    wall's free end along it and the start stays; a wall joined at both ends
    is refused. Walls meeting a moved face follow, and the floor, roof and
    rooms follow too. height_mm makes every wall that high (one storey), with
    the flat roof moved onto them; an opening whose head would reach the top
    is refused. Refuses X-EXIST.

    Parameters:
    - thickness_mm: The new thickness, above 0 and at most 600 mm.
    - length_mm: The new length in mm, above 0.
    - height_mm: Every wall's new height in mm, above 0.
    - id: Wall GUID for thickness_mm or length_mm. Omit to use the selected wall.

    Returns:
    Dictionary with host_id, wall, thickness, length_mm, kept, followed (or
    height, walls, roofs for height_mm), ok, message.
    """
    try:
        given = {k: v for k, v in (("thickness_mm", thickness_mm), ("length_mm", length_mm), ("height_mm", height_mm)) if v is not None}
        if len(given) != 1:
            return {"success": False, "message": "Give one of thickness_mm, length_mm or height_mm."}
        name, value = next(iter(given.items()))
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0:
            return {"success": False, "message": f"{name} must be a positive number in mm."}
        if name == "thickness_mm" and value > 600:
            return {"success": False, "message": "thickness_mm is at most 600 mm."}

        params: Dict[str, Any] = {name: value}
        if id and name != "height_mm":
            params["id"] = id
        result = get_rhino_connection().send_command("set_wall", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in set_wall: {str(e)}")
        return {"success": False, "message": str(e)}
