"""Add one piece of furniture from the catalogue, placed in a room."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from rhinomcp.args import is_mm, is_pair


@mcp.tool()
def add_furniture(
    ctx: Context,
    item: str,
    width: Optional[float] = None,
    seats: Optional[int] = None,
    room: Optional[str] = None,
    at: Optional[List[float]] = None,
    rotation: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Add one piece of furniture ("put a double bed in the bedroom", "add a
    sofa here", "sett inn et toalett på badet"): a 3D block from Forsk's
    catalogue, drawn on plan sheets as its 2D symbol. One piece per call;
    call again for each piece.

    The catalogue (residential): bed (single 90/120, double 140/160/180),
    bedside table, wardrobe (50/100), desk (120/140), sofa (2/3 seats),
    armchair, coffee table, TV bench, bookshelf, dining table (4/6/8 seats,
    round 4/6), chair, kitchen base/tall/wall unit, sink unit, hob, fridge,
    dishwasher, worktop (120/240), kitchen island, WC, basin, shower (80/90),
    bath, washing machine, office chair, shelf, shoe cabinet, coat rack,
    bench. No lamps, rugs, plants or decor.

    Parameters:
    - item: a catalogue id (bed.double.160x200) or words (double bed, toilet).
    - width: nominal width, cm under 300 else mm; picks the nearest size.
    - seats: for sofas and tables; picks the nearest size.
    - room: the room's id (R02), name, or type when only one room has it.
      Omitted: the room holding at, else the picked room, else the only one.
    - at: [x, y] mm. A wall piece takes the wall nearest to it; a free piece
      stands there. With rotation (degrees of the back edge from x), the
      piece's centre goes exactly there.

    A wall piece stands back to the longest free wall, centred, sliding
    along it; tables, chairs and the island stand in the middle. A piece
    that does not fit is refused with the reason; say that to the user.
    Returns id, forsk_id (F01), catalog_id, name, room, centre, rotation,
    size, message.
    """
    try:
        if not isinstance(item, str) or not item.strip():
            return {"success": False, "message": "Say which piece: a bed, a sofa, a WC …"}
        if width is not None and not is_mm(width):
            return {"success": False, "message": "width must be a positive number."}
        if seats is not None and (isinstance(seats, bool) or not isinstance(seats, int) or seats < 1):
            return {"success": False, "message": "seats is a whole number from 1."}
        if at is not None and not is_pair(at):
            return {"success": False, "message": "at is [x, y] in mm."}
        if rotation is not None and at is None:
            return {"success": False, "message": "rotation needs at."}

        params: Dict[str, Any] = {"item": item.strip()}
        for key, value in (("width", width), ("seats", seats), ("room", room), ("at", at), ("rotation", rotation)):
            if value is not None:
                params[key] = value
        result = get_rhino_connection().send_command("add_furniture", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in add_furniture: {str(e)}")
        return {"success": False, "message": str(e)}
