"""Detail drawings of picked walls, doors and windows: add, remove, list."""

import re
from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_ACTIONS = ("add", "remove", "list")
_ID = re.compile(r"^DET[0-9]+$")


@mcp.tool()
def details(
    ctx: Context,
    action: str = "list",
    ids: Optional[List[str]] = None,
    refs: Optional[List[Dict[str, str]]] = None,
) -> Dict[str, Any]:
    """
    Detail drawings, stored by Forsk ids and drawn at every Print on their
    own detail sheets (A-50-001, …), with a callout on the plan.

    action add: one detail per wall, door or window. A wall gets a plan
    detail and a section; a door or window a plan detail, an elevation and
    a section. refs is [{"wall": "w03"}, {"opening": "..."}]; omit refs to
    use what is picked in Rhino. An element with a detail is not added again.
    action remove: ids such as ["DET02"], or omit ids to remove every one
    ("remove the details", "fjern detaljene").
    action list: what is stored.

    Returns:
    Dictionary with details, count and message (add also added and already).
    """
    try:
        act = (action or "").strip().lower()
        if act not in _ACTIONS:
            return {"success": False, "message": "action is add, remove or list."}
        params: Dict[str, Any] = {"action": act}
        if ids is not None:
            if act != "remove" or not all(isinstance(i, str) and _ID.match(i.strip()) for i in ids):
                return {"success": False, "message": "ids are detail ids such as DET02, for remove."}
            params["ids"] = [i.strip() for i in ids]
        if refs is not None:
            ok = act == "add" and len(refs) >= 1 and all(
                isinstance(r, dict) and len(r) == 1 and next(iter(r)) in ("wall", "opening") for r in refs
            )
            if not ok:
                return {"success": False, "message": "refs are one or more {wall} or {opening} ids, for add."}
            params["refs"] = refs

        result = get_rhino_connection().send_command("details", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in details: {str(e)}")
        return {"success": False, "message": str(e)}
