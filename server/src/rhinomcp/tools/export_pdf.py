"""Write Forsk Layout pages to a PDF path. No save dialog."""

import os
from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_NEEDS_PATH = "export_pdf requires a file path."
_NEEDS_PDF = "export_pdf path must be an absolute .pdf file."
_EMPTY_DETAIL = "PDF detail is empty. The sheet does not show the clay."


@mcp.tool()
def export_pdf(
    ctx: Context,
    path: str = "",
    layout: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Write a vector PDF of Forsk Layout pages.

    path is required and must be an absolute file ending in .pdf.
    This tool does not open a save dialog. Call layout_pack first.
    layout may be a page name or a view token (plan, north, east, south, west).
    Omit layout to export every Forsk page.

    Returns:
    Dictionary with path, count, pages, and message.
    """
    try:
        if not isinstance(path, str) or not path.strip():
            return {"success": False, "message": _NEEDS_PATH}
        path = path.strip()
        if not os.path.isabs(path) or not path.lower().endswith(".pdf"):
            return {"success": False, "message": _NEEDS_PDF}
        if layout is not None and not isinstance(layout, str):
            return {"success": False, "message": "layout must be a string."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {"path": path}
        if layout is not None:
            params["layout"] = layout

        result = rhino.send_command("export_pdf", params)
        message = result.get("message", "")
        count = result.get("count", 0)
        ok = count > 0 and not str(message).startswith("PDF write failed")
        if message in (
            "No layouts to print. Call layout_pack first.",
            "Unknown layout.",
            "PDF write failed.",
            _EMPTY_DETAIL,
            _NEEDS_PATH,
            _NEEDS_PDF,
        ) or "does not show the clay" in str(message):
            ok = False
        return {
            "success": ok,
            "path": result.get("path", path if ok else ""),
            "count": count,
            "pages": result.get("pages", []),
            "message": message,
        }
    except Exception as e:
        logger.error(f"Error in export_pdf: {str(e)}")
        return {"success": False, "message": str(e)}
