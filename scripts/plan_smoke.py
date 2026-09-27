"""Plan checks shared by the office and garage smokes."""

from __future__ import annotations

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
