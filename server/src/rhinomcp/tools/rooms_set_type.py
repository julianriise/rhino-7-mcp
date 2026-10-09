"""Set the room type of the selected rooms, or of one named room, from the chat."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def rooms_set_type(
    ctx: Context,
    room_type: str,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Set the room type of every selected room (or the named one) and mark the source user, so a later guess cannot replace it.

    room_type is one of: unassigned, living, kitchen, dining, bedroom, bathroom,
    wc, hall, storage, laundry, technical, office, garage, balcony, stair.
    A label word such as soverom or kjøkken is accepted and stored as the key.

    id is a GUID, a forsk id (rd-01), or the room name. Omit it to use the
    selected room. Every selected room is set.

    Empty selection refuses with:
    Nothing is selected. Click the room in Rhino, then say it again.
    """
    try:
        if not isinstance(room_type, str) or not room_type.strip():
            return {"success": False, "message": "room_type is required."}
        params: Dict[str, Any] = {"room_type": room_type.strip()}
        if id is not None and str(id).strip():
            params["id"] = str(id).strip()
        result = get_rhino_connection().send_command("rooms_set_type", params)
        return {
            "success": True,
            "id": result.get("id", ""),
            "room_type": result.get("room_type", ""),
            "room_type_source": result.get("room_type_source", "user"),
            "message": result.get("message", "Room type set"),
        }
    except Exception as e:
        logger.error(f"Error in rooms_set_type: {str(e)}")
        return {"success": False, "message": str(e)}
