"""Delete Forsk Layout pages only."""

import re
from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger

_VIEWS = ("plan", "north", "east", "south", "west", "schedules")
# A section stored by section_add: section_a to section_z.
_SECTION = re.compile(r"^section_[a-z]$")
_UNKNOWN_VIEW = "Unknown view. Use plan, north, east, south, west, schedules, or a stored section (section_a)."


def _known_view(view: Any) -> bool:
    return isinstance(view, str) and (view in _VIEWS or bool(_SECTION.match(view)))


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def clear_layouts(
    ctx: Context,
    views: Optional[List[str]] = None,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """
    Delete Forsk Layout pages, their title-block objects, and the greyscale
    S-DRAW curves for those views.

    Does not delete walls, floors, roofs, openings, rooms, X-EXIST, or the
    sheet-cache curves on S-PLAN / S-ELEV-*. clear_generated also leaves layouts.

    Parameters:
    - views: Optional filter (plan, north, east, south, west, schedules).
      Omit to delete every Forsk layout.
    - dry_run: List matching pages without deleting (default false)

    Returns:
    Dictionary with deleted page names, object_ids, count, dry_run, message.
    """
    try:
        if views is not None and not isinstance(views, list):
            return {"success": False, "message": "views must be a list."}
        if views is not None and any(not _known_view(v) for v in views):
            return {"success": False, "message": _UNKNOWN_VIEW}
        if not isinstance(dry_run, bool):
            return {"success": False, "message": "dry_run must be a boolean"}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {"dry_run": dry_run}
        if views is not None:
            params["views"] = views

        result = rhino.send_command("clear_layouts", params)
        deleted = result.get("deleted", [])
        count = result.get("count", len(deleted) if isinstance(deleted, list) else 0)
        is_dry = bool(result.get("dry_run", dry_run))
        verb = "Would delete" if is_dry else "Deleted"
        return {
            "success": True,
            "deleted": deleted,
            "object_ids": result.get("object_ids", []),
            "count": count,
            "dry_run": is_dry,
            "message": result.get("message") or f"{verb} {count} layout(s).",
        }
    except Exception as e:
        logger.error(f"Error in clear_layouts: {str(e)}")
        return {"success": False, "message": str(e)}
