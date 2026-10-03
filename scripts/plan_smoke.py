"""Plan checks shared by the office and garage smokes."""

from __future__ import annotations

import math
import re

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
# Glyph extents run from the comma descender to the top of "m²", so the
# bbox short side is taller than the cap height (303 for 250 at 1:100).
GLYPH_BOX = (0.9, 1.5)


# SheetScale.Ladder: a set prints at 1:100, 1:200 or 1:500; 1:50 only when asked.
LADDER = (50, 100, 200, 500)
FIRST_STEP = 100
FILL_SHARE = 0.9


def ladder_step(need: float) -> int:
    """The first of 1:100, 1:200, 1:500 a drawing that needs 1:need fits,
    1:500 when none does. Mirrors SheetScale.Pick for one drawing."""
    for step in LADDER:
        if step >= FIRST_STEP and (need or 0) <= step + 1e-9:
            return step
    return LADDER[-1]


def _number(attr, key):
    try:
        return float(attr.get(key))
    except (TypeError, ValueError):
        return None


def _tag_texts(rows, part: str) -> list:
    return [
        obj for obj in rows
        if (obj.get("attributes") or {}).get("forsk:role") == "room_tag"
        and (obj.get("attributes") or {}).get("forsk:tag") == part
    ]


def check_room_tags(rows, page, failures) -> None:
    """Room tag texts as PlanSymbols.AddPlanText makes them: a name for each
    tagged room, an area line for each tagged room whose area was not dropped,
    model height 2.5 mm x scale, printed 2.5 mm, inside its room. A name
    stamped forsk:overflow may run past the room edge; its centre is inside.
    A tag stamped forsk:leader sits outside on a leader (check_leaders)."""
    scale = int(page.get("scale") or 0)
    tagged = int(page.get("room_tags") or 0)
    dropped = int(page.get("room_areas_dropped") or 0)
    overflow = int(page.get("rooms_overflow") or 0)
    expect = PAPER_TEXT_MM * float(scale)
    names = _tag_texts(rows, "name")
    areas = _tag_texts(rows, "area")
    tags = names + areas
    if expect <= 0 or not tags:
        failures.append(f"room tags missing scale={scale} rows={len(rows)}")
        return
    runs_over = [obj for obj in names if (obj.get("attributes") or {}).get("forsk:overflow") == "1"]
    if len(names) != tagged or len(areas) != tagged - dropped or len(runs_over) != overflow:
        failures.append(
            f"room tag texts: {len(names)} names {len(areas)} areas {len(runs_over)} overflow "
            f"for {tagged} tagged, {dropped} area dropped, {overflow} overflow"
        )
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
        ring = _pairs(attr.get("forsk:room"))
        if attr.get("forsk:leader") == "1":
            continue
        if attr.get("forsk:overflow") == "1":
            points = (((box[0] + box[2]) / 2, (box[1] + box[3]) / 2),)
        else:
            points = ((box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3]))
        if len(ring) < 3 or any(not _inside(point, ring) for point in points):
            failures.append(f"room tag outside its room bbox {[round(v) for v in box]}")
            bad += 1
    if bad == 0:
        print(f"    room tag text height {max(heights):.0f} paper {max(papers):g} inside")


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


def check_fit(page, label, failures) -> None:
    """With no scale asked, the plan prints at a step of the ladder it fits:
    at least its own first step (1:N with N >= need). A larger step came
    from another sheet of the set. At the plan's own step the fill floor
    holds: it fills more than the step below would have needed, so the
    plan would not have fitted there."""
    need = float(page.get("fit_need") or 0)
    scale = int(page.get("scale") or 0)
    fill = float(page.get("fill") or 0)
    want = ladder_step(need)
    print(f"    {label} plan fit 1:{scale} fill {fill:.2f}")
    below = max((s for s in LADDER if FIRST_STEP <= s < want), default=0)
    floor = FILL_SHARE * below / scale if scale and below else 0.0
    ok = page.get("fitted") is True and scale in LADDER[1:] and scale >= want
    if ok and scale == want and fill <= floor:
        ok = False
    if not ok:
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


