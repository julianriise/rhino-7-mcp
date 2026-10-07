"""Furnish a room by its type, deterministically."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger

DENSITIES = ("spacious", "relaxed", "compact")
VARIANTS = ("consistent", "creative")


@mcp.tool()
def furnish_room(
    ctx: Context,
    room: Optional[str] = None,
    density: Optional[str] = None,
    variant: Optional[str] = None,
    replace: Optional[bool] = None,
    preview: Optional[bool] = None,
) -> Dict[str, Any]:
    """
    Furnish a room by its type ("furnish the bedroom", "furnish the house",
    "møbler kjøkkenet"): rules, not guesses, so the same room gives the
    same layout. Bedroom: bed, bedside tables, wardrobe. Living: sofa
    looking at the window, coffee table, armchair, TV bench, bookshelf.
    Dining: table with a chair at every seat. Kitchen: a run of fridge,
    sink, dishwasher and hob on a wall without a door, wall units, a table
    when the floor allows. Bathroom: shower or bath, WC, basin, washing
    machine. WC, office, hall, laundry and storage too. Door swings and the
    floor in front of every piece stay clear.

    Parameters:
    - room: id, name or type, or "all" for every room of a type Forsk
      furnishes. Omit for the picked room, else the only one.
    - density: spacious, relaxed (default) or compact.
    - variant: consistent (default) or creative (the main piece takes the
      next-best wall). Offer creative when the user wants another layout.
    - replace: clear the room's furniture first, keeping pieces the user
      moved by hand. Without it, pieces already in the room stay and a kind
      the room has is not added again.

    - preview: show both layouts as ghosts in the viewport (usual in blue,
      creative in orange) and place nothing; place one with variant after.

    A room that cannot be furnished is refused with the reason; say it.
    Returns rooms (room, type, added, skipped, why), count, message.
    """
    try:
        if density is not None and density not in DENSITIES:
            return {"success": False, "message": "density is spacious, relaxed or compact."}
        if variant is not None and variant not in VARIANTS:
            return {"success": False, "message": "variant is consistent or creative."}
        params: Dict[str, Any] = {}
        for key, value in (("room", room), ("density", density), ("variant", variant)):
            if value:
                params[key] = value
        if replace:
            params["replace"] = True
        if preview:
            params["preview"] = True
        result = get_rhino_connection().send_command("furnish_room", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in furnish_room: {str(e)}")
        return {"success": False, "message": str(e)}
