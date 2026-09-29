"""The office smoke's room-name checks, headless, on the office DXF.

The rooms are what RoomDetect gives on that DXF (OfficeRoomsTests pins the
same names): id, name, and inside point. The checks must pass them and catch
the two ways F2.5 went wrong before: every room read as Rom, and a name taken
from another room.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))

import plan_smoke  # noqa: E402

OFFICE_DXF = ROOT / "tests" / "SoftParam.Tests" / "fixtures" / "office_2D.dxf"

ROOMS = [
    ("rd-01", "Konferanserom", 8881, 24785),
    ("rd-02", "Kontorplasser", 16392, 23238),
    ("rd-03", "Kontor", 22665, 18989),
    ("rd-04", "Kontor", 25175, 18989),
    ("rd-05", "WC", 27258, 20439),
    ("rd-06", "WC", 27258, 19100),
    ("rd-07", "Bøttekott", 27258, 17645),
    ("rd-08", "Rom", 28920, 23240),
    ("rd-09", "Fax/kopi/printer", 29435, 15566),
    ("rd-10", "Open Office", 31535, 5552),
    ("rd-11", "Hall", 8881, 15572),
    ("rd-12", "WC", 10112, 17840),
    ("rd-13", "Data/arkiv", 12630, 18992),
    ("rd-14", "Wet Room", 10617, 19390),
    ("rd-15", "Konferanserom", 33936, 18998),
    ("rd-16", "Kontor", 34280, 15569),
]


def rooms(**renamed):
    return [
        {"id": rid, "name": renamed.get(rid.replace("-", "_"), name), "x": float(x), "y": float(y)}
        for rid, name, x, y in ROOMS
    ]


@pytest.fixture(scope="module")
def rings():
    return plan_smoke.dxf_rooms(OFFICE_DXF)


def test_dxf_rooms_are_the_fifteen_labelled_rings_with_decoded_labels(rings):
    assert len(rings) == 15
    labels = sorted(text for texts, _ring in rings for text in texts)
    assert "Bøttekott" in labels
    corridor = [texts for texts, _ring in rings if "Brannskap" in texts]
    assert corridor == [["Brannskap", "Fax/kopi/printer"]]


def test_detected_names_pass(rings, capsys):
    failures = []
    plan_smoke.check_room_names(rooms(), rings, failures)
    assert failures == []
    assert "room names 16 from DXF labels 15 Rom 1 wrong 0" in capsys.readouterr().out


def test_every_room_rom_fails(rings):
    failures = []
    plan_smoke.check_room_names([dict(room, name="Rom") for room in rooms()], rings, failures)
    assert failures and failures[0].startswith("office room names wrong 15")


def test_a_label_from_another_room_fails(rings):
    failures = []
    plan_smoke.check_room_names(rooms(rd_03="WC"), rings, failures)
    assert failures and "rd-03 'WC'" in failures[0]


def test_an_undecoded_label_fails_in_one_short_line(rings):
    # The live office run: Rhino read B\U+00F8ttekott as B00F8ttekott.
    failures = []
    plan_smoke.check_room_names(rooms(rd_07="B00F8ttekott"), rings, failures)
    assert failures == [
        "office room names wrong 2: rd-07 'B00F8ttekott' not a label in its DXF room; "
        "DXF room 'Bøttekott' names no room"
    ]


def test_tag_text_is_checked_against_the_detected_name():
    tag = {"attributes": {"forsk:role": "room_tag", "forsk:tag": "name", "forsk:room_id": "rd-07"}}
    failures = []
    plan_smoke.check_tag_names([dict(tag, text="Bøttekott")], rooms(), "office fit", failures)
    assert failures == []
    plan_smoke.check_tag_names([dict(tag, text="Rom")], rooms(), "office fit", failures)
    assert failures == ["office fit names not as detected: rd-07='Rom' detected 'Bøttekott'"]


FIT_PAGE = {
    "room_tags": 16, "room_areas_dropped": 2, "rooms_leader": 2, "rooms_overflow": 0, "rooms_too_small": 0,
    "rooms_no_outline": 0, "rooms_untagged": [],
    "rooms_leading": ["rd-07 Bøttekott: name 1.75 m wide, room 1.46 x 1.42 m",
                      "rd-14 Wet Room: name 2.02 m wide, room 1.31 x 1.10 m"],
    "rooms_overflowing": [],
}


def test_room_count_line_names_the_rooms_on_leaders_by_id(capsys):
    failures = []
    plan_smoke.check_room_count(FIT_PAGE, 16, "office fit", failures)
    assert failures == []
    assert capsys.readouterr().out.splitlines() == [
        "    office fit room tags 16/16 area dropped 2 leader 2 overflow 0 too small 0 no outline 0",
        "    office fit room tags leader rd-07 Bøttekott: name 1.75 m wide, room 1.46 x 1.42 m; "
        "rd-14 Wet Room: name 2.02 m wide, room 1.31 x 1.10 m",
    ]


@pytest.mark.parametrize("change", [
    {"room_tags": 15},                                  # a room in no count
    {"room_tags": 15, "rooms_too_small": 1},            # too small with no reason
    {"rooms_leader": 1},                                # a leader not listed
    # F5.2: a name running past its room (no clear spot for a leader) fails.
    {"rooms_overflow": 1, "rooms_overflowing": ["rd-03 Wet Room: name 2.02 m wide, room 1.31 x 1.10 m"]},
    {"rooms_overflow": 5, "room_areas_dropped": 4},     # overflow keeps no area line
    {"rooms_no_outline": 1, "room_tags": 15, "rooms_untagged": ["rd-01: no outline to tag"]},
])
def test_room_count_line_fails_when_the_counts_do_not_add_up(change):
    failures = []
    plan_smoke.check_room_count(dict(FIT_PAGE, **change), 16, "office fit", failures)
    assert len(failures) == 1 and len(failures[0]) <= 120


def tag(part, box, ring, overflow=False):
    attributes = {
        "forsk:role": "room_tag", "forsk:tag": part, "forsk:room_id": "rd-07",
        "forsk:text_height": "312.5", "forsk:paper_height": "2.5",
        "forsk:room": ";".join(f"{x},{y}" for x, y in ring),
    }
    if overflow:
        attributes["forsk:overflow"] = "1"
    return {"attributes": attributes, "bounding_box": [[box[0], box[1], 0], [box[2], box[3], 0]]}


def test_an_overflowing_name_may_run_past_its_room_but_is_centred_in_it(capsys):
    room = [(0, 0), (1460, 0), (1460, 1420), (0, 1420)]
    wide = (-150, 540, 1610, 880)  # 1.76 m name in a 1.46 m room, centred
    page = {"scale": 125, "room_tags": 1, "room_areas_dropped": 1, "rooms_overflow": 1}
    failures = []
    plan_smoke.check_room_tags([tag("name", wide, room, overflow=True)], page, failures)
    assert failures == []
    assert "room tag text height 312 paper 2.5 inside" in capsys.readouterr().out
    # Not stamped overflow: the same box is outside its room.
    plan_smoke.check_room_tags([tag("name", wide, room)], dict(page, rooms_overflow=0), failures)
    assert any("outside its room" in item for item in failures)
    # Stamped, but its centre is out of the room.
    failures = []
    plan_smoke.check_room_tags([tag("name", (1300, 540, 3060, 880), room, overflow=True)], page, failures)
    assert any("outside its room" in item for item in failures)
    # The page's overflow count must match the stamped tags.
    failures = []
    plan_smoke.check_room_tags([tag("name", wide, room, overflow=True)], dict(page, rooms_overflow=0), failures)
    assert failures and "overflow" in failures[0]
