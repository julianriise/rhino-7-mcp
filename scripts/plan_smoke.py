"""Plan checks shared by the office and garage smokes."""

from __future__ import annotations

import math

FACE_PARTS = {"sill", "frame", "jamb"}


def _pairs(text) -> list:
    points = []
    for pair in str(text or "").split(";"):
        xy = pair.split(",")
        if len(xy) != 2:
            continue
        try:
            points.append((float(xy[0]), float(xy[1])))
        except ValueError:
            continue
    return points


def _box(obj):
    box = obj.get("bounding_box") or []
    try:
        x0, y0 = float(box[0][0]), float(box[0][1])
        x1, y1 = float(box[1][0]), float(box[1][1])
    except (TypeError, ValueError, IndexError):
        return None
    return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)


def _inside(point, ring, tol: float = 2.0) -> bool:
    if len(ring) < 3:
        return False
    x, y = point
    # A corner that lands on the boundary still counts as inside.
    for i, a in enumerate(ring):
        b = ring[(i + 1) % len(ring)]
        abx, aby = b[0] - a[0], b[1] - a[1]
        apx, apy = x - a[0], y - a[1]
        cross = abx * apy - aby * apx
        length = (abx * abx + aby * aby) ** 0.5
        if length > 1e-6 and abs(cross) / length <= tol:
            dot = apx * abx + apy * aby
            if -tol * length <= dot <= length * length + tol * length:
                return True
    inside = False
    j = len(ring) - 1
    for i, a in enumerate(ring):
        b = ring[j]
        if (a[1] > y) != (b[1] > y):
            xhit = (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]
            if x < xhit:
                inside = not inside
        j = i
    return inside


PAPER_TEXT_MM = 2.5
BOUNDED_MIN = 0.9  # PlanSymbols.RoomBoundedMin
# Glyph extents run from the comma descender to the top of "m²", so the
# bbox short side is taller than the cap height (303 for 250 at 1:100).
GLYPH_BOX = (0.9, 1.5)


def round_scale_up(need: float) -> int:
    """Standard denominator at or above need: step 5 to 50, 10 to 100,
    25 to 500, then 50. Mirrors OpeningTypes.RoundScaleUp."""
    if not need or need <= 0:
        return 0
    step = 5 if need <= 50 else 10 if need <= 100 else 25 if need <= 500 else 50
    return int(math.ceil(need / step - 1e-9) * step)


def _number(attr, key):
    try:
        return float(attr.get(key))
    except (TypeError, ValueError):
        return None


def check_room_tags(rows, scale, expected, failures) -> None:
    """Room tags as PlanSymbols.AddPlanText makes them: role room_tag, one
    area line per room, model height 2.5 mm x scale, printed 2.5 mm, inside."""
    expect = PAPER_TEXT_MM * float(scale)
    tags = [
        obj for obj in rows
        if str((obj.get("attributes") or {}).get("forsk:role") or "") == "room_tag"
    ]
    areas = [
        obj for obj in tags
        if str((obj.get("attributes") or {}).get("forsk:tag") or "") == "area"
    ]
    if expect <= 0 or not tags:
        failures.append(f"room tags missing scale={scale} rows={len(rows)}")
        return
    if len(areas) != int(expected or 0):
        failures.append(f"room tag areas {len(areas)} expected {expected}")
        return
    bad = 0
    heights, papers = [], []
    for obj in tags:
        attr = obj.get("attributes") or {}
        height = _number(attr, "forsk:text_height")
        paper = _number(attr, "forsk:paper_height")
        if height is None or paper is None:
            failures.append(f"room tag height {height} paper {paper} missing")
            bad += 1
            continue
        heights.append(height)
        papers.append(paper)
        if abs(height - expect) > expect * 0.10:
            failures.append(f"room tag height {height:.0f} expected {expect:.0f}")
            bad += 1
        if abs(paper - PAPER_TEXT_MM) > PAPER_TEXT_MM * 0.10:
            failures.append(f"room tag paper {paper:g} mm expected {PAPER_TEXT_MM:g}")
            bad += 1
        box = _box(obj)
        if box is None:
            failures.append("room tag bbox missing")
            bad += 1
            continue
        short = min(box[2] - box[0], box[3] - box[1])
        low, high = GLYPH_BOX
        if not low * height <= short <= high * height:
            failures.append(f"room tag bbox {short:.0f} for height {height:.0f}")
            bad += 1
        bounded = _number(attr, "forsk:bounded")
        if bounded is None or bounded < BOUNDED_MIN:
            failures.append(f"room tag bounded by walls {bounded} < {BOUNDED_MIN}")
            bad += 1
        if attr.get("forsk:footprint") not in ("floor", "walls"):
            failures.append(f"room tag footprint {attr.get('forsk:footprint')!r}")
            bad += 1
        ring = _pairs(attr.get("forsk:room"))
        corners = (
            (box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3])
        )
        if len(ring) < 3 or any(not _inside(corner, ring) for corner in corners):
            failures.append(f"room tag outside its room bbox {[round(v) for v in box]}")
            bad += 1
    if bad == 0:
        print(
            f"    room tags {len(areas)} height {max(heights):.0f} "
            f"paper {max(papers):g} inside"
        )


