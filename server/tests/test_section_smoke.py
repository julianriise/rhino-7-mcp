"""Headless checks for scripts/section_smoke.py: the F5.3 smoke step against a
fake Rhino that serves the garage smoke's model (A-ROOF hidden, as in Rhino).
A right sheet passes, with a slab or without; a missing poché, a wrong or
missing free height or level, an expectation the model cannot give, and a
marker on a tag fail."""

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


SLAB = [row(box=((0, 0, -400), (8000, 4000, 0)), kind="floor")]


def marker(rid, x0=200, x1=7800):
    """A room marker as get_object_info gives it: its room id stamped, its outline's points."""
    return {"name": rid, "attributes": {"forsk:kind": "room", "forsk:room_id": rid},
            "geometry": {"points": [[x0, 200, 0], [x1, 200, 0], [x1, 3800, 0], [x0, 3800, 0], [x0, 200, 0]]}}


def section_rows(letter, ground=-400):
    """The sheet's records. ground -400 is the slab's underside; 0 is a slabless garage's wall base."""
    view = f"section_{letter.lower()}"
    stamp = {"forsk:view": view, "forsk:section": letter}
    rows = [row("section_fill", **stamp, **{"forsk:source": wall}) for wall in CUT[letter]]
    rows.append(row("section_fill", **stamp, **{"forsk:source": "roof"}))
    rows.append(row("ground_line", **stamp, **{"forsk:z": str(ground)}))
    for kind, z in (("ground", ground), ("floor", 0), ("gesims,mone", 3000)):
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
                return {"objects": model.get("floors", SLAB)}
            if layer == "A-ROOF":
                # A-ROOF is a hidden layer: only include_hidden finds it, as in Rhino.
                if not params.get("include_hidden"):
                    return {"objects": []}
                return {"objects": model.get("roofs", [row(box=((-500, -500, 2800), (8500, 4500, 3000)), kind="roof")])}
            if layer == "S-DRAW::Plan":
                return {"objects": plan}
            if layer.startswith("S-DRAW::Section "):
                return {"objects": sections[layer[-1]]}
            return {"objects": []}
        if cmd == "rooms_detect":
            return model.get("rooms_detect") or {"rooms": [{"id": "rd-01", "name": "Garasje"}], "ids": ["m-01"]}
        if cmd == "get_object_info":
            return (model.get("markers") or {"m-01": marker("rd-01")})[params["id"]]
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


def slabless(**extra):
    """The live garage: walls and a roof, no slab. Ground at the wall base, floor at the room's floor."""
    return {"floors": [], "sections": {letter: section_rows(letter, ground=0) for letter in "AB"}, **extra}


def test_a_garage_without_a_slab_reads_its_floor_and_ground_from_the_walls_and_room():
    failures, _ = run(slabless())
    assert failures == []


def test_a_slabless_garage_drawn_with_the_slab_ground_fails():
    failures, _ = run({"floors": []})
    assert any("A ground line 1" in f and "expected 0" in f for f in failures)


def test_a_model_without_a_roof_fails_instead_of_comparing_none_to_none():
    sections = {letter: [r for r in section_rows(letter)
                         if r["attributes"].get("forsk:level_kind") != "gesims,mone"
                         and r["attributes"].get("forsk:role") != "free_height"] for letter in "AB"}
    failures, _ = run({"roofs": [], "sections": sections})
    assert any("model has no roof" in f for f in failures)
    assert any("A no expected gesims,mone level" in f for f in failures)


def test_a_model_without_walls_or_slab_has_no_ground_and_fails():
    send, _ = fake_rhino()
    failures = []
    def bare(cmd, params):
        if cmd == "get_objects" and params["layer_filter"] in ("A-WALL", "A-FLOR"):
            return {"objects": []}
        return send(cmd, params)
    section_smoke.run_step(bare, "garage", "Garasje", "/tmp/x.pdf", failures)
    assert any("no expected ground" in f for f in failures)
    assert any("model has no walls" in f for f in failures)


def test_each_room_keeps_its_own_outline_when_rooms_and_ids_differ_in_order():
    """rooms_detect lists the rooms detected first and the ids in marker order; the
    live office had them reversed. A reads both rooms, B (x 4000) only rd-02."""
    def free(letter, rid):
        return row("free_height", **{"forsk:view": f"section_{letter.lower()}", "forsk:section": letter,
                                     "forsk:symbol": "text", "forsk:room": rid, "forsk:free_height": "2800", "forsk:floor_z": "0"})
    sections = {letter: [r for r in section_rows(letter) if r["attributes"].get("forsk:role") != "free_height"] for letter in "AB"}
    sections["A"] += [free("A", "rd-01"), free("A", "rd-02")]
    sections["B"] += [free("B", "rd-02")]
    failures, _ = run({
        "rooms_detect": {"rooms": [{"id": "rd-01", "name": "Kontor"}, {"id": "rd-02", "name": "Lager"}], "ids": ["m-02", "m-01"]},
        "markers": {"m-01": marker("rd-01", 200, 3900), "m-02": marker("rd-02", 3900, 7800)},
        "sections": sections,
    })
    assert failures == []


def test_a_missing_free_height_fails_with_the_expected_value():
    sections = {letter: [r for r in section_rows(letter) if r["attributes"].get("forsk:role") != "free_height"] for letter in "AB"}
    failures, _ = run({"sections": sections})
    assert any("A free height rd-01=None expected (2800, 0.0)" in f for f in failures)


def test_a_missing_floor_level_fails():
    sections = {letter: [r for r in section_rows(letter) if r["attributes"].get("forsk:level_kind") != "floor"] for letter in "AB"}
    failures, _ = run({"sections": sections})
    assert any("A floor levels [] expected [0.0]" in f for f in failures)


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
    assert any("B free height rd-01=(2700.0, 0.0) expected (2800, 0.0)" in f for f in failures)
    assert any("B gesims,mone levels [2800.0] expected [3000.0]" in f for f in failures)


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
