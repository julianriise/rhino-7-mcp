"""Create a floor slab below plan Z from the outermost wall outline."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool()
def floor_from_layer(
    ctx: Context,
    layer: str = "wall",
    thickness: float = 400.0,
    target_layer: str = "A-FLOR",
    name_prefix: str = "floor-",
    join_tolerance: Optional[float] = None,
    apply_default_materials: bool = True,
) -> Dict[str, Any]:
    """
    Build a floor slab from the outermost closed curve on a 2D plan layer.

    Extrudes downward by `thickness` so the top of the slab sits on the 2D
    plan Z (the drawing does not move). Default thickness 400. Uses the wall
    outline as the footprint. Does not create a roof or ceiling. CAD layers
    named floor/roof/ceiling/slab are not used as source. Layer X-EXIST
    returns count 0: existing underlay is not a bake source.

    New slabs use MaterialFromLayer. With apply_default_materials (default
    true), the target layer gets concrete (M-CONCRETE). Override later with
    set_layer_material. Leave defaults on unless asked.

    Parameters:
    - layer: Source layer for the outline, case-insensitive (default "wall")
    - thickness: Slab thickness in document units, default 400
    - target_layer: Layer for new slabs, created if missing (default "A-FLOR")
    - name_prefix: Name prefix, default "floor-" → floor-01, …
    - join_tolerance: Optional join tolerance for open segments
    - apply_default_materials: Assign concrete By Layer (default true)

    Returns:
    Dictionary with ids, count, thickness, warnings, message, and
    material_name when defaults were applied.
    """
    try:
        if thickness <= 0:
            return {"success": False, "message": "thickness must be positive"}

        rhino = get_rhino_connection()
        params = {
            "layer": layer,
            "thickness": thickness,
            "target_layer": target_layer,
            "name_prefix": name_prefix,
            "apply_default_materials": apply_default_materials,
        }
        if join_tolerance is not None:
            params["join_tolerance"] = join_tolerance

        result = rhino.send_command("floor_from_layer", params)
        return {
            "success": True,
            "ids": result.get("ids", []),
            "count": result.get("count", 0),
            "source_curves": result.get("source_curves"),
            "joined": result.get("joined"),
            "closed": result.get("closed"),
            "skipped": result.get("skipped"),
            "thickness": result.get("thickness", thickness),
            "warnings": result.get("warnings", []),
            "material_name": result.get("material_name"),
            "message": result.get("message", "Floor created"),
        }
    except Exception as e:
        logger.error(f"Error in floor_from_layer: {str(e)}")
        return {"success": False, "message": str(e)}