def check_symbol_faces(rows, failures) -> None:
    """Sill, frame, and jamb centerlines span the measured wall faces."""
    groups = {}
    for obj in rows:
        attr = obj.get("attributes") or {}
        part = str(attr.get("forsk:symbol") or "")
        if part not in FACE_PARTS:
            continue
        marker = str(attr.get("forsk:marker_id") or "")
        faces = _pairs(attr.get("forsk:faces"))
        line = _pairs(attr.get("forsk:line"))
        if not marker or len(faces) != 2 or len(line) != 2:
            failures.append(f"symbol {part or '?'} missing faces")
            continue
        bucket = groups.setdefault(marker, {"faces": faces, "pts": []})
        bucket["pts"].extend(line)
    if not groups:
        failures.append("symbols on faces missing")
        return
    bad = 0
    for marker, item in groups.items():
        a, b = item["faces"]
        axis = (b[0] - a[0], b[1] - a[1])
        length = (axis[0] ** 2 + axis[1] ** 2) ** 0.5
        if length < 1:
            failures.append(f"symbol faces {marker} collapsed")
            bad += 1
            continue
        ux, uy = axis[0] / length, axis[1] / length
        projs = [(p[0] - a[0]) * ux + (p[1] - a[1]) * uy for p in item["pts"]]
        lo, hi = min(projs), max(projs)
        if lo < -1.0 or hi > length + 1.0 or abs(lo) > 1.0 or abs(hi - length) > 1.0:
            failures.append(
                f"symbol faces {marker[:8]} across {lo:.1f}..{hi:.1f} wall {length:.1f}"
            )
            bad += 1
    if bad == 0:
        print(f"    symbols on faces {len(groups)}")


def check_symbols_on_wall(rows, scale, failures) -> None:
    """Every symbol of an opening stays within its host wall's length.
    The run (wall start and end) rides on that opening's sill, frame, or
    jamb lines. Ribbons are 0.13 mm x scale wide, so allow half of that."""
    runs, boxes = {}, {}
    for obj in rows:
        attr = obj.get("attributes") or {}
        if str(attr.get("forsk:role") or "") != "symbol":
            continue
        marker = str(attr.get("forsk:marker_id") or "")
        run = _pairs(attr.get("forsk:run"))
        if marker and len(run) == 2:
            runs[marker] = run
        box = _box(obj)
        if marker and box:
            boxes.setdefault(marker, []).append(box)
    if not boxes:
        failures.append("symbols on wall missing")
        return
    tol = 1.0 + 0.13 * float(scale or 0) / 2.0
    bad = 0
    for marker, items in boxes.items():
        run = runs.get(marker)
        if not run:
            failures.append(f"symbol {marker[:8]} has no wall run")
            bad += 1
            continue
        a, b = run
        length = ((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5
        if length < 1:
            failures.append(f"symbol {marker[:8]} wall run collapsed")
            bad += 1
            continue
        ux, uy = (b[0] - a[0]) / length, (b[1] - a[1]) / length
        projs = [
            (x - a[0]) * ux + (y - a[1]) * uy
            for x0, y0, x1, y1 in items
            for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))
        ]
        lo, hi = min(projs), max(projs)
        if lo < -tol or hi > length + tol:
            failures.append(
                f"symbol {marker[:8]} spans {lo:.0f}..{hi:.0f} past wall 0..{length:.0f}"
            )
            bad += 1
    if bad == 0:
        print(f"    symbols on wall {len(boxes)}")


