"""One Make2D view as tagged curves on an S-* layer."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("plan", "north", "east", "south", "west")
_UNKNOWN_VIEW = "Unknown view. Use plan, north, east, south, or west."


@mcp.tool()
def make2d_view(
    ctx: Context,
    view: str,
    ids: Optional[List[str]] = None,
    include_existing: bool = True,
    replace: bool = True,
) -> Dict[str, Any]:
    """
    Draw one parallel Make2D view with HiddenLineDrawing.

    Bakes visible curves on S-PLAN / S-ELEV-N / S-ELEV-E / S-ELEV-S / S-ELEV-W.
    Stamps forsk:kind=drawing, forsk:generated=1, forsk:view, and forsk:id d01….
    With ids omitted, sources are generated wall, floor, roof, and opening solids.
    Opening markers and room markers are skipped. include_existing adds X-EXIST.
    replace deletes the previous pack for that view so a re-run does not duplicate.
    No sources returns count 0 and a message. Not a bitmap and not a Layout page.

    Parameters:
    - view: plan, north, east, south, or west (required)
    - ids: Optional source GUIDs. Omit to gather Forsk clay.
    - include_existing: Include existing underlay when ids is omitted (default true)
    - replace: Delete the previous drawing pack for this view first (default true)

    Returns:
    Dictionary with count, ids, layer, view, and message.
    """
    try:
        if not isinstance(view, str) or view not in _VIEWS:
            return {"success": False, "message": _UNKNOWN_VIEW}
        if ids is not None and not isinstance(ids, list):
            return {"success": False, "message": "ids must be a list of GUIDs."}
        if not isinstance(include_existing, bool):
            return {"success": False, "message": "include_existing must be a boolean."}
        if not isinstance(replace, bool):
            return {"success": False, "message": "replace must be a boolean."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "view": view,
            "include_existing": include_existing,
            "replace": replace,
        }
        if ids:
            params["ids"] = ids

        result = rhino.send_command("make2d_view", params)
        return {
            "success": True,
            "count": result.get("count", 0),
            "ids": result.get("ids", []),
            "layer": result.get("layer", ""),
            "view": result.get("view", view),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in make2d_view: {str(e)}")
        return {"success": False, "message": str(e)}
