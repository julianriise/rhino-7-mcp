"""Create a flat roof slab above Forsk walls from the wall outline."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool()
def roof_flat_from_walls(
    ctx: Context,
    layer: str = "wall",
    thickness: float = 200.0,
    overhang: float = 0.0,
    elevation: Optional[float] = None,
    target_layer: str = "A-ROOF",
    name_prefix: str = "roof-",
    join_tolerance: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Build a flat roof slab from the outermost closed curve on the wall
    source layer (same footprint path as floor_from_layer).

    Requires Forsk walls (`forsk:kind=wall` or solids on A-WALL). Slab top
    at max wall bbox Max.Z, or `elevation` when set. Extrudes downward by
    `thickness` so wall tops meet the underside. Default thickness 200,
    overhang 0. Constant WorldXY overhang only.

    Parameters:
    - layer: Fallback outline layer when walls lack forsk:source_layer (default "wall")
    - thickness: Slab thickness in document units, default 200
    - overhang: Constant outline offset, default 0
    - elevation: Optional absolute Z for the slab top
    - target_layer: Layer for new slabs, created if missing (default "A-ROOF")
    - name_prefix: Name prefix, default "roof-" → roof-01, …
    - join_tolerance: Optional join tolerance for open segments

    Returns:
    Dictionary with ids, count, kind, roof_type, thickness, overhang,
    warnings, message, and bbox when count > 0.
    """
    try:
        if thickness <= 0:
            return {"success": False, "message": "thickness must be positive"}
        if overhang < 0:
            return {"success": False, "message": "overhang must be >= 0"}

        rhino = get_rhino_connection()
        params = {
            "layer": layer,
            "thickness": thickness,
            "overhang": overhang,
            "target_layer": target_layer,
            "name_prefix": name_prefix,
        }
        if elevation is not None:
            params["elevation"] = elevation
        if join_tolerance is not None:
            params["join_tolerance"] = join_tolerance

        result = rhino.send_command("roof_flat_from_walls", params)
        payload = {
            "success": True,
            "ids": result.get("ids", []),
            "count": result.get("count", 0),
            "kind": result.get("kind", "roof"),
            "roof_type": result.get("roof_type", "flat"),
            "source_curves": result.get("source_curves"),
            "joined": result.get("joined"),
            "closed": result.get("closed"),
            "skipped": result.get("skipped"),
            "thickness": result.get("thickness", thickness),
            "overhang": result.get("overhang", overhang),
            "warnings": result.get("warnings", []),
            "message": result.get("message", "Roof created"),
        }
        if "bbox" in result:
            payload["bbox"] = result.get("bbox")
        return payload
    except Exception as e:
        logger.error(f"Error in roof_flat_from_walls: {str(e)}")
        return {"success": False, "message": str(e)}
