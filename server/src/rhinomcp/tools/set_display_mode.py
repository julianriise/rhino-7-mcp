"""Set the active viewport display mode."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


DISPLAY_MODES = {
    "rendered": "Rendered",
    "arctic": "Arctic",
    "shaded": "Shaded",
    "wireframe": "Wireframe",
}


@mcp.tool()
def set_display_mode(
    ctx: Context,
    mode: str,
) -> Dict[str, Any]:
    """
    Set the active viewport display mode.

    Rendered shows layer materials (plaster, concrete, wood). Arctic is a
    clay/white look. Use after a plan build or material override so the
    viewport reads as intended. Arctic hides material color.

    Parameters:
    - mode: Rendered | Arctic | Shaded | Wireframe
    """
    try:
        if not mode or not str(mode).strip():
            return {"success": False, "message": "mode is required"}

        canonical = DISPLAY_MODES.get(mode.strip().lower())
        if canonical is None:
            return {
                "success": False,
                "message": "mode must be Rendered, Arctic, Shaded, or Wireframe.",
            }

        rhino = get_rhino_connection()
        result = rhino.send_command("set_display_mode", {"mode": canonical})
        return {
            "success": True,
            "mode": result.get("mode", canonical),
            "viewport": result.get("viewport"),
            "message": result.get("message", f"Set display mode to {canonical}"),
        }
    except Exception as e:
        logger.error(f"Error in set_display_mode: {str(e)}")
        return {"success": False, "message": str(e)}