def dxf_text(value: str) -> str:
    """DXF unicode escapes (backslash U+00F8) to characters."""
    return re.sub(r"\\U\+([0-9A-Fa-f]{4})", lambda m: chr(int(m.group(1), 16)), value or "")


DEFAULT_ROOM_NAME = "Rom"  # RoomDetect.DefaultRoomName


def check_room_names(rooms, dxf_rings, failures) -> None:
    """The names rooms_detect returned, against the DXF: each is Rom or a label
    that lies in the DXF room holding it, and each labelled DXF room gives one
    of its labels to a room inside it. Which label wins is RoomDetect.Name's
    rule, tested headless on this DXF (OfficeRoomsTests), not redone here."""
    bad = []
    for room in rooms:
        name = room.get("name")
        at = (room["x"], room["y"])
        held = [texts for texts, ring in dxf_rings if _inside(at, ring, 0.0)]
        if not name:
            bad.append(f"{room['id']} has no name")
        elif name != DEFAULT_ROOM_NAME and not any(name in texts for texts in held):
            bad.append(f"{room['id']} {name!r} not a label in its DXF room")
    for texts, ring in dxf_rings:
        inside = [room.get("name") for room in rooms if _inside((room["x"], room["y"]), ring, 0.0)]
        if not any(name in texts for name in inside):
            bad.append(f"DXF room {texts[0]!r} names no room")
    rom = sum(1 for room in rooms if room.get("name") == DEFAULT_ROOM_NAME)
    print(f"    room names {len(rooms)} from DXF labels {len(rooms) - rom} Rom {rom} wrong {len(bad)}")
    if bad:
        failures.append(f"office room names wrong {len(bad)}: {'; '.join(bad[:2])}")


def check_tag_names(rows, rooms, label, failures) -> None:
    """Each name tag on the sheet shows the name rooms_detect gave its room."""
    named = {room["id"]: room.get("name") for room in rooms}
    tags = _tag_texts(rows, "name")
    wrong = [
        f"{rid}={obj.get('text')!r} detected {named.get(rid)!r}"
        for obj in tags
        for rid in [(obj.get("attributes") or {}).get("forsk:room_id")]
        if rid not in named or obj.get("text") != named[rid]
    ]
    print(f"    {label} names {len(tags) - len(wrong)}/{len(tags)} as detected")
    if wrong:
        failures.append(f"{label} names not as detected: {'; '.join(wrong[:3])}")


def check_room_count(page, expected, label, failures) -> None:
    """One line per sheet, the counts as PlanStats defines them: every room is
    tagged (its name is on the sheet), too small (under the 1 m² cutoff), or
    without an outline to read. Of the tagged, area dropped show the name
    alone, leader have the tag outside the room on a leader (the name did not
    fit), and overflow are names that run past their room (no clear spot for
    a leader either; F5.2 makes that a failure). Then the leader, overflowing
    and untagged rooms by id, each on its own line."""
    tagged = int(page.get("room_tags") or 0)
    dropped = int(page.get("room_areas_dropped") or 0)
    leader = int(page.get("rooms_leader") or 0)
    overflow = int(page.get("rooms_overflow") or 0)
    small = int(page.get("rooms_too_small") or 0)
    no_outline = int(page.get("rooms_no_outline") or 0)
    untagged = [str(item) for item in page.get("rooms_untagged") or []]
    leading = [str(item) for item in page.get("rooms_leading") or []]
    overflowing = [str(item) for item in page.get("rooms_overflowing") or []]
    line = (
        f"{label} room tags {tagged}/{expected} area dropped {dropped} leader {leader} overflow {overflow} "
        f"too small {small} no outline {no_outline}"
    )
    print(f"    {line}")
    if leading:
        print(f"    {label} room tags leader {'; '.join(leading)}")
    if overflowing:
        print(f"    {label} room tags overflow {'; '.join(overflowing)}")
    if untagged:
        print(f"    {label} room tags untagged {'; '.join(untagged)}")
    if (
        tagged < 1
        or tagged + small + no_outline != expected
        or no_outline
        or overflow
        or len(untagged) != small + no_outline
        or len(leading) != leader
        or len(overflowing) != overflow
        or not overflow <= dropped <= tagged
        or leader + dropped > tagged
    ):
        ids = " ".join(item.split(" ", 1)[0] for item in untagged)
        failures.append(line + (f", untagged {ids}" if ids else ""))


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
    """Labelled inner wall-face rings of a thickness-wall DXF, as (label texts
    inside, ring). Rings are open: no vertex repeats, the caller closes them."""
    rings, labels = [], []
    for ent in _dxf_entities(path):
        layer = ent["layer"].lower()
        if layer == "wall" and ent["type"] == "LWPOLYLINE" and len(ent["points"]) >= 3:
            ring = _open_ring([tuple(p) for p in ent["points"]])
            if len(ring) >= 3:
                rings.append(ring)
        elif layer == "label" and ent["type"] in ("TEXT", "MTEXT") and ent["points"]:
            labels.append((dxf_text(ent["text"]).strip(), tuple(ent["points"][0])))
    rooms = []
    for ring in rings:
        # The outer outline holds every other ring. Rooms hold none.
        if any(other is not ring and _inside(other[0], ring, 0.0) for other in rings):
            continue
        names = [text for text, point in labels if _inside(point, ring, 0.0)]
        if names:
            rooms.append((names, ring))
    return rooms


