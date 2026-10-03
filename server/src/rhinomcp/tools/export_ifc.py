"""Write the model as IFC4. No save dialog."""

import os
from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_NEEDS_PATH = "export_ifc needs an absolute .ifc path."


@mcp.tool()
def export_ifc(ctx: Context, path: str = "") -> Dict[str, Any]:
    """
    Export the model as IFC4 for an IFC viewer or a consultant: walls with a
    material layer set, doors and windows sitting in their openings, slabs,
    the flat roof, and the rooms as spaces with their names and net floor
    area. Use it for "export ifc", "eksporter ifc". The document does not change.

    path must be an absolute file ending in .ifc.

    Returns:
    Dictionary with path, walls, doors, windows, slabs, roofs, spaces and message.
    """
    try:
        if not isinstance(path, str) or not path.strip():
            return {"success": False, "message": _NEEDS_PATH}
        path = path.strip()
        if not os.path.isabs(path) or not path.lower().endswith(".ifc"):
            return {"success": False, "message": _NEEDS_PATH}
        result = get_rhino_connection().send_command("export_ifc", {"path": path})
        return {"success": bool(result.get("path")), **result}
    except Exception as e:
        logger.error(f"Error in export_ifc: {str(e)}")
        return {"success": False, "message": str(e)}
