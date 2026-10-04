"""Store the project info for the title blocks, the PDF, the takeoff CSV and the IFC."""

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
    project_no: Optional[str] = None,
    client: Optional[str] = None,
    address: Optional[str] = None,
    architect: Optional[str] = None,
    date: Optional[str] = None,
    revision: Optional[str] = None,
    scale_label: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Store the project info on the document: project, project number, client,
    address, architect, date and revision, plus the scale label.

    Every title block, the PDF metadata, the takeoff CSV header and the IFC
    read these. Omitted keys are left unchanged. An empty string clears that
    key. date falls back to today and scale_label to 1:100 when unset.

    Returns:
    Dictionary with every key above.
    """
    try:
        params: Dict[str, Any] = {}
        for key, value in (
            ("project", project),
            ("project_no", project_no),
            ("client", client),
            ("address", address),
            ("architect", architect),
            ("date", date),
            ("revision", revision),
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
            "project_no": result.get("project_no", ""),
            "client": result.get("client", ""),
            "address": result.get("address", ""),
            "architect": result.get("architect", ""),
            "date": result.get("date", ""),
            "revision": result.get("revision", ""),
            "scale_label": result.get("scale_label", "1:100"),
            "message": "Stored project info.",
        }
    except Exception as e:
        logger.error(f"Error in set_project_meta: {str(e)}")
        return {"success": False, "message": str(e)}