def layer_rows(send, layer: str, hidden: bool = False) -> list:
    """Every object on a layer, with attributes, through get_objects pages.
    hidden also returns objects on a layer that is off."""
    rows, offset = [], 0
    while offset <= 5000:
        params = {
            "layer_filter": layer,
            "limit": 200,
            "offset": offset,
            "include_geometry": False,
            "include_attributes": True,
        }
        if hidden:
            params["include_hidden"] = True
        found = send("get_objects", params)
        batch = found.get("objects") or []
        rows.extend(batch)
        if not found.get("has_more") or not batch:
            break
        offset += len(batch)
    return rows


def opening_markers(send) -> list:
    """The opening markers. A-OPEN is off by default, and Rhino's default
    object list skips a layer that is off, so ask for hidden objects."""
    return [
        row for row in layer_rows(send, "A-OPEN", hidden=True)
        if (row.get("attributes") or {}).get("forsk:kind") == "opening_marker"
    ]


def schedule_rows(sheets) -> dict:
    """Rows per list over the schedules pages, in page order."""
    merged = {}
    for sheet in sheets or []:
        for kind, found in (((sheet or {}).get("schedules") or {}).get("lists") or {}).items():
            merged.setdefault(kind, []).extend((found or {}).get("rows") or [])
    return merged


