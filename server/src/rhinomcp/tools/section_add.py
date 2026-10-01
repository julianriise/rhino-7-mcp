"""Store a cross section A–A: the line in plan and the side it looks at."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_AXES = ("cross", "long")
_LOOKS = ("north", "south", "east", "west")


def _is_pair(value: Any) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in value)
    )


@mcp.tool()
def section_add(
    ctx: Context,
    letter: Optional[str] = None,
    room: Optional[str] = None,
    axis: Optional[str] = None,
    from_point: Optional[List[float]] = None,
    to_point: Optional[List[float]] = None,
    line_id: Optional[str] = None,
    look: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Store a cross section (snitt) A–A in the document: a vertical cut along a
    line in plan, looking to one side of it. Then layout_pack views plan and
    section_<letter> draws its marker A–A on the plan and its own sheet: poché
    where the plane cuts walls, slabs and roof (openings stay gaps), lines
    beyond thin, the ground line, level marks, the free height of each room
    it crosses, gesims and møne. Never draw a section yourself.

    Parameters (one of room, from_point with to_point, or line_id):
    - letter: A to Z. Omit for the next free letter. The same letter again
      moves that section.
    - room: A room's tag name or id (rd-01): the line runs through it.
    - axis: With room: cross (across the building's longer side, default)
      or long (along it).
    - from_point, to_point: The line's ends [x, y] in model mm.
    - line_id: Id of a curve the user drew or picked: the line from its
      start to its end.
    - look: north, south, east or west: the side the section looks toward.
      Omit for the left of the line.

    Returns:
    Dictionary with section, sections, view, replaced, and message.
    """
    try:
        if letter is not None and (not isinstance(letter, str) or len(letter.strip()) != 1 or not letter.strip().isalpha()):
            return {"success": False, "message": "letter is one of A to Z."}
        if axis is not None and axis not in _AXES:
            return {"success": False, "message": "axis is cross or long."}
        if look is not None and look not in _LOOKS:
            return {"success": False, "message": "look is north, south, east or west."}
        if (from_point is None) != (to_point is None):
            return {"success": False, "message": "Give both from_point and to_point."}
        if from_point is not None and not (_is_pair(from_point) and _is_pair(to_point)):
            return {"success": False, "message": "from_point and to_point are [x, y] in mm."}
        if room is None and from_point is None and not line_id:
            return {"success": False, "message": "No section. Give a room, from and to points, or a line_id."}

        params: Dict[str, Any] = {}
        if letter is not None:
            params["letter"] = letter.strip().upper()
        if room is not None:
            params["room"] = room
        if axis is not None:
            params["axis"] = axis
        if from_point is not None:
            params["from"] = from_point
            params["to"] = to_point
        if line_id:
            params["line_id"] = line_id
        if look is not None:
            params["look"] = look

        result = get_rhino_connection().send_command("section_add", params)
        return {
            "success": True,
            "section": result.get("section"),
            "sections": result.get("sections", []),
            "view": result.get("view"),
            "replaced": result.get("replaced", False),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in section_add: {str(e)}")
        return {"success": False, "message": str(e)}
