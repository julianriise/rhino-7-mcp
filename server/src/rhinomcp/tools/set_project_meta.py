"""Store project meta for the layout title block."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


def _text(value: Optional[str], key: str) -> Optional[str]:
    if value is None:
        return None
    if not isinstance(value, str):
        raise TypeError(f"{key} must be a string.")
    return value


@mcp.tool()
def set_project_meta(
    ctx: Context,
    project: Optional[str] = None,
    client: Optional[str] = None,
    address: Optional[str] = None,
    date: Optional[str] = None,
    scale_label: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Store project, client, address, date, and scale label on the document.

    The next layout_pack reads these into the title block. Omitted keys are
    left unchanged. An empty string clears that key. date falls back to today
    and scale_label to 1:100 when unset.

    Returns:
    Dictionary with project, client, address, date, and scale_label.
    """
    try:
        params: Dict[str, Any] = {}
        for key, value in (
            ("project", project),
            ("client", client),
            ("address", address),
            ("date", date),
            ("scale_label", scale_label),
        ):
            text = _text(value, key)
            if text is not None:
                params[key] = text

        rhino = get_rhino_connection()
        result = rhino.send_command("set_project_meta", params)
        return {
            "success": True,
            "project": result.get("project", ""),
            "client": result.get("client", ""),
            "address": result.get("address", ""),
            "date": result.get("date", ""),
            "scale_label": result.get("scale_label", "1:100"),
            "message": "Stored project meta.",
        }
    except Exception as e:
        logger.error(f"Error in set_project_meta: {str(e)}")
        return {"success": False, "message": str(e)}