def check_schedules(sheets, plan_rows, markers, page, label, failures) -> None:
    """The schedules as layout_pack read them back from its schedules pages,
    against the model: one door row per door marker and one window row per
    window marker, under the mark (forsk:mark) the marker carries and the
    plan prints beside it, every mark once; one room row per tagged room,
    with the name and the area its plan tag prints (the tag is ca. and the
    row's area)."""
    lists = schedule_rows(sheets)

    def rows(kind):
        return lists.get(kind) or []

    def attrs(obj):
        return obj.get("attributes") or {}

    doors = [m for m in markers if attrs(m).get("forsk:opening_kind") == "door"]
    windows = [m for m in markers if attrs(m).get("forsk:opening_kind") == "window"]
    door_ids = [row.get("id") for row in rows("door")]
    window_ids = [row.get("id") for row in rows("window")]
    marks = sorted(str(attrs(m).get("forsk:mark") or "") for m in doors + windows)
    listed = sorted(door_ids + window_ids)
    on_plan = sorted(str(obj.get("text") or "") for obj in plan_rows if attrs(obj).get("forsk:role") == "opening_mark")
    bad = []
    if not sheets:
        bad.append("no schedules page")
    if not markers:
        bad.append("read 0 opening markers from A-OPEN")
    elif len(door_ids) != len(doors) or len(window_ids) != len(windows):
        bad.append(f"rows doors {len(door_ids)}/{len(doors)} windows {len(window_ids)}/{len(windows)}")
    if "" in marks or len(set(marks)) != len(marks):
        bad.append("a marker without a mark, or a mark twice")
    elif listed != marks:
        bad.append(f"row marks are not the markers' ({', '.join(sorted(set(listed) ^ set(marks))[:4])})")
    if on_plan != marks:
        bad.append(f"plan marks {len(on_plan)} are not the markers' {len(marks)}")

    tags = {}
    for obj in plan_rows:
        attr = attrs(obj)
        if attr.get("forsk:role") == "room_tag":
            tags[(attr.get("forsk:room_id"), attr.get("forsk:tag"))] = obj.get("text")
    tagged = int(page.get("room_tags") or 0)
    wrong = []
    for row in rows("room"):
        rid, cells = row.get("id"), row.get("cells") or []
        name, area = (cells + ["", ""])[:2]
        if tags.get((rid, "name")) != name:
            wrong.append(f"{rid} {name!r} tag {tags.get((rid, 'name'))!r}")
        elif (rid, "area") in tags and tags[(rid, "area")] != f"ca. {area}":
            wrong.append(f"{rid} {area!r} tag {tags[(rid, 'area')]!r}")
    if len(rows("room")) != tagged:
        bad.append(f"room rows {len(rows('room'))} for {tagged} tagged rooms")
    if wrong:
        bad.append("rooms not as tagged: " + "; ".join(wrong[:2]))
    # The page measured every cell text Rhino laid out against its column.
    over = [item for sheet in sheets or [] for item in ((sheet or {}).get("schedules") or {}).get("cells_over") or []]
    if over:
        bad.append(f"{len(over)} cells wider than their column: {'; '.join(over[:2])}")
    matched = len(set(on_plan) & set(listed))
    print(
        f"    {label} schedules doors {len(door_ids)} windows {len(window_ids)} rooms {len(rows('room'))} "
        f"marks {matched}/{len(marks)}{'' if wrong else ', rooms as tagged'}{'' if over else ', cells fit'}, "
        f"pages {len(sheets or [])}"
    )
    if bad:
        failures.append(f"{label} schedules: {'; '.join(bad[:3])}")


OPENING_PROBE_MM = 300.0  # ScheduleSheet.OpeningProbeMm, past each wall face


def check_opening_rooms(sheets, markers, rooms, rings, label, failures) -> None:
    """Each door and window row's Rom cell against the plan, not a stand-in:
    a probe a step past each face of the opening lands in a DXF room ring or
    outside. For each side in a ring, the cell names a room rooms_detect
    found in that ring; the cell is "–" only when no side is in one. A
    divided ring (the corridor) accepts either of its rooms."""
    lists = schedule_rows(sheets)
    cells = {
        row.get("id"): row.get("cells") or []
        for kind in ("door", "window")
        for row in lists.get(kind) or []
    }
    held = [
        {room.get("name") for room in rooms if _inside((room["x"], room["y"]), ring, 0.0)}
        for _texts, ring in rings
    ]
    wrong = []
    for marker in markers:
        mark = (marker.get("attributes") or {}).get("forsk:mark")
        box = _box(marker)
        row = cells.get(mark)
        if row is None or box is None:
            wrong.append(f"{mark} has no row")
            continue
        cell = row[4] if len(row) > 4 else ""
        listed = [] if cell in ("", "–") else cell.split(" / ")
        # The marker box is thin across the wall: probe along its short side.
        dx, dy = box[2] - box[0], box[3] - box[1]
        across = (0.0, 1.0) if dx >= dy else (1.0, 0.0)
        reach = min(dx, dy) / 2.0 + OPENING_PROBE_MM
        cx, cy = (box[0] + box[2]) / 2.0, (box[1] + box[3]) / 2.0
        sides = []
        for sign in (-1.0, 1.0):
            probe = (cx + sign * across[0] * reach, cy + sign * across[1] * reach)
            names = next((held[i] for i, (_t, ring) in enumerate(rings) if _inside(probe, ring, 0.0)), None)
            if names is not None:
                sides.append(names)
        if len(listed) != len(sides) or not all(any(name in names for name in listed) for names in sides):
            expect = " / ".join("|".join(sorted(n for n in names if n)) for names in sides) or "–"
            wrong.append(f"{mark} {cell!r} expected {expect!r}")
    print(f"    {label} opening rooms {len(markers) - len(wrong)}/{len(markers)} as the plan")
    if wrong:
        failures.append(f"{label} opening rooms: {'; '.join(wrong[:3])}")


