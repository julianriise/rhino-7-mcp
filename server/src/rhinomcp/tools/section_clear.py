"""Remove a stored section, its sheet and its plan marker."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def section_clear(ctx: Context, letter: Optional[str] = None) -> Dict[str, Any]:
    """
    Remove a stored section: its Layout page, its S-DRAW sheet curves and its
    marker on the plan. Does not touch walls, rooms or other pages.

    Parameters:
    - letter: A to Z. Omit to remove every section.

    Returns:
    Dictionary with removed letters, the sections left, and message.
    """
    try:
        if letter is not None and (not isinstance(letter, str) or len(letter.strip()) != 1 or not letter.strip().isalpha()):
            return {"success": False, "message": "letter is one of A to Z."}
        params: Dict[str, Any] = {}
        if letter is not None:
            params["letter"] = letter.strip().upper()
        result = get_rhino_connection().send_command("section_clear", params)
        return {
            "success": True,
            "removed": result.get("removed", []),
            "sections": result.get("sections", []),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in section_clear: {str(e)}")
        return {"success": False, "message": str(e)}
