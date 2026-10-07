"""Saved options (AN.5) and comparing one with the model now (AN.6)."""

import re
from typing import Any, Callable, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _-]{0,31}$")
_BAD_NAME = "An option name is letters, digits, space, _ or -, at most 32."


@mcp.tool()
def save_option(ctx: Context, name: Optional[str] = None) -> Dict[str, Any]:
    """
    Save the model as an option ("save as option A", "lagre som alternativ"):
    walls, doors and windows, rooms, project info and analysis settings as
    JSON beside the saved 3dm. Omit name for the next free letter. The model
    does not change.

    Returns name, path, walls, openings, rooms, message.
    """
    try:
        if name is not None and not _NAME.match(name):
            return {"success": False, "message": _BAD_NAME}
        params: Dict[str, Any] = {"name": name} if name else {}
        return {"success": True, **get_rhino_connection().send_command("save_option", params)}
    except Exception as e:
        logger.error(f"Error in save_option: {str(e)}")
        return {"success": False, "message": str(e)}


@mcp.tool()
def restore_option(ctx: Context, name: str) -> Dict[str, Any]:
    """
    Put a saved option back ("restore option A", "go back to A"): walls,
    doors and windows return to the option; floor, roof and rooms follow.
    One Undo. Refused with the reason when a wall or opening was added or
    removed since, or a door or window changed type.

    Returns name, walls, moved, resized, message.
    """
    return _named(name, lambda: get_rhino_connection().send_command("restore_option", {"name": name}))


@mcp.tool()
def rename_option(ctx: Context, name: str, to: str) -> Dict[str, Any]:
    """Rename a saved option ("call A wide hall"). The model does not change."""
    if not isinstance(to, str) or not _NAME.match(to):
        return {"success": False, "message": _BAD_NAME}
    return _named(name, lambda: get_rhino_connection().send_command("rename_option", {"name": name, "to": to}))


@mcp.tool()
def delete_option(ctx: Context, name: str) -> Dict[str, Any]:
    """Delete a saved option's file ("delete option B"). The model does not change."""
    return _named(name, lambda: get_rhino_connection().send_command("delete_option", {"name": name}))


def _named(name: Any, send: Callable[[], Dict[str, Any]]) -> Dict[str, Any]:
    """A command about one saved option: the name checked first, then the call."""
    try:
        if not isinstance(name, str) or not _NAME.match(name):
            return {"success": False, "message": _BAD_NAME}
        return {"success": True, **send()}
    except Exception as e:
        logger.error(f"Error in an option command: {str(e)}")
        return {"success": False, "message": str(e)}


@mcp.tool()
def compare_option(ctx: Context, name: str) -> Dict[str, Any]:
    """
    Compare a saved option with the model now ("compare with A"): what
    changed, then net area, each room's area and daylight, and the door and
    window counts side by side, the better daylight marked.

    Returns name, summary, rows (label, option, now, better), message.
    """
    try:
        if not isinstance(name, str) or not _NAME.match(name):
            return {"success": False, "message": _BAD_NAME}
        return {"success": True, **get_rhino_connection().send_command("compare_option", {"name": name})}
    except Exception as e:
        logger.error(f"Error in compare_option: {str(e)}")
        return {"success": False, "message": str(e)}