MARK_PAPER_MM = 1.25  # Schedules.MarkMm, half the room tags' 2.5 mm


def check_marks(rows, page, label, failures) -> None:
    """Door and window marks on the plan: printed at half the tag height,
    and no mark's box overlaps a room tag's (name or area line)."""
    marks = [obj for obj in rows if (obj.get("attributes") or {}).get("forsk:role") == "opening_mark"]
    tags = [obj for obj in rows if (obj.get("attributes") or {}).get("forsk:role") == "room_tag"]
    bad = []
    papers = sorted({_number(obj.get("attributes") or {}, "forsk:paper_height") for obj in marks} - {None})
    if not marks:
        bad.append("no marks on the plan")
    elif any(abs(paper - MARK_PAPER_MM) > MARK_PAPER_MM * 0.10 for paper in papers) or not papers:
        bad.append(f"mark paper {papers} mm expected {MARK_PAPER_MM:g}")
    hits = []
    for mark in marks:
        box = _box(mark)
        if box is None:
            continue
        for tag in tags:
            other = _box(tag)
            if other and box[0] < other[2] and other[0] < box[2] and box[1] < other[3] and other[1] < box[3]:
                hits.append(f"{mark.get('text')} on {tag.get('text')!r}")
    if hits:
        bad.append(f"{len(hits)} marks on room tags: {'; '.join(hits[:3])}")
    listed = page.get("marks_on_tags") or []
    if listed:
        bad.append(f"layout_pack found no clear spot for {' '.join(listed[:4])}")
    clear = len(marks) - len({hit.split(' on ')[0] for hit in hits})
    paper = papers[-1] if papers else 0
    print(f"    {label} marks {len(marks)} paper {paper:g} clear of tags {clear}/{len(marks)}")
    if bad:
        failures.append(f"{label} marks: {'; '.join(bad)}")


DIM_PAPER_MM = 1.8
# Symbol lines layout_pack keeps a facade chain's lines off; a room's may
# cross a door swing's box no more than it crosses the room.
DIM_TOUCH_ROLES = ("room_tag", "opening_mark", "symbol", "room_leader")


def _attrs(obj) -> dict:
    return obj.get("attributes") or {}


