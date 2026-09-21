"""Delete Forsk-generated solids and opening markers by tag."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def clear_generated(
    ctx: Context,
    kinds: Optional[List[str]] = None,
    level: Optional[str] = None,
    dry_run: bool = False,
    include_untagged_prefixes: bool = False,
    name_prefixes: Optional[List[str]] = None,
) -> Dict[str, Any]:
    """
    Delete Forsk-generated walls, floors, roofs, rooms, and opening markers.

    Matches objects with forsk:generated=1 and forsk:kind in kinds.
    Optional level filter. With include_untagged_prefixes, also deletes
    untagged objects named wall-/floor-/roof-/door-/window-/room- (pre-tag leftovers).
    Never deletes source DXF curves. Prefer tagged clear for rebuilds.

    Parameters:
    - kinds: forsk:kind values (default wall, floor, roof, opening, opening_marker, room)
    - level: optional forsk:level filter
    - dry_run: list matching ids without deleting (default false)
    - include_untagged_prefixes: also match name prefixes on untagged objects
    - name_prefixes: prefixes for untagged fallback (default wall-, floor-, roof-, door-, window-, room-)

    Returns:
    Dictionary with success, deleted (ids), count, dry_run, message.
    """
    try:
        if not isinstance(dry_run, bool):
            return {"success": False, "message": "dry_run must be a boolean"}
        if not isinstance(include_untagged_prefixes, bool):
            return {"success": False, "message": "include_untagged_prefixes must be a boolean"}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "dry_run": dry_run,
            "include_untagged_prefixes": include_untagged_prefixes,
        }
        if kinds is not None:
            params["kinds"] = kinds
        if level is not None:
            params["level"] = level
        if name_prefixes is not None:
            params["name_prefixes"] = name_prefixes

        result = rhino.send_command("clear_generated", params)
        deleted = result.get("deleted", [])
        count = result.get("count", len(deleted) if isinstance(deleted, list) else 0)
        is_dry = bool(result.get("dry_run", dry_run))
        verb = "Would delete" if is_dry else "Deleted"
        return {
            "success": True,
            "deleted": deleted,
            "count": count,
            "dry_run": is_dry,
            "message": result.get("message") or f"{verb} {count} object(s).",
        }
    except Exception as e:
        logger.error(f"Error in clear_generated: {str(e)}")
        return {"success": False, "message": str(e)}
