"""Write the takeoff as a CSV. No save dialog."""

import os
from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_NEEDS_PATH = "export_csv needs an absolute .csv path."


@mcp.tool()
def export_csv(ctx: Context, path: str = "") -> Dict[str, Any]:
    """
    Export the takeoff (mengdeliste) as one CSV table for a spreadsheet: the
    project info on top, then rooms (area, perimeter), walls (one row per
    run: length, height, thickness, net area), doors and windows (mark,
    type, host wall, sizes) and stairs. UTF-8 with a BOM, comma separated,
    decimal point. Use it for "export csv", "export schedule", "eksporter csv".
    The document does not change.

    path must be an absolute file ending in .csv.

    Returns:
    Dictionary with path, rooms, walls, doors, windows, stairs and message.
    """
    try:
        if not isinstance(path, str) or not path.strip():
            return {"success": False, "message": _NEEDS_PATH}
        path = path.strip()
        if not os.path.isabs(path) or not path.lower().endswith(".csv"):
            return {"success": False, "message": _NEEDS_PATH}
        result = get_rhino_connection().send_command("export_csv", {"path": path})
        return {"success": bool(result.get("path")), **result}
    except Exception as e:
        logger.error(f"Error in export_csv: {str(e)}")
        return {"success": False, "message": str(e)}
