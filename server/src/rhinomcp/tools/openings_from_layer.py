"""Cut door/window openings from wall solids using plan-layer geometry."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, List, Dict, Any


@mcp.tool()
def openings_from_layer(
    ctx: Context,
    layer: str,
    sill: Optional[float] = None,
    head: Optional[float] = None,
    target_layer: str = "A-WALL",
    wall_ids: Optional[List[str]] = None,
    limit: Optional[int] = None,
    pad: float = 50.0,
    min_depth: float = 250.0,
) -> Dict[str, Any]:
    """
    Cut openings through wall solids from door or window plan geometry.

    Door: opening 0 → 2100 (height 2100). Width from the gap polyline;
    door swing blocks/arcs are ignored when gap polylines exist.
    Window: sill 900, head 2100 (height 1200). Uses window-layer curves
    or block-instance bounding boxes. Creates selectable opening_marker
    boxes on A-OPEN (named door-NN / window-NN). Door/window blocks are
    not created.

    Parameters:
    - layer: Source layer, typically "door" or "window" (case-insensitive)
    - sill: Optional bottom Z. Default 0 for door, 900 for window
    - head: Optional top Z. Default 2100
    - target_layer: Layer of wall solids to cut (default "A-WALL")
    - wall_ids: Optional GUIDs of walls to cut; otherwise all solids on target_layer
    - limit: Optional cap on openings (hero-only if full-file booleans are unstable)
    - pad: Extra width on the cutter (default 50)
    - min_depth: Minimum cutter depth through the wall (default 250)

    Returns:
    Dictionary with cut_count, failed_count, failures, wall_ids, marker_ids, message.
    """
    try:
        if not layer:
            return {"success": False, "message": "layer is required"}
        if limit is not None and limit < 1:
            return {"success": False, "message": "limit must be >= 1"}

        rhino = get_rhino_connection()
        params = {
            "layer": layer,
            "target_layer": target_layer,
            "pad": pad,
            "min_depth": min_depth,
        }
        if sill is not None:
            params["sill"] = sill
        if head is not None:
            params["head"] = head
        if wall_ids:
            params["wall_ids"] = wall_ids
        if limit is not None:
            params["limit"] = limit

        result = rhino.send_command("openings_from_layer", params)
        out = {
            "success": True,
            "cut_count": result.get("cut_count", 0),
            "failed_count": result.get("failed_count", 0),
            "failures": result.get("failures", []),
            "wall_ids": result.get("wall_ids", []),
            "opening_count": result.get("opening_count"),
            "sill": result.get("sill"),
            "head": result.get("head"),
            "message": result.get("message", "Openings cut"),
        }
        if "marker_ids" in result:
            out["marker_ids"] = result.get("marker_ids") or []
        return out
    except Exception as e:
        logger.error(f"Error in openings_from_layer: {str(e)}")
        return {"success": False, "message": str(e)}
