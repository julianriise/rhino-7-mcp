"""Raw Tectly floorplan JSON to forsk.plan_import.v0, the file plan_import reads."""

import os
from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import mcp, logger

import forsk_plan_import


@mcp.tool()
def plan_from_tectly(
    ctx: Context,
    raw_path: str = "",
    out_path: str = "",
    crop_size: Optional[List[float]] = None,
    dpi: float = 0,
    crop_offset: Optional[List[float]] = None,
    page_size: Optional[List[float]] = None,
    scale: float = 100,
) -> Dict[str, Any]:
    """
    Convert a raw Tectly floorplan-recognition result to forsk.plan_import.v0,
    the file plan_import reads. Local files only; no network and no Rhino.

    Parameters:
    - raw_path: absolute path of the JSON the Tectly API returned
    - out_path: absolute path to write the forsk.plan_import.v0 JSON
    - crop_size: [width, height] in px of the image Tectly was sent
    - dpi: resolution of that image
    - crop_offset: [x, y] in px of that image's top-left on the full page
      raster; [0, 0] when the whole page was sent
    - page_size: [width, height] in px of the full page raster, so the plan
      file states how wide the page image is
    - scale: the drawing's scale denominator, 100 for 1:100. An assumption
      until the user sets the scale with plan_scale

    Returns:
    Dictionary with walls, openings, rooms (counts), scale, out_path, and message.
    """
    try:
        for label, path in (("raw_path", raw_path), ("out_path", out_path)):
            if not isinstance(path, str) or not path.strip() or not os.path.isabs(path.strip()):
                return {"success": False, "message": f"plan_from_tectly needs an absolute {label}."}
        if not crop_size:
            return {"success": False, "message": "plan_from_tectly needs crop_size [width, height] in px."}
        plan = forsk_plan_import.convert_tectly_file(
            raw_path.strip(), out_path.strip(),
            crop_size=crop_size, crop_offset=crop_offset or (0, 0),
            page_size=page_size, dpi=dpi, scale=scale,
            source_file=os.path.basename(raw_path.strip()))
        counts = {key: len(plan[key]) for key in ("walls", "openings", "rooms")}
        return {
            "success": True,
            **counts,
            "scale": plan["scale"],
            "out_path": out_path.strip(),
            "message": "Wrote %(walls)d walls, %(openings)d openings, %(rooms)d rooms" % counts
                       + " to %s. Scale %s is assumed." % (os.path.basename(out_path.strip()), plan["scale"]["ratio"]),
        }
    except Exception as e:
        logger.error(f"Error in plan_from_tectly: {str(e)}")
        return {"success": False, "message": str(e)}
