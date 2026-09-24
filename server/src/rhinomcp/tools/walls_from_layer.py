"""Extrude closed curves on a plan layer to named wall solids."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, Dict, Any


@mcp.tool()
def walls_from_layer(
    ctx: Context,
    layer: str = "wall",
    height: float = 3000.0,
    target_layer: str = "A-WALL",
    name_prefix: str = "wall-",
    join_tolerance: Optional[float] = None,
    apply_default_materials: bool = True,
) -> Dict[str, Any]:
    """
    Build wall solids from closed polylines on a 2D plan layer.

    Uses the curves as drawn (double-line / room outlines). Does not
    centerline-offset. Nested closed curves become wall bands (outer minus
    inner). Disjoint closed curves extrude as-is. Source curves stay on
    their original layer. Does not create a roof, ceiling, or floor slab;
    rooms stay open at the top. Roof/ceiling/slab/floor layers are ignored.
    Layer X-EXIST returns count 0: existing underlay is not a bake source.

    New solids use MaterialFromLayer. With apply_default_materials (default
    true), the target layer gets plaster (M-PLASTER). Override later with
    set_layer_material (e.g. walls → wood). Leave defaults on unless asked.

    Parameters:
    - layer: Source layer name, case-insensitive (default "wall")
    - height: Extrusion height in document units, default 3000.
      Stamped on each wall as forsk:height, with forsk:level 0,
      forsk:thickness (measured band thickness, mm), and forsk:path
      (uncut plan outline). Bake order also stamps forsk:id w01, w02, …
      matching wall-01, wall-02. rebuild_host_wall reads that record.
    - target_layer: Layer for new solids, created if missing (default "A-WALL")
    - name_prefix: Name prefix, default "wall-" → wall-01, wall-02, …
    - join_tolerance: Optional join tolerance for open segments
    - apply_default_materials: Assign plaster By Layer (default true)

    Returns:
    Dictionary with ids (new GUIDs), forsk_ids (w01…), count, source_curves,
    warnings, message, and material_name when defaults were applied.

    Do not use this tool for furniture, doors, windows, roofs, or ceilings.
    """
    try:
        if height <= 0:
            return {"success": False, "message": "height must be positive"}

        rhino = get_rhino_connection()
        params = {
            "layer": layer,
            "height": height,
            "target_layer": target_layer,
            "name_prefix": name_prefix,
            "apply_default_materials": apply_default_materials,
        }
        if join_tolerance is not None:
            params["join_tolerance"] = join_tolerance

        result = rhino.send_command("walls_from_layer", params)
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
            "material_name": result.get("material_name"),
            "message": result.get("message", "Walls created"),
        }
    except Exception as e:
        logger.error(f"Error in walls_from_layer: {str(e)}")
        return {"success": False, "message": str(e)}
