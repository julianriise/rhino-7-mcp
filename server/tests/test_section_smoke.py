"""Headless checks for scripts/section_smoke.py: the F5.3 smoke step against a
fake Rhino that serves the garage smoke's model. A right sheet passes; a
missing poché, a wrong free height or level, and a marker on a tag fail."""

import copy
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import section_smoke  # noqa: E402

WALLS = {
    "w-south": ((0, 0, 0), (8000, 200, 3000)),
    "w-north": ((0, 3800, 0), (8000, 4000, 3000)),
    "w-west": ((0, 0, 0), (200, 4000, 3000)),
    "w-east": ((7800, 0, 0), (8000, 4000, 3000)),
}
LINES = {"A": ([0, 2000], [8000, 2000], "long"), "B": ([4000, 4000], [4000, 0], "cross")}
CUT = {"A": ["w-west", "w-east"], "B": ["w-south", "w-north"]}


def row(role=None, box=((0, 0, 0), (1, 1, 0)), kind="drawing", **attrs):
    attributes = {"forsk:kind": kind, **attrs}
    if role:
        attributes["forsk:role"] = role
    return {"id": attrs.get("forsk:id", role or "x"), "bounding_box": [list(box[0]), list(box[1])], "attributes": attributes}


def section_rows(letter):
    view = f"section_{letter.lower()}"
    stamp = {"forsk:view": view, "forsk:section": letter}
    rows = [row("section_fill", **stamp, **{"forsk:source": wall}) for wall in CUT[letter]]
    rows.append(row("section_fill", **stamp, **{"forsk:source": "floor"}))
    rows.append(row("ground_line", **stamp, **{"forsk:z": "-400"}))
    for kind, z in (("ground", -400), ("floor", 0), ("gesims,mone", 3000)):
        rows.append(row("level", **stamp, **{"forsk:symbol": "text", "forsk:level_kind": kind, "forsk:level_z": str(z)}))
    rows.append(row("free_height", **stamp, **{
        "forsk:symbol": "text", "forsk:room": "rd-01", "forsk:free_height": "2800", "forsk:floor_z": "0"}))
    return rows


def fake_rhino(model=None):
    model = model or {}
    plan = model.get("plan") or [
        row("room_tag", ((3500, 1800, 0), (4500, 2200, 0))),
        row("opening_mark", ((1100, -400, 0), (1300, -300, 0))),
    ] + [
        row("section_marker", ((x, y, 0), (x + 60, y + 90, 0)), **{"forsk:section": letter, "forsk:symbol": symbol})
        for letter, ends in (("A", ((-1500, 2100), (9500, 2100))), ("B", ((4100, 5500), (4100, -1500))))
        for x, y in ends
        for symbol in ("letter", "stroke")
    ]
    sections = model.get("sections") or {letter: section_rows(letter) for letter in "AB"}
    calls = []

    def send(cmd, params):
        calls.append(cmd)
        if cmd == "panel_daylight":
            return {"intent": model.get("intent", "sheets")}
        if cmd in ("section_clear", "clear_layouts"):
            return {}
        if cmd == "section_add":
            a, b, axis = LINES[params["letter"]]
            letter = params["letter"]
            return {"section": {"letter": letter, "view": f"section_{letter.lower()}", "a": a, "b": b, "axis": axis}}
        if cmd == "layout_pack":
            pages = [{"view": "plan", "page": "Forsk — Plan", "section_markers": ["A", "B"], "section_markers_blocked": []}]
            for letter in "AB":
                pages.append({
                    "view": f"section_{letter.lower()}", "page": f"Forsk — Section {letter}", "scale": 50,
                    "view_title": f"Snitt {letter}–{letter}", "section": {"cut_walls": CUT[letter]},
                })
            return {"pages": pages, "message": "Laid out 3 page(s)."}
        if cmd == "get_objects":
            layer = params["layer_filter"]
            if params.get("offset"):
                return {"objects": []}
            if layer == "A-WALL":
                return {"objects": [row(box=box, kind="wall", **{"forsk:id": wid}) for wid, box in WALLS.items()]}
            if layer == "A-FLOR":
                return {"objects": [row(box=((0, 0, -400), (8000, 4000, 0)), kind="floor")]}
            if layer == "A-ROOF":
                return {"objects": [row(box=((-500, -500, 2800), (8500, 4500, 3000)), kind="roof")]}
            if layer == "S-DRAW::Plan":
                return {"objects": plan}
            if layer.startswith("S-DRAW::Section "):
                return {"objects": sections[layer[-1]]}
            return {"objects": []}
        if cmd == "rooms_detect":
            return {"rooms": [{"id": "rd-01", "name": "Garasje"}], "ids": ["m-01"]}
        if cmd == "get_object_info":
            return {"geometry": {"points": [[200, 200, 0], [7800, 200, 0], [7800, 3800, 0], [200, 3800, 0], [200, 200, 0]]}}
        if cmd == "export_pdf":
            return {"pages": ["Forsk — Plan", "Forsk — Section A", "Forsk — Section B"], "message": "ok"}
        raise AssertionError(cmd)

    return send, calls


