"""The user's own plan dimensions: add across parallel walls, remove, list."""

import re
from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_ACTIONS = ("add", "remove", "list")
_ID = re.compile(r"^U[0-9]+$")


@mcp.tool()
def plan_dims(
    ctx: Context,
    action: str = "list",
    ids: Optional[List[str]] = None,
    refs: Optional[List[Dict[str, str]]] = None,
) -> Dict[str, Any]:
    """
    Dimensions the user asked for on the plan. They are stored by Forsk ids
    and drawn at the next Print, so a moved wall carries its dimension.

    action add: a dimension across two or more parallel walls, with a stop at
    both faces of each wall and at the centre of each picked door or window
    in a wall that runs along it. refs is [{"wall": "w03"}, {"opening": "..."}];
    omit refs to use the walls and openings picked in Rhino.
    action remove: ids such as ["U02"], or omit ids to remove every one
    ("remove the dimensions", "fjern målene").
    action list: what is stored.

    Returns:
    Dictionary with dims, count and message (add also id, total, values, skipped).
    """
    try:
        act = (action or "").strip().lower()
        if act not in _ACTIONS:
            return {"success": False, "message": "action is add, remove or list."}
        params: Dict[str, Any] = {"action": act}
        if ids is not None:
            if act != "remove" or not all(isinstance(i, str) and _ID.match(i.strip()) for i in ids):
                return {"success": False, "message": "ids are dimension ids such as U02, for remove."}
            params["ids"] = [i.strip() for i in ids]
        if refs is not None:
            ok = act == "add" and len(refs) >= 2 and all(
                isinstance(r, dict) and len(r) == 1 and next(iter(r)) in ("wall", "opening") for r in refs
            )
            if not ok:
                return {"success": False, "message": "refs are two or more {wall} or {opening} ids, for add."}
            params["refs"] = refs

        result = get_rhino_connection().send_command("plan_dims", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in plan_dims: {str(e)}")
        return {"success": False, "message": str(e)}
