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
    assert failures and "rd-03='WC'" in failures[0]


def test_tag_text_is_checked_against_the_detected_name():
    tag = {"attributes": {"forsk:role": "room_tag", "forsk:tag": "name", "forsk:room_id": "rd-07"}}
    failures = []
    plan_smoke.check_tag_names([dict(tag, text="Bøttekott")], rooms(), "office fit", failures)
    assert failures == []
    plan_smoke.check_tag_names([dict(tag, text="Rom")], rooms(), "office fit", failures)
    assert failures == ["office fit names not as detected: rd-07='Rom' detected 'Bøttekott'"]


def test_room_count_line_lists_untagged_rooms_by_id(capsys):
    page = {
        "room_tags": 14, "room_areas_dropped": 2, "rooms_too_small": 2, "rooms_unbounded": 0,
        "rooms_untagged": ["rd-07 Bøttekott: name 1.75 m wide, room 1.46 x 1.42 m",
                           "rd-14 Wet Room: name 2.06 m wide, room 1.32 x 1.10 m"],
    }
    failures = []
    plan_smoke.check_room_count(page, 16, "office fit", failures)
    assert failures == []
    assert capsys.readouterr().out.startswith(
        "    office fit room tags 14/16 area dropped 2 too small 2 unbounded 0: rd-07 Bøttekott"
    )
    # A room in no count, or too small with no reason, fails.
    plan_smoke.check_room_count(dict(page, rooms_too_small=1), 16, "office fit", failures)
    plan_smoke.check_room_count(dict(page, rooms_untagged=[]), 16, "office fit", failures)
    assert len(failures) == 2
