"""Plan import: a plan image as an underlay with its detection as 2D review geometry."""

import os
from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def plan_import(
    ctx: Context,
    image_path: str = "",
    plan_path: str = "",
    pdf_path: str = "",
    page: Optional[int] = None,
    scale_hint: Optional[str] = None,
    image_dpi: Optional[float] = None,
    image_width_mm: Optional[float] = None,
    replace: bool = False,
) -> Dict[str, Any]:
    """
    Import a floor plan in one undoable step: a vector PDF, or a plan image
    with a detection of it.

    A vector PDF (pdf_path, with page when it has several) is read by forsk's
    extractor (tools/pdf_vector, run with uv): its walls, doors, windows, rooms
    and the scale printed on the sheet, and the page rendered in the same
    frame. The detected scale is applied; the receipt names it, and the user
    confirms or overrides it with plan_scale. A page with no vector walls (a
    scan) imports nothing and says so, with the next step: a Tectly detection
    of the page image through plan_from_tectly.

    The image goes in as a locked, faded underlay on X-PLAN, its top-left at
    (0, 0), shown in every view's display mode and kept off Print. The
    detection (a forsk.plan_import.v0 JSON: mm, y up; from Tectly through
    plan_from_tectly, or from a vector-PDF extractor) goes on top as 2D review
    geometry on the layers the bake reads: a closed outline per connected run
    of walls on wall (the walls merged, so none overlaps another; the holes an
    outline closes are loops of their own), opening footprints on door and
    window, room outlines on A-ROOM and their names on label (Rom for a room
    the detection could not name). Nothing is 3D until the user asks to
    generate. The bake makes one wall solid of each outline. A wall the user
    draws afterwards merges with the outlines it touches at the bake; a wall
    run standing free bakes as a wall on its own.

    Clean-up: walls within 3 degrees of an axis are squared (diagonals stay),
    thickness is rounded to 10 mm, collinear pieces merge (through an opening
    too), each opening snaps to its nearest wall, wall ends run into the walls
    they stop at. An end that stops short of a wall by less than the thicker
    of the two walls is a gap in the detection: closed, and listed in review.
    A wider gap stays open. What it could not place is reported, not hidden.

    Parameters:
    - pdf_path: absolute path of a vector PDF plan, in place of image_path
      and plan_path
    - page: the PDF's page, 1-based; needed when it has more than one
    - image_path: absolute path of the plan image (PNG or JPEG)
    - plan_path: absolute path of the forsk.plan_import.v0 JSON
    - scale_hint: the drawing's scale when known, such as "1:100". It sizes the
      image and is named in the receipt; the user still sets the scale with
      plan_scale
    - image_dpi: image resolution when the file does not carry it
    - image_width_mm: width of the image on the plan in mm; wins over dpi and scale
    - replace: swap an earlier import instead of refusing

    Returns:
    Dictionary with walls, walls_detected, merged, squared, diagonal, joined,
    gaps_closed, extended, doors, windows, loose (openings with no wall),
    uncut (openings the bake will not cut), outlines (the merged wall outlines
    drawn, and what the bake extrudes: one wall solid, floor and roof each),
    outline_holes, wall_pieces (detected pieces merged into the outlines),
    overlaps (outlines still overlapping another: 0), free_walls (outlines standing free
    of the rest), blocks_skipped (of those, the ones too wide to be a wall,
    which the bake skips), rooms, unlabelled, outside (rooms the walls do not
    close around), dropped, review (one line per thing to look at), scale
    (status, ratio, factor), underlay, pdf (with a PDF: the page read and
    the plan file and page image written for it), and message.
    Always pass on the scale status and the review lines.
    """
    try:
        if pdf_path and pdf_path.strip():
            if (image_path and image_path.strip()) or (plan_path and plan_path.strip()):
                return {"success": False, "message": "plan_import takes pdf_path, or image_path with plan_path, not both."}
            if not os.path.isabs(pdf_path.strip()):
                return {"success": False, "message": "plan_import needs an absolute pdf_path."}
            params: Dict[str, Any] = {"pdf_path": pdf_path.strip()}
            if page is not None:
                if page < 1:
                    return {"success": False, "message": "plan_import page is 1-based."}
                params["page"] = page
        else:
            for label, path in (("image_path", image_path), ("plan_path", plan_path)):
                if not isinstance(path, str) or not path.strip() or not os.path.isabs(path.strip()):
                    return {"success": False, "message": f"plan_import needs an absolute {label}, or a pdf_path."}
            params = {"image_path": image_path.strip(), "plan_path": plan_path.strip()}
        if scale_hint:
            params["scale_hint"] = scale_hint
        if image_dpi:
            params["image_dpi"] = image_dpi
        if image_width_mm:
            params["image_width_mm"] = image_width_mm
        if replace:
            params["replace"] = True
        result = get_rhino_connection().send_command("plan_import", params)
        return {"success": True, **result}
    except Exception as e:
        logger.error(f"Error in plan_import: {str(e)}")
        return {"success": False, "message": str(e)}
