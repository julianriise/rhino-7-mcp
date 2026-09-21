"""Plan plus elevations as Make2D curve packs on S-* layers."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("plan", "north", "east", "south", "west")
_UNKNOWN_VIEW = "Unknown view. Use plan, north, east, south, or west."


@mcp.tool()
def sheet_pack(
    ctx: Context,
    views: Optional[List[str]] = None,
    include_existing: bool = True,
    replace: bool = True,
) -> Dict[str, Any]:
    """
    Draw a sheet pack: plan and four elevations, or the views you name.

    Each view is a make2d_view. Curves land on S-* layers with
    forsk:kind=drawing. Model-space linework, not a Layout page or a PDF.
    Re-run after a 3D rebuild if the drawings look stale. clear_generated
    does not remove them.

    Parameters:
    - views: Optional list of plan, north, east, south, west.
      Omit for plan plus all four elevations.
    - include_existing: Include X-EXIST in each view (default true)
    - replace: Replace each view's previous pack (default true)

    Returns:
    Dictionary with views (per-view results), count, and message.
    """
    try:
        if views is not None and not isinstance(views, list):
            return {"success": False, "message": "views must be a list."}
        if views is not None and any(v not in _VIEWS for v in views):
            return {"success": False, "message": _UNKNOWN_VIEW}
        if not isinstance(include_existing, bool):
            return {"success": False, "message": "include_existing must be a boolean."}
        if not isinstance(replace, bool):
            return {"success": False, "message": "replace must be a boolean."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "include_existing": include_existing,
            "replace": replace,
        }
        if views is not None:
            params["views"] = views

        result = rhino.send_command("sheet_pack", params)
        return {
            "success": True,
            "views": result.get("views", []),
            "count": result.get("count", 0),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in sheet_pack: {str(e)}")
        return {"success": False, "message": str(e)}