def _touch(a, b) -> bool:
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def check_dimensions(rows, page, label, failures, markers=()) -> dict:
    """F5.2 dimensions as layout_pack baked them on the plan, read back
    against the model. Every chain adds up to its total, and every value is
    the length it spans in the model to the mm. The overalls are the poché's
    extents. A facade chain's point at an opening is that opening's centre
    (its marker's). Every opening in an outer wall is on a chain. Values
    print 1.8 mm. No value touches a tag, a mark, a symbol, a leader or any
    other dimension; no dimension line or witness touches a tag or a mark,
    and a facade, jog or overall line no symbol. Returns the counts."""
    dims = [obj for obj in rows if _attrs(obj).get("forsk:role") == "dimension"]
    texts = [obj for obj in dims if _attrs(obj).get("forsk:symbol") == "text"]
    strokes = [obj for obj in dims if _attrs(obj).get("forsk:symbol") != "text"]
    info = page.get("dimensions") or {}
    bad = []
    chains = {}
    for obj in texts:
        chains.setdefault(_attrs(obj).get("forsk:dim_chain"), []).append(obj)
    kinds = {chain: _attrs(items[0]).get("forsk:dim_kind") for chain, items in chains.items()}
    exterior = [chain for chain, kind in kinds.items() if kind != "room"]
    counts = {
        "chains": len(chains),
        "exterior": len(exterior),
        "exterior_values": sum(len(chains[chain]) for chain in exterior),
        "room": len(chains) - len(exterior),
    }
    if not texts:
        failures.append(f"{label} dims: no dimensions on the plan")
        print(f"    {label} dims missing")
        return counts

    sums = 0
    for chain, items in chains.items():
        total = _number(_attrs(items[0]), "forsk:dim_total")
        values = [_number(_attrs(obj), "forsk:dim_value") for obj in items]
        if total is not None and None not in values and abs(sum(values) - total) < 0.5:
            sums += 1
        else:
            bad.append(f"{chain} adds {values} to {total}")

    spans = 0
    for obj in texts:
        attr = _attrs(obj)
        ends = _pairs(attr.get("forsk:dim_span"))
        value = _number(attr, "forsk:dim_value")
        length = math.dist(ends[0], ends[1]) if len(ends) == 2 else None
        if length is not None and value is not None and abs(length - value) <= 1.0:
            spans += 1
        else:
            bad.append(f"{attr.get('forsk:dim_chain')} {value} spans {length}")

    fills = [_box(obj) for obj in rows if _attrs(obj).get("forsk:role") == "section_fill"]
    fills = [box for box in fills if box]
    overall = {
        _attrs(obj).get("forsk:dim_side"): _number(_attrs(obj), "forsk:dim_value")
        for obj in texts if _attrs(obj).get("forsk:dim_kind") == "overall"
    }
    width = height = None
    if fills:
        width = max(b[2] for b in fills) - min(b[0] for b in fills)
        height = max(b[3] for b in fills) - min(b[1] for b in fills)
    extents = {"S": width, "N": width, "E": height, "W": height}
    if sorted(overall) != ["E", "N", "S", "W"] or any(
        extents[side] is None or abs(overall[side] - extents[side]) > 1.0 for side in overall
    ):
        shown = " ".join(f"{side} {overall[side]:.0f}" for side in sorted(overall))
        poche = f"{width:.0f} x {height:.0f}" if width is not None else "missing"
        bad.append(f"overall {shown}, poché {poche}")

    by_id = {str(item.get("id") or "").lower(): item for item in markers or []}
    centres, ends_at = 0, 0
    for obj in texts:
        attr = _attrs(obj)
        ends = _pairs(attr.get("forsk:dim_span"))
        for key, end in (("forsk:dim_from", 0), ("forsk:dim_to", 1)):
            marker_id = str(attr.get(key) or "").lower()
            if not marker_id or len(ends) != 2:
                continue
            ends_at += 1
            box = _box(by_id.get(marker_id) or {})
            length = math.dist(ends[0], ends[1])
            if box is None or length < 1:
                bad.append(f"{attr.get('forsk:dim_chain')} point at {marker_id[:8]}: no marker")
                continue
            ux, uy = (ends[1][0] - ends[0][0]) / length, (ends[1][1] - ends[0][1]) / length
            cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
            off = (cx - ends[end][0]) * ux + (cy - ends[end][1]) * uy
            if abs(off) <= 2.0:
                centres += 1
            else:
                bad.append(f"{attr.get('forsk:dim_chain')} point {off:.0f} mm off {marker_id[:8]}'s centre")

    openings = int(info.get("openings") or 0)
    shown = int(info.get("openings_shown") or 0)
    if shown != openings or openings < 1:
        bad.append(f"openings on a chain {shown}/{openings}")
    if int(info.get("collisions") or 0):
        bad.append(f"{info.get('collisions')} values with no clear spot")
    papers = sorted({_number(_attrs(obj), "forsk:paper_height") for obj in texts} - {None})
    if not papers or any(abs(paper - DIM_PAPER_MM) > DIM_PAPER_MM * 0.10 for paper in papers):
        bad.append(f"value paper {papers} mm expected {DIM_PAPER_MM:g}")

    others = {role: [b for b in (_box(o) for o in rows if _attrs(o).get("forsk:role") == role) if b] for role in DIM_TOUCH_ROLES}
    text_boxes = [(obj, _box(obj)) for obj in texts]
    stroke_boxes = [(obj, _box(obj)) for obj in strokes]
    hits = []
    for i, (obj, box) in enumerate(text_boxes):
        if box is None:
            continue
        name = f"{_attrs(obj).get('forsk:dim_chain')} {obj.get('text')}"
        for role, boxes in others.items():
            if any(_touch(box, other) for other in boxes):
                hits.append(f"{name} on a {role}")
        if any(other and _touch(box, other) for _o, other in text_boxes[i + 1:]):
            hits.append(f"{name} on a value")
        if any(other and _touch(box, other) for _o, other in stroke_boxes):
            hits.append(f"{name} on a dimension line")
    for obj, box in stroke_boxes:
        attr = _attrs(obj)
        if box is None or attr.get("forsk:symbol") == "tick":
            continue
        roles = ("room_tag", "opening_mark") + (("symbol",) if attr.get("forsk:dim_kind") != "room" else ())
        for role in roles:
            if any(_touch(box, other) for other in others[role]):
                hits.append(f"{attr.get('forsk:dim_chain')} {attr.get('forsk:symbol')} on a {role}")
    if hits:
        bad.append(f"{len(hits)} touching: {'; '.join(hits[:3])}")

    paper = papers[-1] if papers else 0
    size = f"{width:.0f}x{height:.0f}" if width is not None else "none"
    print(
        f"    {label} dims chains {counts['chains']} exterior {counts['exterior']} values {counts['exterior_values']} "
        f"room {counts['room']}, sums {sums}/{len(chains)}, spans {spans}/{len(texts)}, overall {size}, "
        f"centres {centres}/{ends_at}, openings {shown}/{openings}, paper {paper:g}, "
        f"{'clear' if not hits else 'touching'}"
    )
    if bad:
        failures.append(f"{label} dims: {'; '.join(bad[:4])}")
    return counts


