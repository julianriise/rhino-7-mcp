"""Choose how the sheets are inked (F5.4 print profile)."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

PROFILES = ("default", "grey", "hatch")


@mcp.tool()
def print_profile(ctx: Context, name: Optional[str] = None) -> Dict[str, Any]:
    """
    Choose the print profile of the document, or read the current one.

    A profile sets the poché fill (solid or hatched, its colour) and the
    colour and weight of the plan's line tiers (cut, beyond, thin) and of
    dashed lines. It drives the plan, the section sheets and the schedule
    rules. The choice is stored in the document. The next layout_pack or Print
    draws with it; pages already made keep their old look until then.

    Parameters:
    - name: default (solid black poché, black lines), grey (grey poché,
      coloured lines) or hatch (hatched poché, lighter cut line). Omit to read
      the current profile.

    Returns:
    Dictionary with the profile (name, label, pens, poché), the profiles
    available, whether it changed, and message.
    """
    try:
        params: Dict[str, Any] = {}
        if name is not None:
            if not isinstance(name, str):
                return {"success": False, "message": "name must be a string."}
            if name.strip():
                params["name"] = name.strip()
        result = get_rhino_connection().send_command("print_profile", params)
        return {
            "success": True,
            "profile": result.get("profile", {}),
            "available": result.get("available", []),
            "changed": bool(result.get("changed", False)),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in print_profile: {str(e)}")
        return {"success": False, "message": str(e)}