FILL_MIN = 0.6


def check_fit(page, label, failures) -> None:
    """A fitted plan takes the largest standard scale that fits and fills
    at least 60% of the detail on its tighter axis."""
    need = float(page.get("fit_need") or 0)
    scale = int(page.get("scale") or 0)
    fill = float(page.get("fill") or 0)
    want = round_scale_up(need)
    print(f"    {label} plan fit 1:{scale} fill {fill:.2f}")
    if page.get("fitted") is not True or scale != want or fill < FILL_MIN:
        failures.append(
            f"{label} plan fit 1:{scale} want 1:{want} need {need:.1f} fill {fill:.2f}"
        )


TITLE_MAX_MM = 20.0
BAR_LENGTHS_M = (1, 2, 3, 5, 10, 20, 30, 50, 100)
BAR_MIN_MM, BAR_MAX_MM = 40.0, 80.0
BAR_TOL_MM = 0.5
BOX_SLACK_MM = 0.01  # layout_pack rounds its boxes to 0.01 mm
# A3 sheet border (10 mm margin) and the footer band along its bottom edge.
BORDER_X = (10.0, 410.0)
BAND_Y = (10.0, 28.0)
NORTH_MM = 12.0


def _paper_box(item):
    try:
        return tuple(float(item[k]) for k in ("x0", "y0", "x1", "y1"))
    except (KeyError, TypeError, ValueError):
        return None


def check_footer(page, label, failures) -> None:
    """Title block, scale bar, and north arrow as layout_pack measured the
    objects it stored, in paper mm. The scale is the detail's own scale."""
    footer = page.get("footer") or {}
    scale = int(page.get("page_scale") or 0)
    if scale < 1 or scale != int(page.get("scale") or 0):
        failures.append(f"{label} page scale {scale} sheet 1:{page.get('scale')}")
        return

    title = footer.get("title_block") or {}
    tbox = _paper_box(title)
    cells = title.get("cells") or []
    height = float(title.get("h") or 0)
    width = float(title.get("w") or 0)
    print(f"    {label} title block h {height:.1f} w {width:.1f} cells {len(cells)}")
    empty = [c.get("name") for c in cells if str(c.get("text") or "").strip() in ("", "—", "-")]
    scale_cell = [c.get("text") for c in cells if c.get("name") == "scale"]
    if tbox is None or height > TITLE_MAX_MM + BOX_SLACK_MM or width <= 4 * height:
        failures.append(f"{label} title block h {height:.1f} w {width:.1f}")
    if not cells or empty:
        failures.append(f"{label} title block cells {len(cells)} empty {empty}")
    if scale_cell != [f"1:{scale}"]:
        failures.append(f"{label} title block scale {scale_cell} page 1:{scale}")

    bar = footer.get("scale_bar") or {}
    meters = int(bar.get("meters") or 0)
    length = float(bar.get("w") or 0)
    print(f"    {label} scale bar {meters} m len {length:.1f} mm at 1:{scale}")
    expect = meters * 1000.0 / scale
    if (
        meters not in BAR_LENGTHS_M
        or not BAR_MIN_MM - BOX_SLACK_MM <= length <= BAR_MAX_MM + BOX_SLACK_MM
        or abs(expect - length) > BAR_TOL_MM
    ):
        failures.append(
            f"{label} scale bar {meters} m len {length:.1f} mm want {expect:.1f} at 1:{scale}"
        )

    free = int(footer.get("free_labels", -1))
    print(f"    {label} free label {free}")
    if free != 0:
        failures.append(f"{label} free label {free}")

    plan = str(page.get("view") or "") == "plan"
    groups = {
        "title": _paper_box(title.get("all") or {}),
        "bar": _paper_box(bar.get("with_labels") or {}),
    }
    if plan:
        groups["arrow"] = _paper_box(footer.get("north_arrow") or {})
    missing = [name for name, box in groups.items() if box is None]
    if missing:
        failures.append(f"{label} footer boxes missing {missing}")
        return
    lo_x, hi_x = BORDER_X[0] - BOX_SLACK_MM, BORDER_X[1] + BOX_SLACK_MM
    lo_y, hi_y = BAND_Y[0] - BOX_SLACK_MM, BAND_Y[1] + BOX_SLACK_MM
    out = [
        f"{name} {[round(v, 1) for v in box]}"
        for name, box in groups.items()
        if not (lo_x <= box[0] and box[2] <= hi_x and lo_y <= box[1] and box[3] <= hi_y)
    ]
    if groups["bar"][2] >= groups["title"][0]:
        out.append(f"bar x1 {groups['bar'][2]:.1f} >= title x0 {groups['title'][0]:.1f}")
    if out:
        failures.append(f"{label} footer outside band: " + "; ".join(out))
    else:
        print(f"    {label} footer inside sheet")

    if not plan:
        return
    arrow, barbox = groups["arrow"], groups["bar"]
    tall = arrow[3] - arrow[1]
    left = arrow[2] < barbox[0]
    sized = abs(tall - NORTH_MM) <= 0.5
    centred = abs((arrow[1] + arrow[3]) / 2 - sum(BAND_Y) / 2) <= 0.5
    if left and sized and centred:
        print(f"    {label} north arrow left")
    else:
        failures.append(
            f"{label} north arrow {[round(v, 1) for v in arrow]} h {tall:.1f} "
            f"bar x0 {barbox[0]:.1f}"
        )


