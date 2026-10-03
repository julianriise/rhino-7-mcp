"""A3 Layout pages: one Detail of the clay per view, title block bottom-right."""

import re
from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("plan", "north", "east", "south", "west", "schedules")
# A section stored by section_add: section_a to section_z.
_SECTION = re.compile(r"^section_[a-z]$")
_SCHEDULE_KINDS = ("door", "window", "room")
_UNKNOWN_VIEW = "Unknown view. Use plan, north, east, south, west, schedules, or a stored section (section_a)."


def _known_view(view: Any) -> bool:
    return isinstance(view, str) and (view in _VIEWS or bool(_SECTION.match(view)))
_UNKNOWN_PAPER = "Unknown paper. Use A3."


@mcp.tool()
def layout_pack(
    ctx: Context,
    paper: str = "A3",
    views: Optional[List[str]] = None,
    scale: Optional[int] = None,
    replace: bool = True,
    include_existing: bool = True,
    schedule_kinds: Optional[List[str]] = None,
) -> Dict[str, Any]:
    """
    Create A3 Layout pages of a greyscale HiddenLineDrawing.

    Each page has one parallel Detail of black curves on S-DRAW and a title
    block at the bottom-right. The plan is a cut 1200 mm above the floor.
    The plan carries dimensions made from the model each time: a chain
    outside each facade with openings (corner, opening centres, corner), the
    jogs and overall per side, and each rectangular room's width and depth.
    A room name too wide for its room sits outside on a leader. Never draw
    dimensions yourself. Requires generated walls. replace removes previous
    Forsk pages for those views so a re-run does not duplicate tabs. Not a PDF.

    Parameters:
    - paper: A3 only (default A3)
    - views: Optional list of plan, north, east, south, west, schedules,
      and section_<letter> for a section stored by section_add (its own
      sheet; the plan carries its marker A–A). Omit for plan, four
      elevations, every stored section, and schedules. schedules is its own
      A3 page with the door, window and room lists (dørliste, vindusliste,
      romliste) from the model; every door and window gets a stable mark
      (D01, W01) shown on the plan and on its row. Export draws the lists
      again from the model.
    - scale: Requested denominator, 100 means 1:100. Rounded up to a
      standard step if the drawing does not fit the detail. Omit to fit:
      the plan takes the largest scale that fits (elevations 1:100).
    - replace: Replace existing Forsk layouts for these views (default true)
    - include_existing: Include X-EXIST in the greyscale drawing (default true)
    - schedule_kinds: Which lists the schedules page shows: door, window,
      room. Omit for all three.

    Returns:
    Dictionary with pages, count, scale, and message.
    """
    try:
        if not isinstance(paper, str) or paper != "A3":
            return {"success": False, "message": _UNKNOWN_PAPER}
        if views is not None and not isinstance(views, list):
            return {"success": False, "message": "views must be a list."}
        if views is not None and (len(views) == 0 or any(not _known_view(v) for v in views)):
            return {"success": False, "message": _UNKNOWN_VIEW}
        if scale is not None and (isinstance(scale, bool) or not isinstance(scale, int) or scale < 1):
            return {"success": False, "message": "Scale must be a positive number."}
        if not isinstance(replace, bool):
            return {"success": False, "message": "replace must be a boolean."}
        if not isinstance(include_existing, bool):
            return {"success": False, "message": "include_existing must be a boolean."}
        if schedule_kinds is not None and (
            not isinstance(schedule_kinds, list)
            or len(schedule_kinds) == 0
            or any(k not in _SCHEDULE_KINDS for k in schedule_kinds)
        ):
            return {"success": False, "message": "schedule_kinds are door, window, and room."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "paper": paper,
            "replace": replace,
            "include_existing": include_existing,
        }
        if views is not None:
            params["views"] = views
        if scale is not None:
            params["scale"] = scale
        if schedule_kinds is not None:
            params["schedule_kinds"] = schedule_kinds

        result = rhino.send_command("layout_pack", params)
        message = result.get("message", "")
        # The plugin throws this. A result with the same text is still a failure.
        empty_detail = (
            "Layout detail is empty" in str(message)
            or "does not show the drawing" in str(message)
            or "does not show the clay" in str(message)
        )
        return {
            "success": not empty_detail,
            "pages": result.get("pages", []),
            "count": result.get("count", 0),
            "scale": result.get("scale", scale),
            "message": message,
        }
    except Exception as e:
        logger.error(f"Error in layout_pack: {str(e)}")
        return {"success": False, "message": str(e)}
