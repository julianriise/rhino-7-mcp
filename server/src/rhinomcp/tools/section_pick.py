"""Pick a cross section in the viewport: an X or Y line at z=0, then a name."""

from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def section_pick(ctx: Context) -> Dict[str, Any]:
    """
    Switch to the Top view and let the user drag a cross-section line.
    The line stays at z=0 and locks to X or Y, including a pick started in
    another view. A dialog asks for the name (A1, then A2). The cut is stored
    with section_add: the plan marker stays A–A and the sheet stays
    section_<letter>. The line goes on the hidden layer cross-sections.
    Cancelling the line or the dialog changes nothing.

    No parameters. The user picks in the Rhino viewport.

    Returns:
    Dictionary with section, sections, view, name, and message.
    """
    try:
        result = get_rhino_connection().send_command("section_pick", {})
        return {
            "success": True,
            "section": result.get("section"),
            "sections": result.get("sections", []),
            "view": result.get("view"),
            "name": result.get("name"),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in section_pick: {str(e)}")
        return {"success": False, "message": str(e)}
