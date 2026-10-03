"""F5.3 smoke step: a long and a cross section through a room, as chat makes
them (the Sheets intent, then section_add and layout_pack), checked against
the model the sheets were drawn from.

The checks read the model, not the drawing's own report: the walls the
plane crosses (their boxes), the slab tops and undersides, the roof, the
rooms the line runs through (their marker polygons). Then the page records
and the S-DRAW objects must agree:

- the marker A–A on the plan: both ends, clear of every room tag and
  opening mark;
- at least one poché hatch per cut wall (forsk:source is the wall);
- the ground line at the lowest slab's underside, or with no slab at the
  lowest wall base;
- level marks at each slab top (with no slab: the floor of each room the line
  crosses), the ground, and gesims/møne at the roof top;
- a free height for each room the line crosses: roof underside less floor.

A-ROOF is a hidden layer, so the roof is read with include_hidden. An
expectation the model cannot give (no wall, no roof, no floor, an empty level
list) is a failure of its own, never a pass by comparing nothing to nothing.

One summary line per run ("sections ..."), the rest in failures.
"""

from __future__ import annotations

from typing import Callable

Send = Callable[[str, dict], dict]

TOL = 1.0
LETTERS = ("A", "B")


def _rows(send: Send, layer: str, hidden: bool = False) -> list:
    rows: list = []
    offset = 0
    while offset <= 20000:
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


def _attr(row: dict) -> dict:
    return row.get("attributes") or {}


def _box(row: dict):
    box = row.get("bounding_box") or []
    try:
        xs = (float(box[0][0]), float(box[1][0]))
        ys = (float(box[0][1]), float(box[1][1]))
        zs = (float(box[0][2]), float(box[1][2]))
    except (TypeError, ValueError, IndexError):
        return None
    return min(xs), min(ys), min(zs), max(xs), max(ys), max(zs)


def _overlap(a, b) -> bool:
    return a[0] < b[3] and b[0] < a[3] and a[1] < b[4] and b[1] < a[4]


def _inside(point, ring) -> bool:
    x, y = point
    inside = False
    for i, a in enumerate(ring):
        b = ring[i - 1]
        if (a[1] > y) != (b[1] > y) and x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]:
            inside = not inside
    return inside


def crossed(ring, a, b, step: float = 20.0, min_span: float = 200.0) -> bool:
    """True when the line a-b (extended 50 m both ways) runs inside ring for at least min_span."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    length = (dx * dx + dy * dy) ** 0.5
    if length < 1 or len(ring) < 3:
        return False
    ux, uy = dx / length, dy / length
    run = best = 0.0
    t = -50000.0
    while t <= length + 50000.0:
        if _inside((a[0] + ux * t, a[1] + uy * t), ring):
            run += step
            best = max(best, run)
        else:
            run = 0.0
        t += step
    return best >= min_span


def _line_hits_box(box, a, b) -> bool:
    """The vertical plane through a-b cuts this XY box (both sides of the line hold a corner)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    sides = set()
    for x in (box[0], box[3]):
        for y in (box[1], box[4]):
            cross = dx * (y - a[1]) - dy * (x - a[0])
            if abs(cross) > 1e-6 * max(1.0, (dx * dx + dy * dy) ** 0.5):
                sides.add(cross > 0)
    return len(sides) == 2


