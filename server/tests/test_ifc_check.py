"""Headless checks for scripts/ifc_check.py (R4): a door that fills no
opening fails, and the garage counts are minimums except the storey and
the roof. The garage file itself is written by IfcExportTests and checked
with the script as part of the gate."""

import sys
from pathlib import Path

import pytest

ifcopenshell = pytest.importorskip("ifcopenshell")
import ifcopenshell.api  # noqa: E402

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import ifc_check  # noqa: E402


def model_with(*classes):
    model = ifcopenshell.file(schema="IFC4")
    ifcopenshell.api.run("root.create_entity", model, ifc_class="IfcProject", name="p")
    for cls in classes:
        ifcopenshell.api.run("root.create_entity", model, ifc_class=cls, name=cls)
    return model


def test_a_door_in_no_opening_fails():
    problems = ifc_check.check(model_with("IfcDoor"), None, shapes=False)
    assert any("IfcDoor" in p and "fills no opening" in p for p in problems), problems


def test_garage_counts_are_minimums_but_one_storey_and_one_roof():
    expect = ifc_check.EXPECT["garage"]
    five = model_with("IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcSlab", "IfcSlab",
                      "IfcRoof", "IfcSpace", "IfcBuildingStorey", "IfcDoor")
    problems = ifc_check.check(five, expect, shapes=False)
    assert not any("want" in p for p in problems), problems
    two = model_with("IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcSlab", "IfcSlab", "IfcRoof",
                     "IfcSpace", "IfcBuildingStorey", "IfcBuildingStorey", "IfcDoor")
    assert "IfcBuildingStorey 2, want 1" in ifc_check.check(two, expect, shapes=False)
    three = model_with("IfcWall", "IfcWall", "IfcWall")
    assert "IfcWall 3, want 4 or more" in ifc_check.check(three, expect, shapes=False)


@pytest.mark.parametrize("kind", ["garage", "stair"])
def test_the_smoke_garage_has_its_roof_slab_and_no_floor_slab(kind):
    # The garage smoke draws walls and a roof, no floor: one IfcSlab, the roof's.
    roof_only = model_with("IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcSlab", "IfcRoof",
                           "IfcSpace", "IfcBuildingStorey", "IfcDoor", *(["IfcStair"] if kind == "stair" else []))
    problems = ifc_check.check(roof_only, ifc_check.EXPECT[kind], shapes=False)
    # The counts pass; a bare stair's own checks are another test's.
    assert not [p for p in problems if p.startswith("Ifc") and ", want" in p], problems
    no_slab = model_with("IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcRoof", "IfcSpace", "IfcBuildingStorey", "IfcDoor")
    assert "IfcSlab 0, want 1 or more" in ifc_check.check(no_slab, ifc_check.EXPECT[kind], shapes=False)


def test_a_stair_without_its_flight_fails():
    problems = ifc_check.stair_problems(model_with("IfcStair"), shapes=False)
    assert any("0 flights" in p for p in problems), problems


def test_the_stair_expectation_wants_exactly_one_stair():
    expect = ifc_check.EXPECT["stair"]
    none = model_with("IfcWall", "IfcWall", "IfcWall", "IfcWall", "IfcSlab", "IfcSlab", "IfcRoof",
                      "IfcSpace", "IfcBuildingStorey", "IfcDoor")
    assert "IfcStair 0, want 1" in ifc_check.check(none, expect, shapes=False)


def test_the_written_stair_file_passes_when_present():
    path = Path("/tmp/forsk-ifc-stair.ifc")
    if not path.exists():
        pytest.skip("IfcStairTests writes it")
    assert ifc_check.check_path(path, "stair") == []
