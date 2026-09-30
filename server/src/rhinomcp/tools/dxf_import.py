"""Forsk's DXF import: Rhino's import, then texts rewritten from the DXF source."""

import os
from typing import Any, Dict

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def dxf_import(ctx: Context, path: str = "") -> Dict[str, Any]:
    """
    Import a DXF plan with its text intact.

    Rhino's own import loses DXF text escapes (B\\U+00F8ttekott comes in as
    B00F8ttekott). This runs Rhino's import, then reads every TEXT and MTEXT
    from the DXF file and rewrites the imported texts, matched by layer and
    position. Room names from rooms_detect then read the right text.

    It also lands the plan at true size in the mm model. The DXF's units are
    read from $INSUNITS (inches, feet, mm, cm or m). When that is missing or 0
    they are guessed from the drawing's size (under 500 across is metres, else
    mm) and a warning says so. Rhino's import takes the units from its own
    AutoCAD import setting instead; what it scaled by is measured and set right.

    path must be an absolute path to a .dxf file.

    Returns:
    Dictionary with objects, texts, matched, rewritten, unmatched,
    labels_suspect, units, insunits, scale (mm per drawing unit),
    units_guessed, warnings, and message.
    """
    try:
        if not isinstance(path, str) or not path.strip():
            return {"success": False, "message": "dxf_import requires a file path."}
        path = path.strip()
        if not os.path.isabs(path) or not path.lower().endswith(".dxf"):
            return {"success": False, "message": "dxf_import path must be an absolute .dxf file."}
        result = get_rhino_connection().send_command("dxf_import", {"path": path})
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in dxf_import: {str(e)}")
        return {"success": False, "message": str(e)}