def run_step(send: Send, label: str, room: str, pdf: str, failures: list, panel: Send | None = None) -> dict:
    """Sections A (long) and B (cross) through room. Returns the layout_pack result."""
    reasons: list[str] = []

    # Chat's path: the message routes to Sheets, whose tools are section_add then layout_pack.
    if panel is not None:
        for text in (f"section A through the {room}", f"tverrsnitt gjennom {room}"):
            intent = panel("state", {"text": text}).get("intent")
            if intent != "sheets":
                reasons.append(f"intent {text!r}={intent}")

    send("section_clear", {})
    defs = {}
    for letter, axis in zip(LETTERS, ("long", "cross")):
        added = send("section_add", {"letter": letter, "room": room, "axis": axis})
        defs[letter] = added.get("section") or {}
        if (defs[letter].get("axis"), defs[letter].get("view")) != (axis, f"section_{letter.lower()}"):
            reasons.append(f"section_add {letter} {added.get('message')}")

    # One PDF of exactly these pages: the plan with the markers, then A, then B.
    send("clear_layouts", {})
    packed = send("layout_pack", {"views": ["plan", "section_a", "section_b"], "replace": True})
    pages = {str(p.get("view")): p for p in packed.get("pages") or []}
    plan = pages.get("plan") or {}

    # The model the sheets were drawn from.
    walls = [r for r in _rows(send, "A-WALL", hidden=True) if str(_attr(r).get("forsk:kind")).lower() == "wall"]
    floors = [_box(r) for r in _rows(send, "A-FLOR", hidden=True) if str(_attr(r).get("forsk:kind")).lower() == "floor"]
    roofs = [_box(r) for r in _rows(send, "A-ROOF", hidden=True) if str(_attr(r).get("forsk:kind")).lower() == "roof"]
    floors = [b for b in floors if b]
    roofs = [b for b in roofs if b]
    wall_boxes = [b for b in (_box(w) for w in walls) if b]
    rooms = send("rooms_detect", {})
    rings = {}
    # ids (marker order) and rooms (detected first) are not parallel: the marker
    # names its room, forsk:room_id, or for an outline drawn by hand its name.
    for marker in rooms.get("ids") or []:
        info = send("get_object_info", {"id": marker})
        rid = (info.get("attributes") or {}).get("forsk:room_id") or info.get("name")
        points = ((info.get("geometry") or {}).get("points")) or []
        ring = [(float(p[0]), float(p[1]), float(p[2]) if len(p) > 2 else 0.0) for p in points]
        if ring and rid:
            rings[str(rid)] = ring

    # Marker on the plan, clear of tags and marks.
    plan_rows = _rows(send, "S-DRAW::Plan")
    markers = [r for r in plan_rows if _attr(r).get("forsk:role") == "section_marker"]
    texts = [_box(r) for r in plan_rows if _attr(r).get("forsk:role") in ("room_tag", "opening_mark")]
    letters_on_plan = sorted({str(_attr(r).get("forsk:section")) for r in markers if _attr(r).get("forsk:symbol") == "letter"})
    on_tags = 0
    for row in markers:
        box = _box(row)
        if box and any(t and _overlap(box, t) for t in texts):
            on_tags += 1
    letter_count = sum(1 for r in markers if _attr(r).get("forsk:symbol") == "letter")
    if plan.get("section_markers") != list(LETTERS) or letters_on_plan != list(LETTERS) or letter_count != 2 * len(LETTERS):
        reasons.append(f"plan markers {plan.get('section_markers')} letters {letter_count}")
    if plan.get("section_markers_blocked"):
        reasons.append(f"plan markers blocked {plan.get('section_markers_blocked')}")
    if on_tags:
        reasons.append(f"plan markers on tags or marks {on_tags}")

    summary = []
    # What the model gives. Ground: the lowest slab's underside, else the lowest wall base.
    # Floors: the slab tops, else (below, per section) the floor of each crossed room.
    if floors:
        ground_expect = min(b[2] for b in floors)
    elif wall_boxes:
        ground_expect = min(b[2] for b in wall_boxes)
    else:
        ground_expect = None
    slab_tops = sorted({round(b[5], 1) for b in floors})
    roof_top = max(b[5] for b in roofs) if roofs else None
    roof_under = min(b[2] for b in roofs) if roofs else None
    # An expectation the model cannot give fails the run; it is never compared as None.
    if not wall_boxes:
        reasons.append("model has no walls: nothing to cut")
    if ground_expect is None:
        reasons.append("no expected ground: the model has no slab and no wall")
    if roof_top is None or roof_under is None:
        reasons.append("no expected gesims/møne or free height: the model has no roof (A-ROOF, include_hidden)")
    if not rings:
        reasons.append("no expected free height: rooms_detect gave no room outline")
    for letter in LETTERS:
        view = f"section_{letter.lower()}"
        page = pages.get(view) or {}
        record = page.get("section") or {}
        line = defs.get(letter) or {}
        a, b = line.get("a") or [0, 0], line.get("b") or [1, 0]
        if not record:
            reasons.append(f"{letter} page missing: {packed.get('message')}")
            continue
        if page.get("view_title") != f"Section {letter}–{letter}":
            reasons.append(f"{letter} title {page.get('view_title')!r}")

        # Poché per cut wall: the walls whose box the plane cuts, by forsk:id.
        cut = {str(_attr(w).get("forsk:id") or w.get("id")) for w in walls if (bx := _box(w)) and _line_hits_box(bx, a, b)}
        rows = _rows(send, f"S-DRAW::Section {letter}")
        poche: dict[str, int] = {}
        for row in rows:
            attr = _attr(row)
            if attr.get("forsk:role") == "section_fill" and attr.get("forsk:source"):
                poche[attr["forsk:source"]] = poche.get(attr["forsk:source"], 0) + 1
        filled = sum(1 for wid in cut if poche.get(wid, 0) >= 1)
        if not cut or filled != len(cut) or set(record.get("cut_walls") or []) != cut:
            reasons.append(f"{letter} poché walls {filled}/{len(cut)} reported {sorted(record.get('cut_walls') or [])}")
        if any(str(_attr(r).get("forsk:view")) != view for r in rows):
            reasons.append(f"{letter} objects not stamped {view}")

        # Ground line at the lowest slab's underside.
        grounds = [r for r in rows if _attr(r).get("forsk:role") == "ground_line"]
        ground_ok = bool(grounds) and ground_expect is not None and all(
            abs(float(_attr(r).get("forsk:z")) - ground_expect) <= TOL for r in grounds)
        if not ground_ok and ground_expect is not None:
            reasons.append(f"{letter} ground line {len(grounds)} z {[ _attr(r).get('forsk:z') for r in grounds[:1]]} expected {ground_expect}")

        # Rooms the line runs through, and the floors they stand on.
        crossing = [rid for rid, ring in rings.items() if crossed([(p[0], p[1]) for p in ring], a, b)]
        if not crossing:
            reasons.append(f"{letter} crosses no room")

        # Level marks: every slab top (no slab: each crossed room's floor), the ground, gesims and møne at the roof top.
        marks = {}
        for row in rows:
            attr = _attr(row)
            if attr.get("forsk:role") == "level" and attr.get("forsk:symbol") == "text":
                marks.setdefault(attr.get("forsk:level_kind"), []).append(float(attr.get("forsk:level_z")))
        tops = slab_tops or sorted({round(rings[rid][0][2], 1) for rid in crossing})
        want = {
            "floor": tops,
            "ground": [ground_expect] if ground_expect is not None else [],
            "gesims,mone": [roof_top] if roof_top is not None else [],
        }
        level_ok = True
        for kind, zs in want.items():
            got_z = sorted(round(z, 1) for z in marks.get(kind, []))
            if not zs:
                level_ok = False
                reasons.append(f"{letter} no expected {kind} level from the model")
            elif got_z != sorted(round(z, 1) for z in zs):
                level_ok = False
                reasons.append(f"{letter} {kind} levels {got_z} expected {sorted(round(z, 1) for z in zs)}")

        # Free height in each room the line runs through.
        frees = {}
        for row in rows:
            attr = _attr(row)
            if attr.get("forsk:role") == "free_height" and attr.get("forsk:symbol") == "text":
                frees[attr.get("forsk:room")] = (float(attr.get("forsk:free_height")), float(attr.get("forsk:floor_z")))
        free_ok = 0
        for rid in crossing:
            got = frees.get(rid)
            floor_z = rings[rid][0][2]
            if roof_under is None:
                continue
            expect = round(roof_under - floor_z)
            if got and abs(got[0] - expect) <= TOL and abs(got[1] - floor_z) <= TOL:
                free_ok += 1
            else:
                reasons.append(f"{letter} free height {rid}={got} expected ({expect}, {floor_z})")
        summary.append(
            f"{letter} poché {filled}/{len(cut)} ground {'ok' if ground_ok else 'bad'} "
            f"levels {sum(len(v) for v in marks.values())} free {free_ok}/{len(crossing)} 1:{page.get('scale')}"
        )

    pdf_result = send("export_pdf", {"path": pdf})
    written = {str(name) for name in pdf_result.get("pages") or []}
    missing = [p.get("view") for p in packed.get("pages") or [] if str(p.get("page")) not in written]
    if missing:
        reasons.append(f"pdf missing {missing}: {pdf_result.get('message')}")

    on_plan = ",".join(plan.get("section_markers") or []) or "none"
    print(f"    sections {label} markers {on_plan} on tags {on_tags}; " + "; ".join(summary)
          + f"; pdf {len(written)}/{len(packed.get('pages') or [])}")
    for reason in reasons:
        failures.append(f"{label} sections: {reason}")
    return packed