def run(model=None):
    send, calls = fake_rhino(model)
    failures = []
    section_smoke.run_step(send, "garage", "Garasje", "/tmp/x.pdf", failures,
                           panel=lambda action, extra: send("panel_daylight", {"action": action, **extra}))
    return failures, calls


def test_a_right_sheet_passes():
    failures, calls = run()
    assert failures == []
    assert calls.count("section_add") == 2
    assert calls.index("layout_pack") < calls.index("export_pdf")


def test_a_cut_wall_without_poche_fails():
    sections = {letter: section_rows(letter) for letter in "AB"}
    sections["A"] = [r for r in sections["A"] if r["attributes"].get("forsk:source") != "w-east"]
    failures, _ = run({"sections": sections})
    assert any("A poché walls 1/2" in f for f in failures)


def test_a_wrong_free_height_or_level_fails():
    sections = {letter: section_rows(letter) for letter in "AB"}
    for r in sections["B"]:
        if r["attributes"].get("forsk:role") == "free_height":
            r["attributes"]["forsk:free_height"] = "2700"
        if r["attributes"].get("forsk:level_kind") == "gesims,mone":
            r["attributes"]["forsk:level_z"] = "2800"
    failures, _ = run({"sections": sections})
    assert any("B free height rd-01=2700.0 expected 2800" in f for f in failures)
    assert any(f.startswith("garage sections: B levels") for f in failures)


def test_a_missing_ground_line_fails():
    sections = {letter: [r for r in section_rows(letter) if r["attributes"].get("forsk:role") != "ground_line"] for letter in "AB"}
    failures, _ = run({"sections": sections})
    assert any("A ground line 0" in f for f in failures)


def test_a_marker_on_a_tag_fails():
    send, _ = fake_rhino()
    plan = copy.deepcopy(send("get_objects", {"layer_filter": "S-DRAW::Plan"})["objects"])
    plan.append(row("section_marker", ((3600, 1900, 0), (3700, 2000, 0)), **{"forsk:section": "A", "forsk:symbol": "stroke"}))
    failures, _ = run({"plan": plan})
    assert any("plan markers on tags or marks 1" in f for f in failures)


def test_an_intent_that_misses_sheets_fails():
    failures, _ = run({"intent": "general"})
    assert any("intent 'section A through the Garasje'=general" in f for f in failures)


def test_line_helpers():
    ring = [(200, 200), (7800, 200), (7800, 3800), (200, 3800)]
    assert section_smoke.crossed(ring, (0, 2000), (8000, 2000))
    # Along a wall face, outside the room: not crossed.
    assert not section_smoke.crossed(ring, (0, 100), (8000, 100))
    box = (0, 0, 0, 200, 4000, 3000)
    assert section_smoke._line_hits_box(box, (0, 2000), (8000, 2000))
    assert not section_smoke._line_hits_box(box, (4000, 4000), (4000, 0))
