"""Assign a matte RenderMaterial to a layer (By Layer)."""

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Optional, List, Dict, Any


PRESET_ALIASES = {
    "oak": "wood",
    "terracotta": "clay",
}

ALLOWED_PRESETS = {"plaster", "concrete", "clay", "wood", "white"}

LAYER_ALIASES = {
    "wall": "A-WALL",
    "walls": "A-WALL",
    "a-wall": "A-WALL",
    "floor": "A-FLOR",
    "floors": "A-FLOR",
    "slab": "A-FLOR",
    "a-flor": "A-FLOR",
}


def _canonical_preset(preset: Optional[str]) -> Optional[str]:
    if preset is None:
        return None
    key = preset.strip().lower()
    if not key:
        return None
    return PRESET_ALIASES.get(key, key)


def _canonical_layer(layer_name: str) -> str:
    key = layer_name.strip()
    return LAYER_ALIASES.get(key.lower(), key)


@mcp.tool()
def set_layer_material(
    ctx: Context,
    layer_name: str,
    preset: Optional[str] = None,
    material_name: Optional[str] = None,
    name: Optional[str] = None,
    diffuse_rgb: Optional[List[int]] = None,
    ensure_objects_from_layer: bool = True,
) -> Dict[str, Any]:
    """
    Assign a render material to a layer. Objects on that layer use By Layer
    (MaterialFromLayer). Prefer this over per-object material_index.

    Chat overrides:
    - "Make walls wood" → layer_name="A-WALL", preset="wood"
    - "Set floor to white" → layer_name="A-FLOR", preset="white"
    - "Walls plaster again" → layer_name="A-WALL", preset="plaster"

    Presets (matte architectural): plaster, concrete, clay, wood, white.
    Aliases: oak→wood, terracotta→clay. Casual layer words walls/floor map
    to A-WALL / A-FLOR.

    Parameters:
    - layer_name: Layer to assign (A-WALL, A-FLOR, or walls/floor aliases)
    - preset: plaster | concrete | clay | wood | white
    - material_name: Existing RenderMaterial name to reuse
    - name: New material name when using diffuse_rgb
    - diffuse_rgb: Custom matte diffuse [r, g, b] 0–255 (requires name)
    - ensure_objects_from_layer: Set all objects on the layer to By Layer (default true)

    After a material change, suggest Rendered display mode (shows the
    diffuse). Arctic is a clay/white look that hides material color.
    """
    try:
        if not layer_name or not str(layer_name).strip():
            return {"success": False, "message": "layer_name is required"}

        canonical_preset = _canonical_preset(preset)
        if canonical_preset is not None and canonical_preset not in ALLOWED_PRESETS:
            return {
                "success": False,
                "message": (
                    f"Unknown preset '{preset}'. Use plaster, concrete, clay, wood, or white."
                ),
            }

        has_custom = diffuse_rgb is not None and name is not None and str(name).strip()
        if canonical_preset is None and not material_name and not has_custom:
            return {
                "success": False,
                "message": "Provide preset, material_name, or diffuse_rgb + name.",
            }

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "layer_name": _canonical_layer(layer_name),
            "ensure_objects_from_layer": ensure_objects_from_layer,
        }
        if canonical_preset is not None:
            params["preset"] = canonical_preset
        elif material_name:
            params["material_name"] = material_name
        else:
            params["name"] = name
            params["diffuse_rgb"] = diffuse_rgb

        result = rhino.send_command("set_layer_material", params)
        return {
            "success": True,
            "layer": result.get("layer"),
            "material_name": result.get("material_name"),
            "material_id": result.get("material_id"),
            "objects_updated": result.get("objects_updated", 0),
            "message": result.get("message", "Layer material set"),
        }
    except Exception as e:
        logger.error(f"Error in set_layer_material: {str(e)}")
        return {"success": False, "message": str(e)}
