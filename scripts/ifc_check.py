#!/usr/bin/env python3
"""Check an IFC the R4 export wrote (export_ifc), with ifcopenshell.

Every door and window fills an opening that voids its own host wall; each
space has a Name, a LongName and Qto_SpaceBaseQuantities.NetFloorArea;
every standard wall has a material layer set usage; ifcopenshell.validate
finds no errors; ifcopenshell.geom makes a shape for every wall. With
--expect garage, the counts are at least the garage smoke's (4 walls, a
floor slab and a roof slab, 1 space), exactly 1 roof and 1 storey, and at
least one door or window. With --expect stair, the garage's counts plus
exactly one IfcStair. Every IfcStair aggregates an IfcStairFlight with two
risers or more, a riser height, a tread length, and a shape whose height
is NumberOfRisers - 1 times the riser height (the top step is the floor).

Usage:
  python3 scripts/ifc_check.py /tmp/forsk-ifc-garage.ifc --expect garage
  python3 scripts/ifc_check.py /tmp/forsk-ifc-stair.ifc --expect stair
Install first: python -m pip install ifcopenshell
"""

from __future__ import annotations

import argparse
import logging
import sys

# At least this many; the storey and the roof exactly.
EXPECT = {
    "garage": {"IfcWall": 4, "IfcSlab": 2, "IfcRoof": 1, "IfcSpace": 1, "IfcBuildingStorey": 1},
    "stair": {"IfcWall": 4, "IfcSlab": 2, "IfcRoof": 1, "IfcSpace": 1, "IfcBuildingStorey": 1, "IfcStair": 1},
}
EXACT = {"IfcRoof", "IfcBuildingStorey", "IfcStair"}


def check(model, expect: dict | None = None, shapes: bool = True) -> list[str]:
    """What is wrong with the file. Empty when it passes."""
    import ifcopenshell.util.element as element

    problems = []
    for cls, want in (expect or {}).items():
        got = len(model.by_type(cls))
        if got < want or (cls in EXACT and got != want):
            problems.append(f"{cls} {got}, want {want if cls in EXACT else f'{want} or more'}")
    fills = model.by_type("IfcDoor") + model.by_type("IfcWindow")
    if expect is not None and not fills:
        problems.append("no door or window")

    for item in fills:
        rel = item.FillsVoids
        opening = rel[0].RelatingOpeningElement if rel else None
        voids = opening.VoidsElements if opening else None
        host = voids[0].RelatingBuildingElement if voids else None
        if host is None or not host.is_a("IfcWall"):
            problems.append(f"{item.is_a()} {item.Name} fills no opening in a wall")

    for space in model.by_type("IfcSpace"):
        qto = element.get_psets(space).get("Qto_SpaceBaseQuantities", {})
        if not space.Name or not space.LongName:
            problems.append(f"space {space.Name!r} has no Name or LongName")
        if not qto.get("NetFloorArea"):
            problems.append(f"space {space.Name!r} has no NetFloorArea")

    for wall in model.by_type("IfcWall"):
        if wall.PredefinedType != "STANDARD":
            continue
        material = element.get_material(wall)
        if material is None or not material.is_a("IfcMaterialLayerSetUsage"):
            problems.append(f"standard wall {wall.Tag} has no layer set usage")

    problems += stair_problems(model, shapes)
    problems += validate(model)
    if shapes:
        problems += walls_without_shape(model)
    return problems


def stair_problems(model, shapes: bool = True) -> list[str]:
    """R5: each stair is a straight run whose flight carries its figures and, with shapes, a body of the right height."""
    import ifcopenshell.util.element as element

    problems = []
    for stair in model.by_type("IfcStair"):
        flights = [p for p in element.get_decomposition(stair) if p.is_a("IfcStairFlight")]
        if stair.PredefinedType != "STRAIGHT_RUN_STAIR":
            problems.append(f"stair {stair.Tag} is {stair.PredefinedType}, want STRAIGHT_RUN_STAIR")
        if len(flights) != 1:
            problems.append(f"stair {stair.Tag} has {len(flights)} flights, want 1")
            continue
        flight = flights[0]
        risers, riser, going = flight.NumberOfRisers, flight.RiserHeight, flight.TreadLength
        if not risers or risers < 2 or not riser or riser <= 0 or not going or going <= 0:
            problems.append(f"stair {stair.Tag} flight has risers {risers}, riser {riser}, going {going}")
            continue
        if shapes:
            height = shape_height(model, flight)
            want = (risers - 1) * riser
            if height is None:
                problems.append(f"no shape for stair {stair.Tag}")
            elif abs(height - want) > 1.0:
                problems.append(f"stair {stair.Tag} body is {height:.1f} high, want {want:.1f}")
    return problems


def shape_height(model, product) -> float | None:
    """The product's body height in the file's length unit, or None when it has no shape."""
    import ifcopenshell.geom
    import ifcopenshell.util.unit as unit

    settings = ifcopenshell.geom.settings()
    try:
        shape = ifcopenshell.geom.create_shape(settings, product)
    except Exception:  # noqa: BLE001 - any failure is "no shape"
        return None
    zs = shape.geometry.verts[2::3]
    if not zs:
        return None
    # The geometry comes back in metres; the figures are in the file's unit.
    return (max(zs) - min(zs)) / unit.calculate_unit_scale(model)


def check_path(path, expect: str | None = "garage") -> list[str]:
    """The checks on a file, for the export smoke. A file that does not open is one problem."""
    import ifcopenshell

    try:
        model = ifcopenshell.open(str(path))
    except Exception as e:  # noqa: BLE001
        return [f"{path} does not open: {e}"]
    return check(model, EXPECT.get(expect) if expect else None)


def validate(model) -> list[str]:
    import ifcopenshell.validate

    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(model, logger)
    errors = [s for s in logger.statements if str(s.get("level", "")).lower() in ("error", "40")]
    return [f"validate: {e.get('message')}" for e in errors[:5]]


def walls_without_shape(model) -> list[str]:
    import ifcopenshell.geom

    settings = ifcopenshell.geom.settings()
    missing = []
    for wall in model.by_type("IfcWall"):
        try:
            # The Body, not the Axis a standard wall also carries.
            body = next(r for r in wall.Representation.Representations if r.RepresentationIdentifier == "Body")
            shape = ifcopenshell.geom.create_shape(settings, wall, body)
            if not shape.geometry.verts:
                missing.append(wall.Tag)
        except Exception:  # noqa: BLE001 - any failure is "no shape"
            missing.append(wall.Tag)
    return [f"no shape for walls {missing}"] if missing else []


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    parser.add_argument("path")
    parser.add_argument("--expect", choices=sorted(EXPECT))
    args = parser.parse_args(argv)
    logging.disable(logging.WARNING)
    import ifcopenshell

    try:
        model = ifcopenshell.open(args.path)
    except Exception as e:  # noqa: BLE001
        print(f"FAIL {args.path} does not open: {e}")
        return 1
    counts = {cls: len(model.by_type(cls)) for cls in ("IfcWall", "IfcDoor", "IfcWindow", "IfcOpeningElement", "IfcSlab", "IfcSpace", "IfcStair")}
    problems = check(model, EXPECT.get(args.expect))
    print(" · ".join(f"{k[3:]} {v}" for k, v in counts.items()))
    for p in problems[:20]:
        print("FAIL " + p)
    print("ifc check: " + ("PASS" if not problems else "FAIL"))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
