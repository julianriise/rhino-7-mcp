"""Forsk plan import: a vendor's floor-plan detection -> forsk.plan_import.v0.

forsk.plan_import.v0 is the vendor-neutral file the plan_import tool reads:

    schema   "forsk.plan_import.v0", units "mm", y_axis "up"
    image    {width_mm, height_mm}: the page the detection was made from, its
             top-left at (0, 0), so the page spans y 0 down to -height_mm
    scale    {status, ratio}: "detected" when the source read it off the plan,
             anything else for a scale nobody measured (the user sets it in
             Rhino with two points and a known length)
    walls    [{start, end, thickness}]: centreline and thickness
    openings [{kind: "window", a, b}] and
             [{kind: "door", hinge, closed, open, width}]
    rooms    [{label, ocr_text, source_type, area_m2, tag, boundary}]

Sources are interchangeable: anything that writes this file imports the same
way. This module holds the Tectly adapter. Pure Python, no network: it reads
the JSON the Tectly API returned, not the API.

Tectly answers in coordinates normalised to the plan area it found inside the
image it was sent (pageSection), y down. The image may be a crop of the page,
so the adapter takes the crop's size and its offset on the page raster, the
raster's dpi, and the drawing's scale.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from typing import Any, Dict, List, Optional, Sequence

SCHEMA = "forsk.plan_import.v0"

# Tectly's room types in Norwegian. Other has no name of its own.
ROOM_NAMES = {
    "Bedroom": "Soverom",
    "LivingRoom": "Stue",
    "Bathroom": "Bad",
    "Storage": "Bod",
    "Corridor": "Gang",
    "Kitchen": "Kjøkken",
}
NO_NAME = "Rom"


class PlanImportError(ValueError):
    """The detection cannot be turned into a plan."""


def tectly_to_plan(
    raw: Dict[str, Any],
    *,
    crop_size: Sequence[float],
    dpi: float,
    crop_offset: Sequence[float] = (0, 0),
    page_size: Optional[Sequence[float]] = None,
    scale: float = 100.0,
    plan_index: int = 0,
    source_file: Optional[str] = None,
) -> Dict[str, Any]:
    """Raw Tectly JSON -> forsk.plan_import.v0.

    crop_size: (width, height) in px of the image Tectly was sent.
    crop_offset: (x, y) in px of that image's top-left on the full page raster.
    page_size: (width, height) in px of the full page raster; gives image.width_mm.
    dpi: resolution of the raster. scale: the drawing's scale denominator (100 for 1:100).
    """
    if dpi <= 0 or scale <= 0:
        raise PlanImportError("dpi and scale must be above 0.")
    if len(crop_size) != 2 or min(crop_size) <= 0:
        raise PlanImportError("crop_size must be (width, height) in px.")
    plans = raw.get("plans") if isinstance(raw.get("plans"), list) else [raw]
    if plan_index >= len(plans) or "walls" not in plans[plan_index]:
        raise PlanImportError("The Tectly result has no plan %d with walls." % plan_index)
    plan = plans[plan_index]

    section = (plan.get("plan") or {}).get("pageSection") or {}
    left, top = section.get("left", 0.0), section.get("top", 0.0)
    width, height = section.get("width", 1.0), section.get("height", 1.0)
    mm_per_px = 25.4 / dpi * scale

    def mm(point: Sequence[float]) -> List[int]:
        x = (left + point[0] * width) * crop_size[0] + crop_offset[0]
        y = (top + point[1] * height) * crop_size[1] + crop_offset[1]
        return [round(x * mm_per_px), round(-y * mm_per_px)]

    walls = []
    for wall in plan.get("walls") or []:
        corners = [mm(p) for p in wall["boundary"]][:4]
        if len(corners) < 4:
            continue
        # A traced rectangle: the long sides run along the wall, the short ones are its thickness.
        if math.dist(corners[0], corners[1]) >= math.dist(corners[1], corners[2]):
            start, end = _mid(corners[0], corners[3]), _mid(corners[1], corners[2])
            thickness = math.dist(corners[1], corners[2])
        else:
            start, end = _mid(corners[0], corners[1]), _mid(corners[3], corners[2])
            thickness = math.dist(corners[0], corners[1])
        walls.append({
            "start": [round(start[0]), round(start[1])],
            "end": [round(end[0]), round(end[1])],
            "thickness": round(thickness, 1),
        })

    openings = []
    for opening in plan.get("openings") or []:
        details = opening.get("details") or {}
        kind = str(details.get("type", "")).lower()
        if "from" in details and "to" in details:
            openings.append({"kind": kind or "window", "a": mm(details["from"]), "b": mm(details["to"])})
        elif "hinge" in details and "closed" in details:
            hinge, closed = mm(details["hinge"]), mm(details["closed"])
            door = {"kind": kind or "door", "hinge": hinge, "closed": closed, "width": round(math.dist(hinge, closed))}
            if "open" in details:
                door["open"] = mm(details["open"])
            openings.append(door)

    rooms = []
    for room in plan.get("rooms") or []:
        boundary = [mm(p) for p in room.get("boundary") or []]
        if len(boundary) < 3:
            continue
        caption = room.get("caption") or None
        area = abs(_area(boundary)) / 1e6
        rooms.append({
            "label": caption or ROOM_NAMES.get(room.get("type")) or NO_NAME,
            "source_type": room.get("type"),
            "ocr_text": caption,
            "area_m2": round(area, 1),
            "tag": "ca. " + ("%.1f" % area).replace(".", ",") + " m²",
            "boundary": boundary,
        })

    ratio = "1:%g" % scale
    found_scale = (plan.get("plan") or {}).get("horizontalScaleProcessingStatus") == "Positive"
    out: Dict[str, Any] = {
        "schema": SCHEMA,
        "units": "mm",
        "y_axis": "up",
        "origin": "page top-left at (0, 0); the user places and scales it in Rhino",
        "source": {"vendor": "tectly", "file": source_file, "wall_tracing": "Rectangles"},
        "scale": {
            "status": "assumed",
            "ratio": ratio,
            "note": "mm here assume %s at %g dpi. %s In Forsk the scale comes from the two-point step." % (
                ratio, dpi,
                "Tectly found a scale on this plan, which the adapter does not read yet." if found_scale
                else "Tectly found no scale on this plan."),
        },
    }
    if page_size is not None:
        if len(page_size) != 2 or min(page_size) <= 0:
            raise PlanImportError("page_size must be (width, height) in px.")
        out["image"] = {
            "width_mm": round(page_size[0] * mm_per_px, 1),
            "height_mm": round(page_size[1] * mm_per_px, 1),
        }
    out["walls"] = walls
    out["openings"] = openings
    out["rooms"] = rooms
    return out


def _mid(a: Sequence[float], b: Sequence[float]) -> List[float]:
    return [(a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0]


def _area(ring: Sequence[Sequence[float]]) -> float:
    total = 0.0
    for i, a in enumerate(ring):
        b = ring[(i + 1) % len(ring)]
        total += a[0] * b[1] - b[0] * a[1]
    return total / 2.0


def convert_tectly_file(raw_path: str, out_path: str, **frame: Any) -> Dict[str, Any]:
    """Read raw Tectly JSON, write forsk.plan_import.v0, return the plan."""
    with open(raw_path, encoding="utf-8") as handle:
        raw = json.load(handle)
    plan = tectly_to_plan(raw, **frame)
    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump(plan, handle, indent=1, ensure_ascii=False)
    return plan


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Raw Tectly JSON -> forsk.plan_import.v0")
    parser.add_argument("raw")
    parser.add_argument("out")
    parser.add_argument("--crop-size", nargs=2, type=float, required=True, metavar=("W", "H"))
    parser.add_argument("--crop-offset", nargs=2, type=float, default=(0, 0), metavar=("X", "Y"))
    parser.add_argument("--page-size", nargs=2, type=float, metavar=("W", "H"))
    parser.add_argument("--dpi", type=float, required=True)
    parser.add_argument("--scale", type=float, default=100.0)
    args = parser.parse_args(argv)
    try:
        plan = convert_tectly_file(
            args.raw, args.out, crop_size=args.crop_size, crop_offset=args.crop_offset,
            page_size=args.page_size, dpi=args.dpi, scale=args.scale, source_file=args.raw)
    except (PlanImportError, OSError, KeyError, json.JSONDecodeError) as error:
        print("plan import: %s" % error, file=sys.stderr)
        return 1
    print("%d walls, %d openings, %d rooms -> %s" % (
        len(plan["walls"]), len(plan["openings"]), len(plan["rooms"]), args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
