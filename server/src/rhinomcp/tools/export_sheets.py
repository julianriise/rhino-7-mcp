"""Write the sheet set as DWG or DXF, one file per sheet. No save dialog."""

import os
from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_NEEDS_FOLDER = "export_sheets needs an absolute folder."
_FORMAT = "format is dwg or dxf."


@mcp.tool()
def export_sheets(ctx: Context, folder: str = "", format: str = "dwg") -> Dict[str, Any]:
    """
    Export every sheet in the set as its own DWG or DXF file, in paper
    millimetres at 1:1, with named layers (A-WALL-CUT, A-WALL-PATT, A-SYMB,
    A-ELEV, A-GRND, A-ANNO-DIMS, A-ANNO-TEXT, A-ANNO-TTLB) and lineweights.
    It lays the set out first, as Print does. Use it for "export dwg",
    "send dwg to the consultant", "eksporter dxf".

    folder must be an absolute folder; it is made when missing.
    format is dwg (default) or dxf.

    Returns:
    Dictionary with folder, format, count, files, writer and message.
    """
    try:
        if not isinstance(folder, str) or not folder.strip() or not os.path.isabs(folder.strip()):
            return {"success": False, "message": _NEEDS_FOLDER}
        fmt = (format or "dwg").strip().lower() if isinstance(format, str) else ""
        if fmt not in ("dwg", "dxf"):
            return {"success": False, "message": _FORMAT}

        result = get_rhino_connection().send_command(
            "export_sheets", {"folder": folder.strip(), "format": fmt}
        )
        ok = result.get("count", 0) > 0 and str(result.get("message", "")).startswith("Exported")
        return {"success": ok, **result}
    except Exception as e:
        logger.error(f"Error in export_sheets: {str(e)}")
        return {"success": False, "message": str(e)}