def check_leaders(rows, label, failures) -> int:
    """F5.2: a room tag on a leader (forsk:leader) sits outside its room,
    and a leader line of the same room runs from inside the room to within
    1 mm on paper plus the text gap of the tag."""
    tags = [obj for obj in rows if _attrs(obj).get("forsk:role") == "room_tag" and _attrs(obj).get("forsk:leader") == "1"]
    lines = [obj for obj in rows if _attrs(obj).get("forsk:role") == "room_leader" and _attrs(obj).get("forsk:symbol") == "line"]
    rooms = {}
    for obj in tags:
        rooms.setdefault(_attrs(obj).get("forsk:room_id"), []).append(obj)
    bad = []
    for room_id, items in rooms.items():
        ring = _pairs(_attrs(items[0]).get("forsk:room"))
        mine = [obj for obj in lines if _attrs(obj).get("forsk:room_id") == room_id]
        ends = _pairs(_attrs(mine[0]).get("forsk:line")) if mine else []
        if len(ends) != 2 or not _inside(ends[0], ring):
            bad.append(f"{room_id} leader {'does not start in the room' if ends else 'missing'}")
            continue
        boxes = [b for b in (_box(obj) for obj in items) if b]
        block = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
        gap = max(block[0] - ends[1][0], ends[1][0] - block[2], block[1] - ends[1][1], ends[1][1] - block[3], 0.0)
        centre = ((block[0] + block[2]) / 2, (block[1] + block[3]) / 2)
        if _inside(centre, ring, 0.0) or gap > 0.5 * _number(_attrs(items[0]), "forsk:text_height"):
            bad.append(f"{room_id} tag {'in its room' if _inside(centre, ring, 0.0) else f'{gap:.0f} mm off its leader'}")
    if bad:
        failures.append(f"{label} leaders: {'; '.join(bad[:3])}")
    return len(rooms)


def check_pages_written(laid_out, pdf, label, failures) -> int:
    """Laid out equals written: every page the export was given is in the
    PDF. export_pdf leaves out a page whose preview stayed blank and still
    says it wrote the rest, so each missing page fails here by its view.
    Returns the pages written of those laid out."""
    written = {str(name) for name in pdf.get("pages") or []}
    missing = [item for item in laid_out if str(item.get("page") or "") not in written]
    for item in missing:
        failures.append(f"{label} {item.get('view')} page not in the PDF: {pdf.get('message')}")
    return len(laid_out) - len(missing)
