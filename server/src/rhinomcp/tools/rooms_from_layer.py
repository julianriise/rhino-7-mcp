"""Planar room markers from closed curves on A-ROOM."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool()
def rooms_from_layer(
    ctx: Context,
    layer: str = "A-ROOM",
    target_layer: str = "A-ROOM",
    name_prefix: str = "room-",
    join_tolerance: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Build selectable planar room markers from closed curves on a room layer.

    Default source is A-ROOM. Layer name room is an alias for A-ROOM when
    the other is missing. Each closed curve becomes one marker named
    room-01… with forsk:kind=room, forsk:id r01…, and forsk:area in mm².
    Source curves stay. Markers are tagged forsk:generated=1 so
    clear_generated removes them. A missing or empty layer returns count 0.

    Does not detect rooms from walls and does not extrude to wall height.

    Parameters:
    - layer: Source layer, case-insensitive (default "A-ROOM")
    - target_layer: Layer for new markers, created if missing (default "A-ROOM")
    - name_prefix: Name prefix, default "room-" → room-01, …
    - join_tolerance: Optional join tolerance for open segments

    Returns:
    Dictionary with ids, forsk_ids, count, warnings, and message.
    """
    try:
        rhino = get_rhino_connection()
        params = {
            "layer": layer,
            "target_layer": target_layer,
            "name_prefix": name_prefix,
        }
        if join_tolerance is not None:
            params["join_tolerance"] = join_tolerance

        result = rhino.send_command("rooms_from_layer", params)
        return {
            "success": True,
            "ids": result.get("ids", []),
            "forsk_ids": result.get("forsk_ids", []),
            "count": result.get("count", 0),
            "source_curves": result.get("source_curves"),
            "joined": result.get("joined"),
            "closed": result.get("closed"),
            "skipped": result.get("skipped"),
            "warnings": result.get("warnings", []),
            "message": result.get("message", "Rooms created"),
        }
    except Exception as e:
        logger.error(f"Error in rooms_from_layer: {str(e)}")
        return {"success": False, "message": str(e)}
