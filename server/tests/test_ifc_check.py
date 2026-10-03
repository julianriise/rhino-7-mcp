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
