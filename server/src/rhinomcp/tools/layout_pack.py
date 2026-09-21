"""A3 Layout pages: one Detail of the clay per view, title block bottom-right."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("plan", "north", "east", "south", "west")
_UNKNOWN_VIEW = "Unknown view. Use plan, north, east, south, or west."
_UNKNOWN_PAPER = "Unknown paper. Use A3."


@mcp.tool()
def layout_pack(
    ctx: Context,
    paper: str = "A3",
    views: Optional[List[str]] = None,
    scale: int = 100,
    replace: bool = True,
    include_existing: bool = True,
) -> Dict[str, Any]:
    """
    Create Rhino Layout pages of the 3D clay. Not Make2D and not a PDF.

    Each page is A3 landscape with one parallel Detail (plan looks down,
    elevations match the sheet-pack directions) and a title block at the
    bottom-right. Requires generated walls. replace removes previous Forsk
    pages for those views so a re-run does not duplicate tabs.

    Parameters:
    - paper: A3 only (default A3)
    - views: Optional list of plan, north, east, south, west.
      Omit for plan plus four elevations.
    - scale: Requested denominator, 100 means 1:100. Bumped if the clay
      does not fit the detail.
    - replace: Replace existing Forsk layouts for these views (default true)
    - include_existing: Show X-EXIST in the details (default true)

    Returns:
    Dictionary with pages, count, scale, and message.
    """
    try:
        if not isinstance(paper, str) or paper != "A3":
            return {"success": False, "message": _UNKNOWN_PAPER}
        if views is not None and not isinstance(views, list):
            return {"success": False, "message": "views must be a list."}
        if views is not None and (len(views) == 0 or any(v not in _VIEWS for v in views)):
            return {"success": False, "message": _UNKNOWN_VIEW}
        if isinstance(scale, bool) or not isinstance(scale, int) or scale < 1:
            return {"success": False, "message": "Scale must be a positive number."}
        if not isinstance(replace, bool):
            return {"success": False, "message": "replace must be a boolean."}
        if not isinstance(include_existing, bool):
            return {"success": False, "message": "include_existing must be a boolean."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "paper": paper,
            "scale": scale,
            "replace": replace,
            "include_existing": include_existing,
        }
        if views is not None:
            params["views"] = views

        result = rhino.send_command("layout_pack", params)
        return {
            "success": True,
            "pages": result.get("pages", []),
            "count": result.get("count", 0),
            "scale": result.get("scale", scale),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in layout_pack: {str(e)}")
        return {"success": False, "message": str(e)}
