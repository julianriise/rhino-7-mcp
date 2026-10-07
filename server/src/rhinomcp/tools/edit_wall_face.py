"""Edit one straight wall run by a face: an end changes the length, a side the place or thickness."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations
from rhinomcp.server import get_rhino_connection, mcp, logger
from rhinomcp.args import is_pair


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def edit_wall_face(
    ctx: Context,
    at: List[float],
    distance_mm: Optional[float] = None,
    thickness_mm: Optional[float] = None,
    face: Optional[str] = None,
    id: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Edit one straight wall run by one of its faces ("make this wall 800 mm
    longer", "make the north wall 250 thick"), then rebuild the wall records
    it changed. at picks the nearest face within 300 mm: an end face where
    the wall stands free, or a side face.

    distance_mm on an end lengthens the wall (below 0 shortens); the other
    end and the openings keep their place, and a wall left too short for an
    opening is refused. distance_mm on a side moves the run out through that
    face (below 0: in), as move_wall. thickness_mm on a side makes the run
    that thick by moving that face alone; the face across stays, and the
    walls that meet the moved face follow. Floor, roof and rooms follow.
    Refuses X-EXIST.

    Parameters:
    - at: [x, y] in mm on the face.
    - distance_mm: Signed mm out through the face. Exclusive with thickness_mm.
    - thickness_mm: The new thickness in mm, above 0. A side face only.
    - face: end or side, to pick only that kind of face.
    - id: Wall GUID. Omit to use the selected wall, else the wall nearest at.

    Returns:
    Dictionary with host_id, wall, face, length_mm, thickness, followed, ok, message.
    """
    try:
        if not is_pair(at):
            return {"success": False, "message": "at is [x, y] in mm."}
        if (distance_mm is None) == (thickness_mm is None):
            return {"success": False, "message": "Give distance_mm or thickness_mm, not both."}
        for value in (distance_mm, thickness_mm):
            if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float))):
                return {"success": False, "message": "distance_mm and thickness_mm are numbers in mm."}
        if thickness_mm is not None and thickness_mm <= 0:
            return {"success": False, "message": "thickness_mm must be positive."}
        if face is not None and face not in ("end", "side"):
            return {"success": False, "message": "face is end or side."}
        if thickness_mm is not None and face == "end":
            return {"success": False, "message": "An end face changes the length: give distance_mm."}

        params: Dict[str, Any] = {"at": at}
        if distance_mm is not None:
            params["distance_mm"] = distance_mm
        if thickness_mm is not None:
            params["thickness_mm"] = thickness_mm
        if face is not None:
            params["face"] = face
        if id:
            params["id"] = id

        result = get_rhino_connection().send_command("edit_wall_face", params)
        return {
            "success": True,
            "host_id": result.get("host_id"),
            "forsk_id": result.get("forsk_id"),
            "wall": result.get("wall"),
            "face": result.get("face"),
            "length_mm": result.get("length_mm"),
            "thickness": result.get("thickness"),
            "openings_moved": result.get("openings_moved", []),
            "followed": result.get("followed", []),
            "records": result.get("records", []),
            "rebuilt": result.get("rebuilt", ""),
            "host_openings": result.get("host_openings"),
            "host_voids": result.get("host_voids"),
            "ok": result.get("ok", True),
            "message": result.get("message", ""),
        }
    except Exception as e:
        logger.error(f"Error in edit_wall_face: {str(e)}")
        return {"success": False, "message": str(e)}