def check_room_count(page, expected, label, failures) -> None:
    """Every drawn room is tagged or too small for its tag; none is unbounded."""
    found = int(page.get("room_tags") or 0)
    unfit = int(page.get("rooms_unfit") or 0)
    unbounded = int(page.get("rooms_unbounded") or 0)
    print(f"    {label} room tags {found}/{expected} too small {unfit} unbounded {unbounded}")
    if found < 1 or found + unfit != expected or unbounded:
        failures.append(
            f"{label} room tags {found}/{expected} too small {unfit} unbounded {unbounded}"
        )


def _dxf_pairs(path):
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    for i in range(0, len(lines) - 1, 2):
        yield lines[i].strip(), lines[i + 1].strip()


def _dxf_entities(path):
    """(type, layer, points, text) per ENTITIES entry. Stdlib only."""
    section = None
    current = None
    previous = None
    for code, value in _dxf_pairs(path):
        if code == "2" and previous == ("0", "SECTION"):
            section = value
        previous = (code, value)
        if section != "ENTITIES":
            continue
        if code == "0":
            if current:
                yield current
            current = {"type": value, "layer": "", "points": [], "text": ""}
            continue
        if current is None:
            continue
        if code == "8":
            current["layer"] = value
        elif code == "1":
            current["text"] = value
        elif code == "10":
            current["points"].append([float(value), 0.0])
        elif code == "20" and current["points"]:
            current["points"][-1][1] = float(value)
    if current:
        yield current


def _open_ring(points, tol: float = 0.01) -> list:
    """Drop repeated vertices, including a closing copy of the first one.
    The office DXF closes its rings that way; closing them again gave
    create_object a zero-length segment and Rhino refused the polyline."""
    ring = []
    for point in points:
        if ring and abs(point[0] - ring[-1][0]) <= tol and abs(point[1] - ring[-1][1]) <= tol:
            continue
        ring.append(point)
    while len(ring) > 1 and abs(ring[0][0] - ring[-1][0]) <= tol and abs(ring[0][1] - ring[-1][1]) <= tol:
        ring.pop()
    return ring


def dxf_rooms(path) -> list:
    """Labelled inner wall-face rings of a thickness-wall DXF, as (label, ring).
    Rings are open: no vertex repeats, the caller closes them."""
    rings, labels = [], []
    for ent in _dxf_entities(path):
        layer = ent["layer"].lower()
        if layer == "wall" and ent["type"] == "LWPOLYLINE" and len(ent["points"]) >= 3:
            ring = _open_ring([tuple(p) for p in ent["points"]])
            if len(ring) >= 3:
                rings.append(ring)
        elif layer == "label" and ent["type"] in ("TEXT", "MTEXT") and ent["points"]:
            labels.append((ent["text"], tuple(ent["points"][0])))
    rooms = []
    for ring in rings:
        # The outer outline holds every other ring. Rooms hold none.
        if any(other is not ring and _inside(other[0], ring, 0.0) for other in rings):
            continue
        names = [text for text, point in labels if _inside(point, ring, 0.0)]
        if names:
            rooms.append((names[0], ring))
    return rooms
