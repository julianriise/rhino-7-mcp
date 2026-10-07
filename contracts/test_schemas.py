#!/usr/bin/env python3
"""
Test script to validate JSON schemas and example payloads.

Run with: python3 contracts/test_schemas.py
"""

import json
import sys
from pathlib import Path

try:
    import jsonschema
    from jsonschema import Draft202012Validator
except ImportError:
    print("Please install jsonschema: pip install jsonschema")
    sys.exit(1)

CONTRACTS_DIR = Path(__file__).parent


def load_schema_with_refs(schema_path: str) -> dict:
    """
    Load a JSON schema and inline all $ref references to common/definitions.json.
    This avoids complex ref resolution issues.
    """
    with open(CONTRACTS_DIR / schema_path) as f:
        schema = json.load(f)

    # Load common definitions
    with open(CONTRACTS_DIR / "common" / "definitions.json") as f:
        definitions = json.load(f)

    # Add definitions to schema
    if "$defs" not in schema:
        schema["$defs"] = {}

    # Merge common definitions
    for key, value in definitions.get("$defs", {}).items():
        schema["$defs"][f"common_{key}"] = value

    # Replace refs to common/definitions.json with local refs
    schema_str = json.dumps(schema)
    schema_str = schema_str.replace("../common/definitions.json#/$defs/", "#/$defs/common_")
    schema = json.loads(schema_str)

    return schema


def validate(schema_path: str, instance: dict) -> bool:
    """Validate an instance against a schema."""
    try:
        schema = load_schema_with_refs(schema_path)
        validator = Draft202012Validator(schema)

        errors = list(validator.iter_errors(instance))
        if errors:
            print(f"  FAIL: {schema_path}")
            for error in errors[:3]:  # Show first 3 errors
                print(f"    - {error.message}")
            return False

        print(f"  PASS: {schema_path}")
        return True
    except Exception as e:
        print(f"  ERROR: {schema_path} - {e}")
        return False


def test_create_object_commands():
    """Test create_object command schemas."""
    print("\n=== Testing create_object commands ===")

    valid_examples = [
        # POINT
        {"type": "POINT", "params": {"x": 0, "y": 0, "z": 0}},
        # LINE
        {"type": "LINE", "params": {"start": [0, 0, 0], "end": [1, 1, 1]}},
        # BOX
        {"type": "BOX", "params": {"width": 1.0, "length": 2.0, "height": 3.0}},
        # BOX with optional fields
        {
            "type": "BOX",
            "name": "MyBox",
            "color": [255, 0, 0],
            "params": {"width": 1.0, "length": 2.0, "height": 3.0},
            "translation": [10, 0, 0]
        },
        # SPHERE
        {"type": "SPHERE", "params": {"radius": 5.0}},
        # CIRCLE
        {"type": "CIRCLE", "params": {"center": [0, 0, 0], "radius": 2.5}},
        # CURVE with explicit degree
        {"type": "CURVE", "params": {"points": [[0, 0, 0], [1, 1, 0], [2, 0, 0]], "degree": 2}},
        # CURVE relying on default degree (3 points -> handler clamps to degree 2)
        {"type": "CURVE", "params": {"points": [[0, 0, 0], [1, 1, 0], [2, 0, 0]]}},
        # CURVE with 4 points and default degree (-> 3)
        {"type": "CURVE", "params": {"points": [[0, 0, 0], [1, 1, 0], [2, 0, 0], [3, 0, 0]]}},
        # CYLINDER
        {"type": "CYLINDER", "params": {"radius": 1.0, "height": 5.0, "cap": True}},
        # CYLINDER without cap (defaults to true)
        {"type": "CYLINDER", "params": {"radius": 1.0, "height": 5.0}},
        # CONE
        {"type": "CONE", "params": {"radius": 2.0, "height": 4.0}},
    ]

    all_passed = True
    for i, example in enumerate(valid_examples):
        obj_type = example.get("type", "?")
        print(f"  Testing {obj_type}...", end=" ")
        if not validate("commands/create_object.json", example):
            all_passed = False

    return all_passed


def test_modify_object_commands():
    """Test modify_object command schemas."""
    print("\n=== Testing modify_object commands ===")

    valid_examples = [
        {"id": "12345678-1234-1234-1234-123456789012"},
        {"name": "MyObject"},
        {"id": "12345678-1234-1234-1234-123456789012", "new_name": "RenamedObject"},
        {"name": "MyObject", "new_color": [0, 255, 0]},
        {"id": "12345678-1234-1234-1234-123456789012", "translation": [1, 2, 3]},
        {"name": "Box1", "rotation": [0, 0, 1.57], "scale": [2, 2, 2]},
    ]

    all_passed = True
    for example in valid_examples:
        if not validate("commands/modify_object.json", example):
            all_passed = False

    return all_passed


def test_delete_object_commands():
    """Test delete_object command schemas."""
    print("\n=== Testing delete_object commands ===")

    valid_examples = [
        {"id": "12345678-1234-1234-1234-123456789012"},
        {"name": "MyObject"},
        {"all": True},
    ]

    all_passed = True
    for example in valid_examples:
        if not validate("commands/delete_object.json", example):
            all_passed = False

    return all_passed


def test_select_objects_commands():
    """Test select_objects command schemas."""
    print("\n=== Testing select_objects commands ===")

    valid_examples = [
        {"filters": {}},
        {"filters": {"name": ["Object1", "Object2"]}},
        {"filters": {"color": [255, 0, 0]}},
        {"filters": {"name": ["Box"], "category": ["furniture"]}, "filters_type": "and"},
        {"filters": {"category": ["walls", "floors"]}, "filters_type": "or"},
    ]

    all_passed = True
    for example in valid_examples:
        if not validate("commands/select_objects.json", example):
            all_passed = False

    return all_passed


def test_layer_commands():
    """Test layer command schemas."""
    print("\n=== Testing layer commands ===")

    all_passed = True

    # create_layer
    print("  create_layer:")
    create_examples = [
        {},
        {"name": "Layer 1"},
        {"name": "Layer 2", "color": [100, 150, 200]},
        {"name": "Sublayer", "parent": "Parent Layer"},
    ]
    for example in create_examples:
        if not validate("commands/create_layer.json", example):
            all_passed = False

    # delete_layer
    print("  delete_layer:")
    delete_examples = [
        {"name": "Layer 1"},
        {"guid": "12345678-1234-1234-1234-123456789012"},
    ]
    for example in delete_examples:
        if not validate("commands/delete_layer.json", example):
            all_passed = False

    # get_or_set_current_layer
    print("  get_or_set_current_layer:")
    layer_examples = [
        {},
        {"name": "Default"},
        {"guid": "12345678-1234-1234-1234-123456789012"},
    ]
    for example in layer_examples:
        if not validate("commands/get_or_set_current_layer.json", example):
            all_passed = False

    return all_passed


def test_new_commands():
    """Positive cases for the commands added in the schema-coverage pass."""
    print("\n=== Testing newly added command schemas ===")

    GUID = "12345678-1234-1234-1234-123456789012"
    cases = [
        ("commands/boolean_union.json", {"object_ids": [GUID, GUID]}),
        ("commands/boolean_difference.json", {"base_id": GUID, "subtract_ids": [GUID]}),
        ("commands/boolean_intersection.json", {"object_ids": [GUID, GUID]}),
        ("commands/create_objects.json", {"Box1": {"type": "BOX", "params": {"width": 1, "length": 1, "height": 1}}}),
        ("commands/execute_rhinocommon_csharp_code.json", {"code": "doc.Objects.AddPoint(0,0,0);"}),
        ("commands/extrude_curve.json", {"curve_id": GUID, "direction": [0, 0, 10]}),
        ("commands/loft.json", {"curve_ids": [GUID, GUID]}),
        ("commands/modify_objects.json", {"objects": [{"id": GUID, "new_name": "X"}]}),
        ("commands/offset_curve.json", {"curve_id": GUID, "distance": 1.5}),
        ("commands/pipe.json", {"curve_id": GUID, "radius": 0.5}),
        ("commands/sweep1.json", {"rail_id": GUID, "profile_ids": [GUID]}),
        ("commands/walls_from_layer.json", {}),
        ("commands/walls_from_layer.json", {"layer": "wall", "height": 3000, "target_layer": "A-WALL", "name_prefix": "wall-"}),
        ("commands/walls_from_layer.json", {"layer": "wall", "apply_default_materials": True}),
        ("commands/walls_from_layer.json", {"layer": "wall", "apply_default_materials": False}),
        ("commands/floor_from_layer.json", {}),
        ("commands/floor_from_layer.json", {"layer": "wall", "thickness": 400, "target_layer": "A-FLOR", "name_prefix": "floor-"}),
        ("commands/floor_from_layer.json", {"layer": "wall", "apply_default_materials": True}),
        ("commands/roof_flat_from_walls.json", {}),
        ("commands/roof_flat_from_walls.json", {"layer": "wall", "thickness": 200, "overhang": 0, "target_layer": "A-ROOF", "name_prefix": "roof-"}),
        ("commands/roof_flat_from_walls.json", {"thickness": 200, "overhang": 200, "elevation": 3000}),
        ("commands/rooms_from_layer.json", {}),
        ("commands/rooms_from_layer.json", {"layer": "A-ROOM", "target_layer": "A-ROOM", "name_prefix": "room-"}),
        ("commands/rooms_from_layer.json", {"layer": "room", "join_tolerance": 1.0}),
        ("commands/mark_as_existing.json", {}),
        ("commands/mark_as_existing.json", {
            "ids": ["12345678-1234-1234-1234-123456789012"],
            "target_layer": "X-EXIST",
        }),
        ("commands/openings_from_layer.json", {"layer": "door"}),
        ("commands/openings_from_layer.json", {"layer": "window", "sill": 900, "head": 2100, "limit": 5}),
        ("commands/openings_from_layer.json", {"layer": "door", "wall_ids": [GUID]}),
        ("commands/delete_opening.json", {}),
        ("commands/delete_opening.json", {"id": GUID}),
        ("commands/add_opening.json", {"opening_kind": "door"}),
        ("commands/add_opening.json", {"opening_kind": "window", "host_id": GUID, "width": 1200, "sill": 900, "head": 2100, "t": 0.5}),
        ("commands/add_opening.json", {"opening_kind": "door", "distance_mm": 1000}),
        ("commands/move_opening.json", {"delta_mm": 500}),
        ("commands/move_opening.json", {"id": GUID, "t": 0.3}),
        ("commands/set_opening.json", {"width": 1400}),
        ("commands/set_opening.json", {"id": GUID, "sill": 1000, "head": 2200}),
        ("commands/set_opening_type.json", {"type": "door.sliding"}),
        ("commands/set_opening_type.json", {"id": GUID, "hand": "flip"}),
        ("commands/set_opening_type.json", {"swing": "out", "type": "window.top_hung"}),
        ("commands/set_opening_type.json", {"type": "window.fixed", "all": True}),
        ("commands/area_stats.json", {}),
        ("commands/move_wall.json", {"side": "north", "toward": "north", "distance_mm": 500}),
        ("commands/move_wall.json", {"id": GUID, "at": [4000, 2000], "toward": "east", "distance_mm": 300}),
        ("commands/delete_wall.json", {"side": "north"}),
        ("commands/delete_wall.json", {}),
        ("commands/move_wall.json", {"toward": "north", "distance_mm": 500}),
        ("commands/delete_wall.json", {"id": GUID, "at": [5000, 2000]}),
        ("commands/add_wall.json", {"from": [5000, 200], "to": [5000, 3800]}),
        ("commands/room_push_pull.json", {"side": "north", "distance_mm": 500}),
        ("commands/room_push_pull.json", {"id": GUID, "side": "east", "distance_mm": 300, "way": "in"}),
        ("commands/add_room_area.json", {"points": [[0, 0], [4000, 0], [4000, 3000]]}),
        ("commands/add_room_area.json", {"points": [[0, 0], [4000, 0], [4000, 3000], [0, 3000]], "replace": True, "id": GUID}),
        ("commands/split_walls.json", {}),
        ("commands/split_walls.json", {"id": GUID}),
        ("commands/add_wall.json", {"from": [2000, 6000], "to": [6000, 6000], "thickness": 100, "height": 2400}),
        ("commands/add_wall.json", {"line_id": GUID}),
        ("commands/rebuild_host_wall.json", {}),
        ("commands/rebuild_host_wall.json", {"id": GUID}),
        ("commands/clear_generated.json", {}),
        ("commands/clear_generated.json", {"dry_run": True}),
        ("commands/clear_generated.json", {
            "kinds": ["wall", "floor"],
            "level": "0",
            "include_untagged_prefixes": True,
            "name_prefixes": ["wall-", "floor-"],
        }),
        ("commands/clear_generated.json", {"kinds": ["drawing"]}),
        ("commands/daylight_scene.json", {}),
        ("commands/daylight_paint.json", {"z": 50, "vertices": [], "colors": [], "faces": []}),
        ("commands/daylight_paint.json", {
            "z": 50,
            "vertices": [[0, 0], [400, 0], [400, 400], [0, 400]],
            "colors": [[8, 42, 82]] * 4,
            "faces": [[0, 1, 2, 3]],
        }),
        ("commands/capture_viewport.json", {"viewport": "top", "zoom_bbox": [0, 0, 8000, 4000]}),
        ("commands/daylight_clear.json", {}),
        ("commands/panel_daylight.json", {}),
        ("commands/panel_daylight.json", {"action": "run", "text": "run daylight"}),
        ("commands/panel_daylight.json", {"action": "hide"}),
        ("commands/panel_daylight.json", {"action": "show"}),
        ("commands/panel_daylight.json", {"action": "rooms"}),
        ("commands/rooms_detect.json", {}),
        ("commands/dxf_import.json", {"path": "/tmp/office_2D.dxf"}),
        ("commands/plan_import.json", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json"}),
        ("commands/plan_import.json", {"image_path": "/tmp/scan.jpg"}),
        ("commands/plan_import.json", {"pdf_path": "/tmp/plan1.pdf"}),
        ("commands/plan_import.json", {"pdf_path": "/tmp/plan1.pdf", "page": 2, "replace": True}),
        ("commands/plan_import.json", {
            "image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json",
            "scale_hint": "1:100", "image_dpi": 200, "image_width_mm": 42012, "replace": True,
        }),
        ("commands/plan_scale.json", {"p1": [1000, -2000], "p2": [4900, -2000]}),
        ("commands/plan_scale.json", {"p1": [1000, -2000], "p2": [4900, -2000], "length_mm": 4000, "frame": "source"}),
        ("commands/make2d_view.json", {"view": "plan"}),
        ("commands/make2d_view.json", {
            "view": "south",
            "ids": [GUID],
            "include_existing": False,
            "replace": False,
        }),
        ("commands/sheet_pack.json", {}),
        ("commands/sheet_pack.json", {
            "views": ["plan", "north"],
            "include_existing": True,
            "replace": True,
        }),
        ("commands/clear_drawings.json", {}),
        ("commands/clear_drawings.json", {"views": ["plan", "west"], "dry_run": True}),
        ("commands/set_project_meta.json", {}),
        ("commands/set_project_meta.json", {
            "project": "Villa X",
            "client": "Y",
            "address": "Oslo",
            "date": "2026-09-21",
            "scale_label": "1:100",
        }),
        ("commands/set_project_meta.json", {
            "project": "Garage",
            "project_no": "2026-07",
            "client": "Ola Nordmann",
            "address": "Storgata 1, 0150 Oslo",
            "architect": "Riise Arkitekter",
            "date": "2026-10-04",
            "revision": "B",
        }),
        ("commands/layout_pack.json", {"views": ["plan", "schedules"]}),
        ("commands/layout_pack.json", {"views": ["plan", "section_a", "section_b"]}),
        ("commands/layout_pack.json", {"views": ["plan", "detail_20_1", "detail_25_2"]}),
        ("commands/clear_layouts.json", {"views": ["section_a"]}),
        ("commands/section_add.json", {"room": "Stue"}),
        ("commands/section_add.json", {"letter": "B", "room": "rd-01", "axis": "long", "look": "north"}),
        ("commands/section_add.json", {"from": [0, 2000], "to": [8000, 2000]}),
        ("commands/section_add.json", {"letter": "c", "line_id": "8f8e7d6c-0000-0000-0000-000000000001"}),
        ("commands/section_clear.json", {}),
        ("commands/section_clear.json", {"letter": "A"}),
        ("commands/print_profile.json", {}),
        ("commands/print_profile.json", {"name": "grey"}),
        ("commands/section_pick.json", {}),
        ("commands/layout_pack.json", {"views": ["schedules"], "schedule_kinds": ["door", "window", "room"]}),
        ("commands/clear_layouts.json", {"views": ["schedules"]}),
        ("commands/layout_pack.json", {}),
        ("commands/layout_pack.json", {
            "paper": "A3",
            "views": ["plan", "north"],
            "scale": 100,
            "replace": True,
            "include_existing": False,
        }),
        ("commands/export_pdf.json", {"path": "/tmp/forsk-plan.pdf"}),
        ("commands/export_pdf.json", {"path": "/tmp/forsk-plan.pdf", "layout": "plan"}),
        ("commands/export_pdf.json", {"path": "/tmp/forsk-analysis.pdf", "set": "analysis"}),
        ("commands/layout_pack.json", {"set": "analysis"}),
        ("commands/export_sheets.json", {"folder": "/tmp/forsk-export-garage/Garage DWG"}),
        ("commands/export_sheets.json", {"folder": "/tmp/forsk-export-garage/Garage DXF", "format": "dxf"}),
        ("commands/details.json", {"action": "add"}),
        ("commands/details.json", {"action": "add", "refs": [{"wall": "w03"}]}),
        ("commands/details.json", {"action": "add", "refs": [{"wall": "w01"}, {"opening": "o-1"}]}),
        ("commands/details.json", {"action": "remove", "ids": ["DET02"]}),
        ("commands/details.json", {"action": "remove"}),
        ("commands/details.json", {"action": "list"}),
        ("commands/export_ifc.json", {"path": "/tmp/forsk-ifc-garage.ifc"}),
        ("commands/export_csv.json", {"path": "/tmp/Garage Takeoff.csv"}),
        ("commands/add_stair.json", {}),
        ("commands/add_stair.json", {"from": [1000, 650], "to": [5000, 650], "width": 1000, "riser_max": 170, "going": 280}),
        ("commands/add_stair.json", {"from": [1500, 550], "to": [6180, 550], "width": 900, "going": 260, "against": "right"}),
        ("commands/add_stair.json", {"along_wall": True, "at": [7000, 300], "rise": "auto"}),
        ("commands/add_stair.json", {"wall_id": GUID, "side": "north", "rise": 2750}),
        ("commands/edit_stair.json", {"width": 1000}),
        ("commands/edit_stair.json", {"id": "S01", "riser_max": 170}),
        ("commands/edit_stair.json", {"id": GUID, "going": 280, "rise": "auto"}),
        ("commands/edit_stair.json", {"flip": True}),
        ("commands/delete_stair.json", {}),
        ("commands/delete_stair.json", {"id": "S02"}),
        ("commands/add_furniture.json", {"item": "double bed"}),
        ("commands/add_furniture.json", {"item": "bed.double.160x200", "room": "R02"}),
        ("commands/add_furniture.json", {"item": "sofa", "seats": 3, "room": "living"}),
        ("commands/add_furniture.json", {"item": "bed", "width": 180, "at": [2500, 1200]}),
        ("commands/add_furniture.json", {"item": "dining table", "at": [4000, 3000], "rotation": 90}),
        ("commands/move_furniture.json", {"id": "F01", "to": [3000, 2000]}),
        ("commands/move_furniture.json", {"by": [-500, 0]}),
        ("commands/move_furniture.json", {"id": GUID, "rotate": 90}),
        ("commands/delete_furniture.json", {}),
        ("commands/delete_furniture.json", {"id": "F02"}),
        ("commands/delete_furniture.json", {"room": "R03"}),
        ("commands/delete_furniture.json", {"all": True}),
        ("commands/furnish_room.json", {}),
        ("commands/furnish_room.json", {"room": "bedroom"}),
        ("commands/furnish_room.json", {"room": "all", "density": "compact", "variant": "creative", "replace": True}),
        ("commands/furnish_room.json", {"room": "R02", "preview": True}),
        ("commands/jump_inside.json", {}),
        ("commands/jump_inside.json", {"room": "bedroom"}),
        ("commands/clear_layouts.json", {}),
        ("commands/clear_layouts.json", {"views": ["plan"], "dry_run": True}),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "preset": "plaster"}),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "preset": "wood", "ensure_objects_from_layer": True}),
        ("commands/set_layer_material.json", {"layer_name": "A-FLOR", "preset": "white"}),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "material_name": "M-PLASTER"}),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "name": "M-CUSTOM", "diffuse_rgb": [200, 180, 160]}),
        ("commands/set_display_mode.json", {"mode": "Rendered"}),
        ("commands/set_display_mode.json", {"mode": "Arctic"}),
        ("commands/get_object_attributes.json", {"id": GUID}),
        ("commands/update_object_attributes.json", {"id": GUID, "user_strings": {"PartNo": "A-100", "Count": 3}}),
        ("commands/update_object_attributes.json", {"name": "Box1", "layer": "Default", "visible": True}),
        ("commands/update_object_attributes.json", {"id": GUID, "delete_user_strings": ["PartNo"]}),
        ("commands/update_object_attributes.json", {"id": GUID, "material_index": -1}),
        ("commands/analyze_objects.json", {"id": GUID}),
        ("commands/analyze_objects.json", {"object_ids": [GUID]}),
        ("commands/analyze_objects.json", {"selected": True}),
        ("commands/gh_create_document.json", {"new_if_missing": True, "make_active": True, "open_canvas": True}),
        ("commands/gh_get_document_info.json", {}),
        ("commands/gh_search_components.json", {"query": "addition", "limit": 10}),
        ("commands/gh_batch_search_components.json", {"queries": ["Circle", "Number Slider"], "max_matches": 5}),
        ("commands/gh_list_component_categories.json", {}),
        ("commands/gh_get_available_components.json", {"category": "Curve", "include_description": True, "limit": 25}),
        ("commands/gh_get_component_type_info.json", {"name": "Circle"}),
        ("commands/gh_batch_get_component_type_info.json", {"components": [{"name": "Circle"}, {"guid": GUID}]}),
        ("commands/gh_get_graph.json", {"graph_id": "TestGraph", "include_values": True, "max_items": 5}),
        ("commands/gh_clear_graph.json", {"graph_id": "TestGraph", "include_groups": True, "recompute": False}),
        ("commands/gh_list_components.json", {"category": "Curve", "limit": 20}),
        ("commands/gh_get_component_info.json", {"instance_id": GUID}),
        ("commands/gh_get_canvas_state.json", {"include_connections": True, "include_values": False, "max_items": 5}),
        ("commands/gh_capture_preview.json", {"viewport": "perspective", "width": 800, "height": 600, "graph_id": "TestGraph", "padding_factor": 1.2}),
        ("commands/gh_run_solution.json", {"expire_all": True}),
        ("commands/gh_expire_solution.json", {"component_ids": [GUID], "recompute": True}),
        ("commands/gh_build_graph.json", {
            "components": [
                {"alias": "slider_a", "component_name": "Number Slider", "nickname": "A", "value": 3.5, "min": 0, "max": 10},
                {"alias": "add", "component_name": "Addition", "nickname": "Add"},
                {"alias": "by_guid", "component_guid": GUID, "nickname": "ByGuid"}
            ],
            "connections": [{"source": "slider_a", "target": "add", "target_input_index": 0}],
            "values": [{"target": "slider_a", "value": 4.0, "decimals": 1}],
            "preview_updates": {"enabled": False},
            "preview_policy": {"mode": "only", "targets": ["add"], "scope": ["slider_a", "add"]},
            "groups": [{"name": "Controls", "targets": ["slider_a"], "color": [180, 220, 255]}],
            "layout": {"enabled": True, "start_position": [40, 40], "x_spacing": 220, "max_columns": 6},
            "graph_id": "TestGraph",
            "recompute": True,
            "rollback_on_error": True,
            "open_canvas": True
        }),
        ("commands/gh_add_component.json", {"component_guid": GUID, "nickname": "ByGuid"}),
        ("commands/gh_mutate_graph.json", {
            "graph_id": "TestGraph",
            "operations": [
                {"op": "create", "alias": "height", "component_name": "Number Slider", "value": 8, "min": 0, "max": 20, "role": "control"},
                {"op": "create", "alias": "by_guid", "guid": GUID},
                {"op": "update", "target": "cylinder", "preview": False},
                {"op": "connect", "source": "height", "target": "cap", "target_input_index": 0},
                {"op": "recompute"}
            ],
            "preview_policy": {"mode": "only", "targets": ["cap"]},
            "groups": [{"name": "Output", "targets": ["cap"], "color": [180, 220, 255]}],
            "layout": {"enabled": True, "targets": ["height", "cap"], "max_columns": 6},
            "verify": {
                "run_solution": True,
                "expect_no_runtime_warnings": True,
                "outputs": [{
                    "target": "cap",
                    "output_index": 0,
                    "expect_count_min": 1,
                    "expect_count_exact": 1,
                    "expect_type": "Brep",
                    "expect_all_type": "Brep",
                    "expect_all_solid": True
                }]
            },
            "fail_on_verification_error": True,
            "recompute": True,
            "rollback_on_error": True,
            "open_canvas": True
        }),
        ("commands/gh_mutate_graph.json", {
            "graph_id": "ExistingGraph",
            "operations": [
                {"op": "disconnect", "source": GUID, "source_output_name": "G", "target": GUID, "target_input_name": "L"},
                {"op": "create", "alias": "flatten_inserted", "component_name": "Flatten Tree", "nickname": "Flatten"},
                {"op": "connect", "source": GUID, "source_output_name": "G", "target": "flatten_inserted"},
                {"op": "connect", "source": "flatten_inserted", "target": GUID, "target_input_name": "L"},
                {"op": "recompute"}
            ],
            "layout": {"enabled": True, "targets": [GUID, "flatten_inserted"], "max_columns": 4},
            "recompute": True,
            "rollback_on_error": True
        }),
        ("commands/gh_add_component.json", {"component_name": "Number Slider", "position": [20, 40], "nickname": "Radius", "value": 5, "min": 0, "max": 10}),
        ("commands/gh_delete_component.json", {"nickname": "Radius"}),
        ("commands/gh_layout_components.json", {"component_ids": [GUID], "start_position": [40, 40], "x_spacing": 220, "y_spacing": 90, "recompute": True}),
        ("commands/gh_connect_components.json", {"source_instance_id": GUID, "source_output_index": 0, "target_instance_id": GUID, "target_input_name": "Radius"}),
        ("commands/gh_disconnect_components.json", {"target_instance_id": GUID, "target_input_index": 0, "disconnect_all": True}),
        ("commands/gh_set_parameter_value.json", {"nickname": "Radius", "value": 7.5, "input_index": 0}),
        ("commands/gh_get_parameter_value.json", {"instance_id": GUID, "output_index": 0, "max_items": 10}),
        ("commands/gh_update_component.json", {"instance_id": GUID, "new_nickname": "Radius2", "position": [100, 200], "preview": False}),
        ("commands/gh_clear_canvas.json", {"include_groups": True, "recompute": False}),
        ("commands/undo.json", {}),
        ("commands/undo.json", {"steps": 3}),
        ("commands/redo.json", {}),
    ]

    all_passed = True
    for path, example in cases:
        if not validate(path, example):
            all_passed = False
    return all_passed


def test_other_commands():
    """Test other command schemas."""
    print("\n=== Testing other commands ===")

    all_passed = True

    # execute_rhinoscript_python_code
    print("  execute_rhinoscript_python_code:")
    if not validate("commands/execute_rhinoscript_python_code.json", {"code": "print('hello')"}):
        all_passed = False

    # get_document_summary
    print("  get_document_summary:")
    if not validate("commands/get_document_summary.json", {}):
        all_passed = False

    # get_objects
    print("  get_objects:")
    if not validate("commands/get_objects.json", {}):
        all_passed = False
    if not validate("commands/get_objects.json", {"limit": 100, "offset": 0}):
        all_passed = False
    if not validate("commands/get_objects.json", {"layer_filter": "A-OPEN", "include_hidden": True}):
        all_passed = False
    if not validate("commands/get_objects.json", {"layer_filter": "Default", "type_filter": "CURVE"}):
        all_passed = False

    # get_selected_objects_info
    print("  get_selected_objects_info:")
    if not validate("commands/get_selected_objects_info.json", {}):
        all_passed = False

    # get_object_info
    print("  get_object_info:")
    if not validate("commands/get_object_info.json", {"id": "12345678-1234-1234-1234-123456789012"}):
        all_passed = False
    if not validate("commands/get_object_info.json", {"name": "MyObject"}):
        all_passed = False

    # run_command
    print("  run_command:")
    if not validate("commands/run_command.json", {"command": "_Box 0,0,0 10,10,10"}):
        all_passed = False
    if not validate("commands/run_command.json", {"command": "_SelAll", "echo": True}):
        all_passed = False

    # get_commands
    print("  get_commands:")
    if not validate("commands/get_commands.json", {}):
        all_passed = False
    if not validate("commands/get_commands.json", {"filter": "boolean", "loaded_only": False}):
        all_passed = False

    return all_passed


def test_responses():
    """Test response schemas."""
    print("\n=== Testing response schemas ===")

    all_passed = True

    # Object info
    print("  object_info:")
    object_info = {
        "id": "12345678-1234-1234-1234-123456789012",
        "name": "MyBox",
        "type": "BOX",
        "layer": "Default",
        "material": "-1",
        "color": {"r": 255, "g": 0, "b": 0},
        "bounding_box": [[-1, -1, -1], [1, 1, 1]],
        "geometry": {}
    }
    if not validate("responses/object_info.json", object_info):
        all_passed = False

    # Select result
    print("  select_result:")
    select_result = {"count": 5}
    if not validate("responses/select_result.json", select_result):
        all_passed = False

    # Delete result
    print("  delete_result:")
    delete_result = {"id": "12345678-1234-1234-1234-123456789012", "name": "Deleted", "deleted": True}
    if not validate("responses/delete_result.json", delete_result):
        all_passed = False
    # Deleting an unnamed object reports the "(unnamed)" fallback, never null.
    # name is typed as a string, so a null is a regression: it made deleting a
    # nameless object fail response validation under strict mode.
    unnamed_delete = {"id": "12345678-1234-1234-1234-123456789012", "name": "(unnamed)", "deleted": True}
    if not validate("responses/delete_result.json", unnamed_delete):
        all_passed = False
    delete_validator = Draft202012Validator(load_schema_with_refs("responses/delete_result.json"))
    null_name = {"id": "12345678-1234-1234-1234-123456789012", "name": None, "deleted": True}
    if not list(delete_validator.iter_errors(null_name)):
        print("  FAIL: delete_result accepted a null name")
        all_passed = False
    else:
        print("  delete_result rejects a null name")

    # Execute script result
    print("  execute_script_result:")
    script_result = {"success": True, "output": "Hello from Rhino"}
    if not validate("responses/execute_script_result.json", script_result):
        all_passed = False

    # Layer info
    print("  layer_info:")
    layer_info = {
        "id": "12345678-1234-1234-1234-123456789012",
        "name": "Default",
        "color": {"r": 0, "g": 0, "b": 0},
        "parent": "00000000-0000-0000-0000-000000000000"
    }
    if not validate("responses/layer_info.json", layer_info):
        all_passed = False

    # Object attributes
    print("  object_attributes:")
    object_attributes = {
        "id": "12345678-1234-1234-1234-123456789012",
        "name": "MyBox",
        "type": "BOX",
        "layer": {
            "index": 0,
            "id": "12345678-1234-1234-1234-123456789012",
            "name": "Default",
            "full_path": "Default",
        },
        "color": {"r": 255, "g": 0, "b": 0},
        "color_source": "ColorFromObject",
        "material_index": -1,
        "material_source": "MaterialFromLayer",
        "visible": True,
        "locked": False,
        "hidden": False,
        "normal": True,
        "user_strings": {"PartNo": "A-100"},
    }
    if not validate("responses/object_attributes.json", object_attributes):
        all_passed = False

    # Analyze objects
    print("  analyze_objects_result:")
    analyze_result = {
        "object_count": 1,
        "analyses": [
            {
                "id": "12345678-1234-1234-1234-123456789012",
                "name": "Line1",
                "type": "LINE",
                "layer": "Default",
                "valid": True,
                "validity_log": None,
                "bounding_box": [[0, 0, 0], [10, 0, 0]],
                "bbox_dimensions": [10, 0, 0],
                "metrics": {
                    "length": 10,
                    "is_closed": False,
                    "start_point": [0, 0, 0],
                    "end_point": [10, 0, 0],
                },
            }
        ],
    }
    if not validate("responses/analyze_objects_result.json", analyze_result):
        all_passed = False

    print("  mark_as_existing_result:")
    mark_result = {
        "ids": ["12345678-1234-1234-1234-123456789012"],
        "forsk_ids": ["x01"],
        "count": 1,
        "target_layer": "X-EXIST",
        "message": "Marked 1 object(s) as existing on X-EXIST.",
    }
    if not validate("responses/mark_as_existing_result.json", mark_result):
        all_passed = False

    print("  clear_generated_result:")
    clear_result = {
        "deleted": ["12345678-1234-1234-1234-123456789012"],
        "count": 1,
        "dry_run": False,
    }
    if not validate("responses/clear_generated_result.json", clear_result):
        all_passed = False
    clear_dry = {"deleted": [], "count": 0, "dry_run": True}
    if not validate("responses/clear_generated_result.json", clear_dry):
        all_passed = False

    print("  daylight results:")
    scene = {
        "walls": [{"id": "w01", "thickness": 200, "z0": 0, "z1": 3000,
                   "rings": [[[0, 0], [8000, 0], [8000, 4000], [0, 4000]]]}],
        "openings": [{"id": "window-01", "host_id": "w01", "kind": "window", "width": 1200, "center": [4800, 100],
                      "sill": 900, "head": 2100}],
        "rooms": [{"id": "12345678-1234-1234-1234-123456789012", "name": "room-01",
                   "ring": [[200, 200], [7800, 200], [7800, 3800], [200, 3800]], "z": 0}],
        "roofs": [{"z0": 2800, "overhang": 500}],
        "selected_room_ids": [],
        "warnings": [],
    }
    if not validate("responses/daylight_scene_result.json", scene):
        all_passed = False
    painted = {"id": "12345678-1234-1234-1234-123456789012", "faces": 200, "vertices": 800,
               "wires": "off", "layer": "A-ANALYSE", "deleted": 1,
               "bbox": [200, 200, 7800, 3800], "message": "Painted 200 daylight faces on A-ANALYSE."}
    if not validate("responses/daylight_paint_result.json", painted):
        all_passed = False
    if not validate("responses/daylight_clear_result.json", {"count": 1, "remaining": 0, "message": "Cleared 1"}):
        all_passed = False
    if not validate("responses/panel_daylight_result.json", {
        "visible": True, "enabled": True, "label": "Make rooms", "intent": "daylight",
        "ok": False, "line": "Daylight · error · No rooms.",
    }):
        all_passed = False
    if not validate("responses/panel_daylight_result.json", {
        "visible": False, "enabled": True, "label": "Make rooms", "intent": "import",
        "import_visible": True, "import_label": "Set scale",
    }):
        all_passed = False

    print("  plan_import_result:")
    if not validate("responses/plan_import_result.json", {
        "walls": 30, "walls_detected": 41, "merged": 11, "squared": 0, "diagonal": 0, "joined": 43, "gaps_closed": 3,
        "extended": 5, "doors": 7, "windows": 12, "loose": 0, "uncut": 0, "outlines": 2, "outline_holes": 3,
        "wall_pieces": 41, "overlaps": 0, "free_walls": 1, "blocks_skipped": 0,
        "rooms": 11, "unlabelled": 3, "outside": 8,
        "dropped": [], "review": ["Closed a 250 mm gap at 5.0, 3.6 m: the wall end was run to the wall it stopped short of. Check that nothing opens there."],
        "scale": {"status": "unconfirmed", "ratio": "1:100", "factor": 1.0},
        "underlay": {"id": "12345678-1234-1234-1234-123456789012", "layer": "X-PLAN", "image": "plan.png",
                     "display": "Rendered", "width_mm": 42011.6, "height_mm": 29705.4},
        "objects": 72, "replaced": 0, "warnings": [],
        "message": "Imported 30 walls (41 detected, 11 merged), 7 doors, 12 windows, 11 rooms.",
    }):
        all_passed = False
    if not validate("responses/plan_import_result.json", {
        "walls": 0, "walls_detected": 0, "doors": 0, "windows": 0, "loose": 0, "rooms": 0, "unlabelled": 0,
        "dropped": [], "review": [], "scale": {"status": "detected", "ratio": None, "factor": 1.0},
        "underlay": {"id": "12345678-1234-1234-1234-123456789012", "layer": "X-PLAN", "image": "plan.png",
                     "width_mm": 1.0, "height_mm": 1.0},
        "message": "Imported 0 walls, 0 doors, 0 windows, 0 rooms.",
    }):
        all_passed = False
    if not validate("responses/plan_import_result.json", {
        "walls": 46, "walls_detected": 54, "doors": 12, "windows": 10, "loose": 0, "rooms": 12, "unlabelled": 0,
        "dropped": ["wall at 15.8, -22.7 m: inside a thicker wall"], "review": [],
        "scale": {"status": "detected", "ratio": "1:100", "factor": 1.0},
        "underlay": {"id": "12345678-1234-1234-1234-123456789012", "layer": "X-PLAN", "image": "plan1-p1.png",
                     "width_mm": 42011.6, "height_mm": 29705.3},
        "pdf": {"file": "plan1.pdf", "page": 1, "plan_path": "/tmp/forsk-plan-import/plan1-p1.json",
                "image_path": "/tmp/forsk-plan-import/plan1-p1.png"},
        "message": "Imported 46 walls (54 detected, 7 merged, 1 dropped), 12 doors, 10 windows, 12 rooms.",
    }):
        all_passed = False
    if not validate("responses/plan_import_result.json", {
        "walls": 4, "walls_detected": 4, "doors": 1, "windows": 1, "loose": 0, "rooms": 1, "unlabelled": 0,
        "dropped": [], "review": [], "scale": {"status": "unconfirmed", "ratio": "1:100", "factor": 1.0},
        "underlay": {"id": "12345678-1234-1234-1234-123456789012", "layer": "X-PLAN", "image": "scan-p1-raster.png",
                     "width_mm": 21000.0, "height_mm": 29700.0},
        "raster": {"file": "scan.pdf", "page": 1, "scan": "no vector walls found; page looks like a raster scan",
                   "model": "cubicasa5k", "licence": "CC BY-NC 4.0 — non-commercial use only",
                   "plan_path": "/tmp/forsk-plan-import/scan-p1-raster.json",
                   "image_path": "/tmp/forsk-plan-import/scan-p1-raster.png"},
        "message": "Imported 4 walls, 1 door, 1 window, 1 room.",
    }):
        all_passed = False
    # The raster source's plan always states its model's licence.
    plan_import_validator = Draft202012Validator(load_schema_with_refs("responses/plan_import_result.json"))
    unlicensed = {
        "walls": 4, "walls_detected": 4, "doors": 1, "windows": 1, "loose": 0, "rooms": 1, "unlabelled": 0,
        "dropped": [], "review": [], "scale": {"status": "unconfirmed", "ratio": "1:100", "factor": 1.0},
        "underlay": {"id": "12345678-1234-1234-1234-123456789012", "layer": "X-PLAN", "image": "plan-raster.png",
                     "width_mm": 1.0, "height_mm": 1.0},
        "raster": {"file": "plan.png", "model": "cubicasa5k",
                   "plan_path": "/tmp/plan-raster.json", "image_path": "/tmp/plan-raster.png"},
        "message": "Imported 4 walls, 1 door, 1 window, 1 room.",
    }
    if not list(plan_import_validator.iter_errors(unlicensed)):
        print("  FAIL: plan_import_result accepted a raster plan with no licence")
        all_passed = False
    else:
        print("  plan_import_result rejects a raster plan with no licence")
    if not validate("responses/plan_scale_result.json", {
        "measured_mm": 3900.0, "factor": 1.0, "status": "detected", "scaled": 0,
        "message": "The two points are 3900 mm apart at the scale the plan has now.",
    }):
        all_passed = False
    if not validate("responses/plan_scale_result.json", {
        "measured_mm": 3900.0, "length_mm": 4000, "factor": 1.0256410256410255, "previous_factor": 1.0,
        "relative": 1.0256410256410255, "status": "user", "scaled": 73, "walls_recleaned": True,
        "message": "Scale set: 4000 mm between the two points (was 3900 mm, x1.0256). Walls cleaned again at this scale: thickness rounded to 10 mm.",
    }):
        all_passed = False
    if not validate("responses/rooms_detect_result.json", {
        "ids": ["12345678-1234-1234-1234-123456789012"], "rooms": [
            {"id": "rd-01", "name": "Garasje", "area_m2": 27.36, "x": 4000.0, "y": 2000.0, "source": "detected"},
            {"id": "room-02", "name": "Bod", "area_m2": 6.0, "x": 9000.0, "y": 1500.0, "source": "drawn"},
        ],
        "count": 1, "detected": 1, "kept": 0, "removed": 0, "slivers": 1, "area_m2": 27.4,
        "open": [{"reason": "gap 0.9 m without a door", "x": 1950.0, "y": 2100.0}],
        "layer": "A-ROOM", "warnings": [], "message": "1 room, 27.4 m². 1 open: gap 0.9 m without a door.",
    }):
        all_passed = False

    print("  dxf_import_result:")
    if not validate("responses/dxf_import_result.json", {
        "success": True, "objects": 283, "texts": 16, "matched": 16,
        "rewritten": ["B00F8ttekott -> Bøttekott"], "unmatched": [], "labels_suspect": [], "warnings": [],
        "units": "mm", "insunits": 4, "scale": 1.0, "units_guessed": False,
        "message": "Imported office_2D.dxf: 283 objects. Texts 16/16 read from the DXF, 1 rewritten. Units mm ($INSUNITS 4), scale ×1.",
    }):
        all_passed = False
    guessed = "Units not stated ($INSUNITS missing): guessed m from its size, 12 across, scale ×1000. Check a known length."
    if not validate("responses/dxf_import_result.json", {
        "success": True, "objects": 40, "texts": 1, "matched": 0,
        "rewritten": [], "unmatched": ["label 'B00F8ttekott' at 2000,3000, no DXF text on its layer"],
        "labels_suspect": ["B00F8ttekott"], "warnings": [guessed],
        "units": "m", "insunits": None, "scale": 1000.0, "units_guessed": True,
        "message": "Imported plan.dxf: 40 objects. Texts 0/1 read from the DXF, 0 rewritten, 1 left as Rhino made them. " + guessed,
    }):
        all_passed = False
    if not validate("responses/rooms_detect_result.json", {
        "ids": [], "count": 0, "detected": 0, "area_m2": 0.0, "open": [],
        "labels_suspect": [{"text": "B00F8ttekott", "room_id": "rd-10", "x": 27257.5, "y": 17645.2}],
        "message": "16 rooms, 399.5 m². Labels suspect 1 (rd-10): a DXF escape lost on import. Import the DXF with dxf_import.",
    }):
        all_passed = False
    # S3: each open region named, and the floor plates with the rooms that got none.
    if not validate("responses/rooms_detect_result.json", {
        "ids": ["12345678-1234-1234-1234-123456789012"], "count": 1, "detected": 1, "area_m2": 12.4,
        "open": [{"name": "Bod", "reason": "gap 0.9 m without a door", "x": 2000.0, "y": 2000.0}],
        "plate_ids": ["12345678-1234-1234-1234-123456789013"], "no_plate": [{"room": "Hall", "why": "gap 1.2 m without a door"}],
        "message": "1 room, 12.4 m². 1 open: Bod (gap 0.9 m without a door). No floor plate for Hall (gap 1.2 m without a door).",
    }):
        all_passed = False
    if not validate("responses/rooms_from_layer_result.json", {
        "ids": ["12345678-1234-1234-1234-123456789012"], "count": 1, "plate_ids": [], "no_plate": [{"room": "Bod", "why": "its walls do not close"}],
        "message": "Created 1 room marker(s) on A-ROOM from layer 'A-ROOM'. No floor plate for Bod (its walls do not close).",
    }):
        all_passed = False

    print("  make2d_view_result:")
    guid = "12345678-1234-1234-1234-123456789012"
    make2d_result = {
        "count": 2,
        "ids": [guid, guid],
        "layer": "S-PLAN",
        "view": "plan",
        "message": "Drew 2 curve(s) on S-PLAN (plan).",
    }
    if not validate("responses/make2d_view_result.json", make2d_result):
        all_passed = False
    make2d_empty = {
        "count": 0,
        "ids": [],
        "layer": "S-PLAN",
        "view": "plan",
        "message": "Nothing to draw. Bake walls, floor, or roof first.",
    }
    if not validate("responses/make2d_view_result.json", make2d_empty):
        all_passed = False

    print("  sheet_pack_result:")
    pack_result = {
        "views": [make2d_empty],
        "count": 0,
        "message": "Drew 0 curve(s) across 5 view(s).",
    }
    if not validate("responses/sheet_pack_result.json", pack_result):
        all_passed = False

    print("  clear_drawings_result:")
    clear_drawings = {
        "deleted": [guid],
        "count": 1,
        "dry_run": False,
    }
    if not validate("responses/clear_drawings_result.json", clear_drawings):
        all_passed = False

    print("  set_project_meta_result:")
    meta_result = {
        "project": "Villa X",
        "project_no": "2026-07",
        "client": "",
        "address": "Oslo",
        "architect": "Riise Arkitekter",
        "date": "2026-09-21",
        "revision": "B",
        "scale_label": "1:100",
    }
    if not validate("responses/set_project_meta_result.json", meta_result):
        all_passed = False

    print("  print_profile_result:")
    pen = {"mm": 0.5, "rgb": "0,0,0"}
    profile_result = {
        "profile": {
            "name": "default", "label": "Default: solid black poché, black lines",
            "cut": pen, "silhouette": {"mm": 0.35, "rgb": "0,0,0"},
            "beyond": {"mm": 0.18, "rgb": "0,0,0"}, "thin": {"mm": 0.13, "rgb": "0,0,0"},
            "dashed": "0,0,0", "text": "0,0,0",
            "poche": {"rgb": "0,0,0", "pattern": "Solid", "spacing_mm": 0},
        },
        "available": [{"name": "default", "label": "Default"}, {"name": "grey", "label": "Grey"}],
        "changed": False,
        "message": "Print profile: default.",
    }
    if not validate("responses/print_profile_result.json", profile_result):
        all_passed = False

    print("  layout_pack_result:")
    layout_result = {
        "pages": [{
            "view": "plan",
            "page": "Forsk — Plan",
            "scale": 100,
            "detail_count": 1,
            "ids": [guid],
        }],
        "count": 1,
        "scale": 100,
        "message": "Laid out 1 page(s) on A3 at 1:100.",
    }
    if not validate("responses/layout_pack_result.json", layout_result):
        all_passed = False
    layout_empty = {
        "pages": [],
        "count": 0,
        "scale": 100,
        "message": "Nothing to lay out. Bake walls first.",
    }
    if not validate("responses/layout_pack_result.json", layout_empty):
        all_passed = False

    print("  section_result:")
    section = {
        "letter": "A", "view": "section_a", "a": [3000, 4000], "b": [3000, 0],
        "look": [1, 0], "room": "rd-01", "axis": "cross",
    }
    added = {
        "section": section, "sections": [section], "view": "section_a", "replaced": False,
        "message": "Added section A–A through Garage.",
    }
    if not validate("responses/section_result.json", added):
        all_passed = False
    if not validate("responses/section_result.json", {"removed": ["A"], "sections": [], "message": "Removed section A."}):
        all_passed = False

    print("  stair_result:")
    stair = {
        "id": "12345678-1234-1234-1234-123456789012", "forsk_id": "S01",
        "risers": 16, "riser": 171.875, "going": 260, "width": 900, "rise": 2750, "rise_auto": True,
        "run": 3900, "rule": 603.75, "comfort": "", "message": "Added a straight stair along the north wall, 16 steps of 172.",
    }
    if not validate("responses/stair_result.json", stair):
        all_passed = False
    if not validate("responses/delete_stair_result.json", {"deleted": [stair["id"]], "count": 1, "message": "Removed the stair."}):
        all_passed = False

    print("  furniture_result:")
    piece = {
        "id": "12345678-1234-1234-1234-123456789012", "forsk_id": "F01", "catalog_id": "bed.double.160x200",
        "name": "Double bed 160", "room": "R02", "centre": [1500.0, 2000.0], "rotation": 90.0, "size": [1600, 2000, 900],
        "message": "Added a double bed 160 to the bedroom, against the east wall.",
    }
    if not validate("responses/furniture_result.json", piece):
        all_passed = False
    furnished = {
        "rooms": [{"room": "R02", "type": "bedroom", "added": [{"id": piece["id"], "forsk_id": "F01", "catalog_id": "bed.double.160x200"}],
                   "skipped": ["a wardrobe (no free wall for it)"], "why": ""},
                  {"room": "R03", "type": "bathroom", "added": [], "skipped": [], "why": "no wall holds an 80 shower with 0.7 m in front of it"}],
        "count": 1, "density": "relaxed", "variant": "consistent",
        "message": "Furnished the bedroom: double bed 160. Did not furnish the bathroom: no wall holds an 80 shower.",
    }
    if not validate("responses/furnish_result.json", furnished):
        all_passed = False
    preview = {
        "preview": True, "room": "R02", "room_words": "the bedroom", "density": "relaxed", "replace": False, "same": False, "count": 0,
        "options": [{"variant": "consistent", "count": 4, "summary": "double bed 160, 2 × bedside table, wardrobe 100",
                     "rooms": [{"room": "R02", "type": "bedroom", "pieces": ["bed.double.160x200"], "skipped": [], "why": ""}]},
                    {"variant": "creative", "count": 4, "summary": "double bed 160, 2 × bedside table, wardrobe 100",
                     "rooms": [{"room": "R02", "type": "bedroom", "pieces": ["bed.double.160x200"], "skipped": [], "why": ""}]}],
        "message": "Two layouts for the bedroom: the usual in blue, another in orange. Pick one on the card.",
    }
    if not validate("responses/furnish_result.json", preview):
        all_passed = False
    if not validate("responses/jump_inside_result.json", {"view": "Interior: Bedroom", "room": "R02", "eye": [1000, 300, 1200], "target": [1000, 5000, 1200], "lens_mm": 24, "message": "Perspective is inside the bedroom."}):
        all_passed = False
    if not validate("responses/delete_furniture_result.json", {"deleted": [piece["id"]], "count": 1, "message": "Removed the double bed 160."}):
        all_passed = False

    print("  export_ifc_result:")
    ifc_written = {
        "path": "/tmp/forsk-ifc-garage.ifc", "walls": 4, "doors": 1, "windows": 1, "slabs": 1, "roofs": 1,
        "spaces": 1, "message": "✓ Exported IFC · 4 walls, 1 door, 1 window, 1 space · forsk-ifc-garage.ifc",
    }
    if not validate("responses/export_ifc_result.json", ifc_written):
        all_passed = False

    print("  export_csv_result:")
    csv_written = {
        "path": "/tmp/Garage Takeoff.csv", "rooms": 6, "walls": 14, "doors": 4, "windows": 1, "stairs": 1,
        "message": "✓ Exported takeoff CSV · 6 rooms, 14 walls, 5 doors and windows, 1 stair · Garage Takeoff.csv",
    }
    if not validate("responses/export_csv_result.json", csv_written):
        all_passed = False

    print("  details_result:")
    details_added = {
        "details": [
            {"id": "DET01", "wall": "w01", "name": "South wall", "views": ["plan", "section"]},
            {"id": "DET02", "opening": "o-1", "name": "Door D01", "views": ["plan", "elevation", "section"]},
        ],
        "count": 2, "added": 2, "already": 0,
        "message": "✓ 2 details added · they print on a detail sheet.",
    }
    if not validate("responses/details_result.json", details_added):
        all_passed = False
    if not validate("responses/details_result.json", {"details": [], "count": 0, "message": "No details yet."}):
        all_passed = False

    print("  export_sheets_result:")
    sheets_result = {
        "folder": "/tmp/forsk-export-garage/Garage DWG", "format": "dwg", "count": 2,
        "files": ["Garage A-20-001 Plan.dwg", "Garage A-00-001 Front sheet.dwg"],
        "writer": "active_doc", "misc": 0, "misc_roles": [], "acad_version": "AC1032",
        "message": "Exported 2 sheets as DWG to /tmp/forsk-export-garage/Garage DWG.",
    }
    if not validate("responses/export_sheets_result.json", sheets_result):
        all_passed = False
    sheets_refuse = {
        "folder": "", "format": "dwg", "count": 0, "files": [], "writer": "", "misc": 0,
        "message": "export_sheets needs an absolute folder.",
    }
    if not validate("responses/export_sheets_result.json", sheets_refuse):
        all_passed = False

    print("  export_pdf_result:")
    pdf_result = {
        "path": "/tmp/forsk-plan.pdf",
        "count": 1,
        "pages": ["Forsk — Plan"],
        "message": "Wrote 1 page(s) to /tmp/forsk-plan.pdf.",
        "vector": True,
        "hatch_fallback": 0,
    }
    if not validate("responses/export_pdf_result.json", pdf_result):
        all_passed = False
    pdf_refuse = {
        "path": "",
        "count": 0,
        "pages": [],
        "message": "export_pdf requires a file path.",
    }
    if not validate("responses/export_pdf_result.json", pdf_refuse):
        all_passed = False
    pdf_blank = {
        "path": "",
        "count": 0,
        "pages": [],
        "message": "PDF detail is empty. The sheet does not show the drawing.",
    }
    if not validate("responses/export_pdf_result.json", pdf_blank):
        all_passed = False
    layout_blank = {
        "pages": [],
        "count": 0,
        "scale": 100,
        "message": "Layout detail is empty. The sheet does not show the drawing.",
    }
    if not validate("responses/layout_pack_result.json", layout_blank):
        all_passed = False

    print("  clear_layouts_result:")
    clear_layouts = {
        "deleted": ["Forsk — Plan"],
        "object_ids": [guid],
        "count": 1,
        "dry_run": False,
    }
    if not validate("responses/clear_layouts_result.json", clear_layouts):
        all_passed = False

    print("  rebuild_host_wall_result:")
    rebuild_host = {
        "host_id": guid,
        "forsk_id": "w01",
        "level": "0",
        "height": 3000,
        "thickness": 200,
        "path_points": 16,
        "opening_count": 2,
        "marker_ids": [guid],
        "block_ids": [guid],
        "warnings": [],
        "solid_volume": 1200000000,
        "ok": True,
        "message": "Rebuilt wall w01 with 2 openings.",
    }
    if not validate("responses/rebuild_host_wall_result.json", rebuild_host):
        all_passed = False

    print("  set_opening_result:")
    set_opening = {
        "marker_id": guid,
        "host_id": guid,
        "width": 1400,
        "sill": 1000,
        "head": 2200,
        "t": 0.4,
        "ok": True,
        "message": "Set the opening to width 1400 mm, sill 1000 mm, head 2200 mm.",
        "block_id": guid,
    }
    if not validate("responses/set_opening_result.json", set_opening):
        all_passed = False

    print("  set_opening_type_result:")
    set_opening_type = {
        "marker_id": guid,
        "host_id": guid,
        "opening_type": "door.sliding",
        "hand": "L",
        "ok": True,
        "message": "Changed 1 door to sliding.",
        "block_id": guid,
        "host_openings": 1,
        "host_voids": 1,
        "max_frame_mm": 8,
    }
    if not validate("responses/set_opening_type_result.json", set_opening_type):
        all_passed = False

    print("  move_wall_result:")
    move_wall = {
        "host_id": guid,
        "forsk_id": "w01",
        "wall": "the north wall",
        "toward": "north",
        "distance_mm": 500,
        "normal": [0, 1],
        "faces_before": [3800, 4000],
        "faces_after": [4300, 4500],
        "length_mm": 8000,
        "thickness": 200,
        "path_points": 4,
        "openings_moved": [guid],
        "followed": [
            {"forsk_id": "w03", "wall": "the east wall", "change_mm": 500},
            {"forsk_id": "w04", "wall": "the west wall", "change_mm": 500},
        ],
        "records": ["w01", "w03", "w04"],
        "rebuilt": "Floor, roof and 1 room updated.",
        "host_openings": 2,
        "host_voids": 2,
        "max_frame_mm": 0,
        "markers": [],
        "warnings": [],
        "ok": True,
        "message": "Moved the north wall of w01 500 mm north, 1 opening with it; the east and west walls followed. Floor, roof and 1 room updated.",
    }
    if not validate("responses/move_wall_result.json", move_wall):
        all_passed = False

    print("  delete_wall_result:")
    delete_wall = {
        "host_id": guid,
        "forsk_id": "w01",
        "wall": "the wall at (5000, 2000)",
        "record_deleted": False,
        "openings_deleted": [],
        "length_mm": 3600,
        "thickness": 100,
        "path_points": 4,
        "holes": 1,
        "host_openings": 2,
        "host_voids": 2,
        "warnings": [],
        "ok": True,
        "message": "Deleted the wall at (5000, 2000) of w01. Roof and 1 room updated.",
    }
    if not validate("responses/delete_wall_result.json", delete_wall):
        all_passed = False
    if not validate("responses/delete_wall_result.json", {**delete_wall, "host_id": "", "record_deleted": True, "path_points": 0, "holes": 0}):
        all_passed = False
    joined_delete = {
        **delete_wall,
        "wall": "the north wall",
        "record_deleted": True,
        "followed": [{"forsk_id": "w03", "wall": "the east wall", "change_mm": -200}],
        "records": ["w01"],
        "rebuilt": "Floor and 1 room updated.",
        "message": "Deleted the north wall, w01; it was joined to the east and west walls. Floor and 1 room updated.",
    }
    if not validate("responses/delete_wall_result.json", joined_delete):
        all_passed = False

    print("  add_wall_result:")
    add_wall = {
        "host_id": guid,
        "forsk_id": "w01",
        "joined": True,
        "from": [5000, 200],
        "to": [5000, 3800],
        "length_mm": 3600,
        "thickness": 100,
        "height": 3000,
        "path_points": 4,
        "holes": 2,
        "host_openings": 2,
        "host_voids": 2,
        "ok": True,
        "message": "Added a 100 mm wall to w01, 3600 mm long. Roof and 2 rooms updated.",
    }
    if not validate("responses/add_wall_result.json", add_wall):
        all_passed = False
    print("  room_push_pull_result:")
    push = {
        "room": "Stue",
        "room_id": guid,
        "side": "north",
        "way": "out",
        "host_id": guid,
        "forsk_id": "w01",
        "wall": "the wall at (2050, 3801)",
        "toward": "north",
        "distance_mm": 500,
        "openings_moved": [],
        "followed": [{"forsk_id": "w01", "wall": "the east wall", "change_mm": 500}],
        "records": ["w01"],
        "rebuilt": "Floor, roof and 2 rooms updated.",
        "ok": True,
        "message": "Pushed Stue's north side 500 mm out; the east and west walls followed. Floor, roof and 2 rooms updated.",
    }
    if not validate("responses/room_push_pull_result.json", push):
        all_passed = False
    print("  split_walls_result:")
    split = {
        "split": [{"forsk_id": "w01", "into": ["w02", "w03", "w04", "w05"], "openings": 2}],
        "walls": 4,
        "openings_moved": 2,
        "refused": [{"forsk_id": "w06", "why": "Not split: the wall bends in a curve near (0, 3000), and a curved wall stays one record."}],
        "ok": True,
        "message": "Split w01 into 4 walls, w02 to w05. 2 openings went to the walls that hold them.",
    }
    if not validate("responses/split_walls_result.json", split):
        all_passed = False
    linking = {**add_wall, "forsk_id": "w03", "joined": False, "joins": ["w01", "w02"], "holes": 0,
               "message": "Added w03, a 200 mm wall joined to w01 and w02, 1900 mm long and 3000 mm high. 1 room updated."}
    if not validate("responses/add_wall_result.json", linking):
        all_passed = False

    return all_passed


def test_invalid_examples():
    """Test that invalid examples are rejected."""
    print("\n=== Testing invalid examples (should fail) ===")

    invalid_examples = [
        # Missing required field
        ("commands/create_object.json", {"type": "BOX"}, "Missing params"),
        ("commands/create_object.json", {"params": {"width": 1}}, "Missing type"),
        # Invalid type enum
        ("commands/create_object.json", {"type": "INVALID", "params": {}}, "Invalid type"),
        # Missing code
        ("commands/execute_rhinoscript_python_code.json", {}, "Missing code"),
        # Missing run_command.command
        ("commands/run_command.json", {}, "run_command missing command"),
        ("commands/run_command.json", {"command": ""}, "run_command empty command"),
        # Unknown property on get_commands
        ("commands/get_commands.json", {"bogus": 1}, "get_commands unknown field"),
        ("commands/daylight_scene.json", {"selected": True}, "daylight_scene unknown field"),
        ("commands/daylight_paint.json", {"z": 50, "vertices": [], "colors": []}, "daylight_paint missing faces"),
        ("commands/daylight_paint.json", {"z": 50, "vertices": [[0, 0]], "colors": [[300, 0, 0]], "faces": []}, "daylight_paint colour out of range"),
        ("commands/daylight_paint.json", {"z": 50, "vertices": [], "colors": [], "faces": [[0, 1, 2, 3, 4]]}, "daylight_paint pentagon face"),
        ("commands/daylight_paint.json", {"z": 50, "vertices": [], "colors": [], "faces": [], "legend": {}}, "daylight_paint legend removed"),
        ("commands/capture_viewport.json", {"zoom_bbox": [0, 0, 1]}, "capture_viewport short zoom_bbox"),
        ("commands/daylight_clear.json", {"all": True}, "daylight_clear unknown field"),
        ("commands/panel_daylight.json", {"action": "paint"}, "panel_daylight unknown action"),
        ("commands/panel_daylight.json", {"action": "clear"}, "panel_daylight clear deletes the map"),
        ("commands/rooms_detect.json", {"layer": "A-ROOM"}, "rooms_detect takes no parameters"),
        ("commands/dxf_import.json", {}, "dxf_import needs a path"),
        ("commands/dxf_import.json", {"path": "/tmp/a.dxf", "units": "mm"}, "dxf_import unknown field"),
        ("commands/plan_import.json", {"plan_path": "/tmp/plan.json"}, "plan_import plan_path needs its image"),
        ("commands/plan_import.json", {"image_path": "/tmp/scan.pdf", "page": 1}, "plan_import page is a pdf_path's"),
        ("commands/plan_import.json", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json", "scale_hint": "100"}, "plan_import scale_hint is a ratio"),
        ("commands/plan_import.json", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json", "api_key": "x"}, "plan_import unknown field"),
        ("commands/plan_import.json", {"pdf_path": "/tmp/plan1.pdf", "image_path": "/tmp/plan.png"}, "plan_import a PDF or an image, not both"),
        ("commands/plan_import.json", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json", "page": 1}, "plan_import page is a PDF's"),
        ("commands/plan_import.json", {"pdf_path": "/tmp/plan1.pdf", "page": 0}, "plan_import page is 1-based"),
        ("commands/plan_import.json", {}, "plan_import needs a source"),
        ("commands/plan_scale.json", {"p1": [0, 0]}, "plan_scale needs two points"),
        ("commands/plan_scale.json", {"p1": [0, 0], "p2": [1000, 0], "length_mm": 0}, "plan_scale length above 0"),
        ("commands/plan_scale.json", {"p1": [0, 0], "p2": [1000, 0], "frame": "page"}, "plan_scale unknown frame"),
        # delete_object: all=false is meaningless and must be rejected
        ("commands/delete_object.json", {"all": False}, "delete_object all=false"),
        # delete_object: unknown properties rejected
        ("commands/delete_object.json", {"id": "12345678-1234-1234-1234-123456789012", "bogus": 1}, "delete_object unknown field"),
        # delete_object: mixed selectors (id + all) — ambiguous, would silently delete-all
        ("commands/delete_object.json", {"id": "12345678-1234-1234-1234-123456789012", "all": True}, "delete_object mixed id+all"),
        ("commands/delete_object.json", {"name": "Box1", "all": True}, "delete_object mixed name+all"),
        # create_object: params discriminated by type — sphere params on a BOX must fail
        ("commands/create_object.json", {"type": "BOX", "params": {"radius": 1}}, "create_object BOX with sphere params"),
        # create_object: PIPE removed from enum (use the dedicated `pipe` tool instead)
        ("commands/create_object.json", {"type": "PIPE", "params": {"curve_id": "x", "radius": 1}}, "create_object PIPE removed"),
        # create_object: top-level additionalProperties: false
        ("commands/create_object.json", {"type": "BOX", "params": {"width": 1, "length": 1, "height": 1}, "bogus": 1}, "create_object unknown top-level field"),
        # New schemas reject obvious mistakes
        ("commands/boolean_union.json", {"object_ids": ["only-one"]}, "boolean_union not enough ids (and bad GUID)"),
        ("commands/extrude_curve.json", {"curve_id": "12345678-1234-1234-1234-123456789012", "direction": [0, 0, 0]}, "extrude_curve zero direction"),
        ("commands/pipe.json", {"curve_id": "12345678-1234-1234-1234-123456789012", "radius": 0}, "pipe non-positive radius"),
        ("commands/undo.json", {"steps": 0}, "undo zero steps"),
        ("commands/loft.json", {"curve_ids": ["12345678-1234-1234-1234-123456789012"]}, "loft single curve"),
        ("commands/modify_objects.json", {"objects": [{"new_name": "X"}]}, "modify_objects no selector"),
        ("commands/modify_objects.json", {"objects": [{"id": "12345678-1234-1234-1234-123456789012"}], "all": False}, "modify_objects all=false"),
        ("commands/offset_curve.json", {"curve_id": "12345678-1234-1234-1234-123456789012", "distance": 0}, "offset_curve zero distance"),
        ("commands/get_object_attributes.json", {"id": "12345678-1234-1234-1234-123456789012", "bogus": 1}, "get_object_attributes unknown field"),
        ("commands/update_object_attributes.json", {"id": "12345678-1234-1234-1234-123456789012"}, "update_object_attributes no update fields"),
        ("commands/update_object_attributes.json", {"id": "12345678-1234-1234-1234-123456789012", "visible": False, "locked": True}, "update_object_attributes hidden and locked"),
        ("commands/update_object_attributes.json", {"id": "12345678-1234-1234-1234-123456789012", "user_strings": {"": "bad"}}, "update_object_attributes empty user string key"),
        ("commands/update_object_attributes.json", {"id": "12345678-1234-1234-1234-123456789012", "user_strings": {"nested": {"bad": True}}}, "update_object_attributes nested user string value"),
        ("commands/analyze_objects.json", {}, "analyze_objects no selector"),
        ("commands/analyze_objects.json", {"object_ids": []}, "analyze_objects empty object_ids"),
        ("commands/analyze_objects.json", {"id": "12345678-1234-1234-1234-123456789012", "selected": True}, "analyze_objects mixed selectors"),
        ("commands/analyze_objects.json", {"selected": False}, "analyze_objects selected=false"),
        ("commands/gh_create_document.json", {"template_path": "example.gh"}, "gh_create_document unknown field"),
        ("commands/gh_batch_search_components.json", {"queries": []}, "gh_batch_search_components empty queries"),
        ("commands/gh_get_component_type_info.json", {}, "gh_get_component_type_info missing selector"),
        ("commands/gh_batch_get_component_type_info.json", {"components": []}, "gh_batch_get_component_type_info empty components"),
        ("commands/gh_batch_get_component_type_info.json", {"components": [{"name": ""}]}, "gh_batch_get_component_type_info empty name"),
        ("commands/gh_get_graph.json", {}, "gh_get_graph missing graph_id"),
        ("commands/gh_get_graph.json", {"graph_id": ""}, "gh_get_graph empty graph_id"),
        ("commands/gh_clear_graph.json", {}, "gh_clear_graph missing graph_id"),
        ("commands/gh_clear_graph.json", {"graph_id": "", "recompute": True}, "gh_clear_graph empty graph_id"),
        ("commands/gh_get_component_info.json", {}, "gh_get_component_info missing selector"),
        ("commands/gh_get_canvas_state.json", {"max_items": -1}, "gh_get_canvas_state negative max_items"),
        ("commands/gh_capture_preview.json", {"targets": []}, "gh_capture_preview empty targets"),
        ("commands/gh_capture_preview.json", {"width": 50}, "gh_capture_preview too narrow"),
        ("commands/gh_capture_preview.json", {"padding_factor": 0.5}, "gh_capture_preview bad padding"),
        ("commands/gh_run_solution.json", {"timeout_ms": 1000}, "gh_run_solution unknown timeout field"),
        ("commands/gh_build_graph.json", {"components": []}, "gh_build_graph empty components"),
        ("commands/gh_build_graph.json", {"components": [{"component_name": "Addition"}]}, "gh_build_graph component missing alias"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add"}]}, "gh_build_graph component missing selector"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "1bad", "component_name": "Addition"}]}, "gh_build_graph bad alias"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add", "component_name": "Addition"}], "connections": [{"source": "add"}]}, "gh_build_graph connection missing target"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add", "component_name": "Addition"}], "layout": {"x_spacing": 0}}, "gh_build_graph bad layout spacing"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add", "component_name": "Addition"}], "layout": {"max_columns": 0}}, "gh_build_graph bad layout max_columns"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add", "component_name": "Addition"}], "open_canvas": "yes"}, "gh_build_graph open_canvas not boolean"),
        ("commands/gh_build_graph.json", {"components": [{"alias": "add", "component_name": "Addition"}], "preview_policy": {"mode": "only"}}, "gh_build_graph preview policy missing targets"),
        ("commands/gh_mutate_graph.json", {"operations": []}, "gh_mutate_graph empty operations"),
        ("commands/gh_mutate_graph.json", {"operations": [{"target": "x"}]}, "gh_mutate_graph op missing op"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "rename", "target": "x"}]}, "gh_mutate_graph bad op"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "create", "alias": "bad alias", "component_name": "Panel"}]}, "gh_mutate_graph bad alias"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "update", "target": "x"}], "preview_policy": {"mode": "show"}}, "gh_mutate_graph preview policy missing targets"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "update", "target": "x"}], "layout": {"max_columns": 0}}, "gh_mutate_graph bad layout max_columns"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "update", "target": "x"}], "open_canvas": "yes"}, "gh_mutate_graph open_canvas not boolean"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "update", "target": "x"}], "verify": {"outputs": []}}, "gh_mutate_graph empty verify outputs"),
        ("commands/gh_mutate_graph.json", {"operations": [{"op": "recompute"}], "fail_on_verification_error": "yes"}, "gh_mutate_graph fail_on_verification_error not boolean"),
        ("commands/gh_add_component.json", {"position": [10, 20]}, "gh_add_component missing component selector"),
        ("commands/gh_add_component.json", {"component_name": "Circle", "position": [1, 2, 3]}, "gh_add_component bad position"),
        ("commands/gh_delete_component.json", {}, "gh_delete_component missing selector"),
        ("commands/gh_layout_components.json", {"component_ids": []}, "gh_layout_components empty component_ids"),
        ("commands/gh_layout_components.json", {"x_spacing": 0}, "gh_layout_components zero x spacing"),
        ("commands/gh_layout_components.json", {"start_position": [1, 2, 3]}, "gh_layout_components bad start position"),
        ("commands/gh_connect_components.json", {"source_instance_id": "bad", "target_instance_id": "12345678-1234-1234-1234-123456789012"}, "gh_connect_components bad source guid"),
        ("commands/gh_disconnect_components.json", {"disconnect_all": True}, "gh_disconnect_components missing target"),
        ("commands/gh_disconnect_components.json", {"target_instance_id": "12345678-1234-1234-1234-123456789012"}, "gh_disconnect_components missing source when not disconnect_all"),
        ("commands/gh_set_parameter_value.json", {"nickname": "Radius"}, "gh_set_parameter_value missing value"),
        ("commands/gh_get_parameter_value.json", {"nickname": "Radius", "output_index": -1}, "gh_get_parameter_value negative output"),
        ("commands/gh_update_component.json", {"instance_id": "12345678-1234-1234-1234-123456789012"}, "gh_update_component no updates"),
        ("commands/gh_clear_canvas.json", {"confirm": True}, "gh_clear_canvas unknown field"),
        ("commands/set_layer_material.json", {}, "set_layer_material missing layer_name"),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL"}, "set_layer_material no material selector"),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "preset": "oak"}, "set_layer_material oak not a schema preset"),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "diffuse_rgb": [200, 180, 160]}, "set_layer_material diffuse without name"),
        ("commands/set_layer_material.json", {"layer_name": "A-WALL", "preset": "plaster", "bogus": 1}, "set_layer_material unknown field"),
        ("commands/set_display_mode.json", {}, "set_display_mode missing mode"),
        ("commands/set_display_mode.json", {"mode": "Ghosted"}, "set_display_mode unsupported mode"),
        ("commands/walls_from_layer.json", {"apply_default_materials": "yes"}, "walls_from_layer apply_default_materials not boolean"),
        ("commands/roof_flat_from_walls.json", {"thickness": 0}, "roof_flat_from_walls thickness 0"),
        ("commands/roof_flat_from_walls.json", {"overhang": -1}, "roof_flat_from_walls overhang negative"),
        ("commands/roof_flat_from_walls.json", {"bogus": 1}, "roof_flat_from_walls unknown field"),
        ("commands/rooms_from_layer.json", {"join_tolerance": 0}, "rooms_from_layer join_tolerance 0"),
        ("commands/rooms_from_layer.json", {"bogus": 1}, "rooms_from_layer unknown field"),
        ("commands/mark_as_existing.json", {"ids": ["not-a-guid"]}, "mark_as_existing bad guid"),
        ("commands/mark_as_existing.json", {"bogus": 1}, "mark_as_existing unknown field"),
        ("commands/delete_opening.json", {"bogus": 1}, "delete_opening unknown field"),
        ("commands/add_opening.json", {}, "add_opening missing opening_kind"),
        ("commands/add_opening.json", {"opening_kind": "portal"}, "add_opening invalid opening_kind"),
        ("commands/add_opening.json", {"opening_kind": "door", "width": 0}, "add_opening width 0"),
        ("commands/add_opening.json", {"opening_kind": "door", "bogus": 1}, "add_opening unknown field"),
        ("commands/move_opening.json", {"bogus": 1}, "move_opening unknown field"),
        ("commands/room_push_pull.json", {"distance_mm": 500}, "room_push_pull needs a side"),
        ("commands/split_walls.json", {"side": "north"}, "split_walls takes no side"),
        ("commands/split_walls.json", {"id": "w01"}, "split_walls id is a GUID"),
        ("commands/room_push_pull.json", {"side": "north", "distance_mm": 500, "way": "up"}, "room_push_pull way is out or in"),
        ("commands/room_push_pull.json", {"side": "north", "distance_mm": 0}, "room_push_pull distance above 0"),
        ("commands/room_push_pull.json", {"side": "north", "distance_mm": 500, "at": [0, 0]}, "room_push_pull unknown field"),
        ("commands/add_room_area.json", {"points": [[0, 0], [4000, 0]]}, "add_room_area needs three corners"),
        ("commands/add_room_area.json", {"points": [[0, 0, 0], [4000, 0, 0], [4000, 3000, 0]]}, "add_room_area corners are [x, y]"),
        ("commands/add_room_area.json", {"points": [[0, 0], [4000, 0], [4000, 3000]], "z": 0}, "add_room_area unknown field"),
        (
            "responses/move_wall_result.json",
            {"host_id": "x", "toward": "north", "distance_mm": 500, "faces_before": [0, 1], "faces_after": [0, 1],
             "openings_moved": [], "ok": True, "message": "m", "followed": [{"forsk_id": "w03", "change_mm": 500}]},
            "move_wall followed needs the wall's name",
        ),
        (
            "responses/delete_wall_result.json",
            {"host_id": "", "record_deleted": True, "openings_deleted": [], "holes": 0, "ok": True, "message": "m",
             "followed": [{"forsk_id": "w03", "wall": "the east wall", "change_mm": "short"}]},
            "delete_wall followed change is a number",
        ),
        (
            "responses/split_walls_result.json",
            {"split": [{"forsk_id": "w01", "into": "w02", "openings": 0}], "walls": 1, "openings_moved": 0,
             "refused": [], "ok": True, "message": "m"},
            "split_walls into is a list",
        ),
        (
            "responses/split_walls_result.json",
            {"split": [], "walls": 0, "openings_moved": 0, "refused": [{"forsk_id": "w01"}], "ok": True, "message": "m"},
            "split_walls refused says why",
        ),
        (
            "responses/add_wall_result.json",
            {"host_id": "x", "forsk_id": "w03", "joined": False, "from": [0, 0], "to": [0, 1], "thickness": 200,
             "holes": 0, "ok": True, "message": "m", "joins": "w01"},
            "add_wall joins is a list",
        ),
        ("commands/move_wall.json", {"side": "north", "at": [0, 0], "toward": "north", "distance_mm": 500}, "move_wall side and at"),
        ("commands/move_wall.json", {"side": "north", "toward": "up", "distance_mm": 500}, "move_wall unknown toward"),
        ("commands/move_wall.json", {"side": "north", "toward": "north", "distance_mm": 0}, "move_wall distance above 0"),
        ("commands/move_wall.json", {"at": [0, 0, 0], "toward": "north", "distance_mm": 500}, "move_wall at is x, y"),
        ("commands/move_wall.json", {"side": "north", "distance_mm": 500}, "move_wall missing toward"),
        ("commands/delete_wall.json", {"side": "north", "at": [0, 0]}, "delete_wall side and at"),
        ("commands/delete_wall.json", {"side": "up"}, "delete_wall unknown side"),
        ("commands/add_wall.json", {}, "add_wall needs a centreline"),
        ("commands/add_wall.json", {"from": [0, 0]}, "add_wall needs both ends"),
        ("commands/add_wall.json", {"from": [0, 0], "to": [1000, 0], "line_id": "12345678-1234-1234-1234-123456789012"}, "add_wall points and line_id"),
        ("commands/add_wall.json", {"from": [0, 0], "to": [1000, 0], "thickness": 700}, "add_wall thickness at most 600"),
        ("commands/add_wall.json", {"from": [0, 0, 0], "to": [1000, 0]}, "add_wall from is x, y"),
        ("commands/set_opening.json", {"bogus": 1}, "set_opening unknown field"),
        ("commands/set_opening.json", {"width": 0}, "set_opening width 0"),
        ("commands/set_opening.json", {"id": "not-a-guid", "sill": 900}, "set_opening bad guid"),
        ("commands/set_opening_type.json", {}, "set_opening_type missing change"),
        ("commands/set_opening_type.json", {"type": "door.portal"}, "set_opening_type bad type"),
        ("commands/set_opening_type.json", {"hand": "left"}, "set_opening_type bad hand"),
        ("commands/set_opening_type.json", {"swing": "sideways"}, "set_opening_type bad swing"),
        ("commands/set_opening_type.json", {"type": "door.sliding", "bogus": 1}, "set_opening_type unknown field"),
        ("commands/set_opening_type.json", {"id": "not-a-guid", "type": "door.sliding"}, "set_opening_type bad guid"),
        ("commands/set_opening_type.json", {"swing": "flip", "all": True}, "set_opening_type all without type"),
        ("commands/area_stats.json", {"floor": 1}, "area_stats unknown field"),
        ("commands/rebuild_host_wall.json", {"bogus": 1}, "rebuild_host_wall unknown field"),
        ("commands/rebuild_host_wall.json", {"id": "not-a-guid"}, "rebuild_host_wall bad guid"),
        ("commands/clear_generated.json", {"dry_run": "yes"}, "clear_generated dry_run not bool"),
        ("commands/clear_generated.json", {"bogus": 1}, "clear_generated unknown field"),
        ("commands/make2d_view.json", {}, "make2d_view missing view"),
        ("commands/make2d_view.json", {"view": "section"}, "make2d_view unknown view"),
        ("commands/make2d_view.json", {"view": "plan", "ids": ["not-a-guid"]}, "make2d_view bad guid"),
        ("commands/make2d_view.json", {"view": "plan", "bogus": 1}, "make2d_view unknown field"),
        ("commands/sheet_pack.json", {"views": ["section"]}, "sheet_pack unknown view"),
        ("commands/sheet_pack.json", {"bogus": 1}, "sheet_pack unknown field"),
        ("commands/clear_drawings.json", {"views": ["section"]}, "clear_drawings unknown view"),
        ("commands/clear_drawings.json", {"dry_run": "yes"}, "clear_drawings dry_run not bool"),
        ("commands/clear_drawings.json", {"bogus": 1}, "clear_drawings unknown field"),
        ("commands/set_project_meta.json", {"bogus": 1}, "set_project_meta unknown field"),
        ("commands/set_project_meta.json", {"project": 1}, "set_project_meta project not string"),
        ("commands/set_project_meta.json", {"project_no": 2026}, "set_project_meta project_no not string"),
        ("commands/set_project_meta.json", {"project": "Garage", "firm": "X"}, "set_project_meta unknown key"),
        ("commands/layout_pack.json", {"views": ["section"]}, "layout_pack unknown view"),
        ("commands/layout_pack.json", {"views": ["section_ab"]}, "layout_pack section is one letter"),
        ("commands/layout_pack.json", {"views": ["detail_30_1"]}, "layout_pack detail scale is on the ladder"),
        ("commands/clear_layouts.json", {"views": ["section"]}, "clear_layouts unknown view"),
        ("commands/section_add.json", {"letter": "AB", "room": "Stue"}, "section_add letter is one letter"),
        ("commands/section_add.json", {"room": "Stue", "axis": "diagonal"}, "section_add unknown axis"),
        ("commands/section_add.json", {"from": [0, 0, 0], "to": [1, 1]}, "section_add from is x, y"),
        ("commands/section_add.json", {"room": "Stue", "look": "up"}, "section_add unknown look"),
        ("commands/section_add.json", {"bogus": 1}, "section_add unknown field"),
        ("commands/section_clear.json", {"letter": "AA"}, "section_clear letter is one letter"),
        ("commands/print_profile.json", {"name": "neon"}, "print_profile name is a shipped profile"),
        ("commands/print_profile.json", {"bogus": 1}, "print_profile unknown field"),
        ("commands/section_pick.json", {"letter": "A"}, "section_pick takes no fields"),
        ("commands/layout_pack.json", {"paper": "A5"}, "layout_pack paper not A4 to A1"),
        ("commands/layout_pack.json", {"scale": 0}, "layout_pack scale 0"),
        ("commands/layout_pack.json", {"bogus": 1}, "layout_pack unknown field"),
        ("commands/layout_pack.json", {"schedules": "sheet"}, "layout_pack schedules is a view, not a field"),
        ("commands/layout_pack.json", {"schedule_kinds": []}, "layout_pack no schedule kinds"),
        ("commands/layout_pack.json", {"schedule_kinds": ["stair"]}, "layout_pack unknown schedule kind"),
        ("commands/export_pdf.json", {}, "export_pdf missing path"),
        ("commands/export_pdf.json", {"path": ""}, "export_pdf empty path"),
        ("commands/export_pdf.json", {"path": "/tmp/forsk-plan.pdf", "bogus": 1}, "export_pdf unknown field"),
        ("commands/export_sheets.json", {}, "export_sheets missing folder"),
        ("commands/details.json", {}, "details missing action"),
        ("commands/export_ifc.json", {}, "export_ifc missing path"),
        ("commands/export_csv.json", {}, "export_csv missing path"),
        ("commands/export_csv.json", {"path": "/tmp/a.csv", "locale": "nb"}, "export_csv unknown field"),
        ("commands/add_stair.json", {"from": [0, 0]}, "add_stair from only one point"),
        ("commands/add_stair.json", {"riser_max": 50}, "add_stair riser too low"),
        ("commands/add_stair.json", {"rise": "high"}, "add_stair rise word"),
        ("commands/add_stair.json", {"shape": "l"}, "add_stair unknown shape field"),
        ("commands/edit_stair.json", {}, "edit_stair nothing to change"),
        ("commands/edit_stair.json", {"id": "S01"}, "edit_stair id only"),
        ("commands/edit_stair.json", {"going": 80}, "edit_stair going too short"),
        ("commands/delete_stair.json", {"all": True}, "delete_stair unknown field"),
        ("commands/add_furniture.json", {}, "add_furniture missing item"),
        ("commands/add_furniture.json", {"item": "bed", "rotation": 90}, "add_furniture rotation without at"),
        ("commands/add_furniture.json", {"item": "bed", "seats": 0}, "add_furniture no seats"),
        ("commands/add_furniture.json", {"item": "bed", "colour": "red"}, "add_furniture unknown field"),
        ("commands/move_furniture.json", {"id": "F01"}, "move_furniture nothing to change"),
        ("commands/move_furniture.json", {"to": [1]}, "move_furniture one coordinate"),
        ("commands/delete_furniture.json", {"kind": "bed"}, "delete_furniture unknown field"),
        ("commands/furnish_room.json", {"density": "tight"}, "furnish_room unknown density"),
        ("commands/jump_inside.json", {"lens": 24}, "jump_inside unknown field"),
        ("commands/furnish_room.json", {"variant": "random"}, "furnish_room unknown variant"),
        ("commands/furnish_room.json", {"room": "R01", "style": "modern"}, "furnish_room unknown field"),
        ("commands/export_ifc.json", {"path": "/tmp/a.ifc", "format": "ifc2x3"}, "export_ifc unknown field"),
        ("commands/details.json", {"action": "move"}, "details unknown action"),
        ("commands/details.json", {"action": "add", "refs": []}, "details no refs"),
        ("commands/details.json", {"action": "add", "refs": [{"wall": "w01", "opening": "o"}]}, "details ref with two keys"),
        ("commands/details.json", {"action": "remove", "ids": ["U01"]}, "details id not DET.."),
        ("commands/export_sheets.json", {"folder": "/tmp/x", "format": "pdf"}, "export_sheets format not dwg or dxf"),
        ("commands/export_sheets.json", {"folder": "/tmp/x", "path": "/tmp/x.dwg"}, "export_sheets unknown field"),
        ("commands/clear_layouts.json", {"views": ["section"]}, "clear_layouts unknown view"),
        ("commands/clear_layouts.json", {"dry_run": "yes"}, "clear_layouts dry_run not bool"),
        ("commands/clear_layouts.json", {"bogus": 1}, "clear_layouts unknown field"),
    ]

    all_rejected = True
    for schema_path, example, description in invalid_examples:
        try:
            schema = load_schema_with_refs(schema_path)
            validator = Draft202012Validator(schema)
            errors = list(validator.iter_errors(example))

            if errors:
                print(f"  CORRECTLY REJECTED: {description}")
            else:
                print(f"  INCORRECTLY ACCEPTED: {description}")
                all_rejected = False
        except Exception as e:
            print(f"  ERROR checking {description}: {e}")
            all_rejected = False

    return all_rejected


def test_contract_synchronization_across_tiers():
    """All three tiers must agree on the set of command names:

    - protocol.json command.type enum
    - Python: every send_command("<name>", ...) call in the tool wrappers
    - C#: every [McpCommand("<name>")] attribute in the plugin

    A mismatch means an LLM client can call a tool that the plugin doesn't
    understand, or a plugin handler that nothing routes to.
    """
    import re

    print("\n=== Testing contract sync across tiers ===")
    repo_root = CONTRACTS_DIR.parent

    with open(CONTRACTS_DIR / "protocol.json") as f:
        protocol = json.load(f)
    protocol_cmds = set(protocol["$defs"]["command"]["properties"]["type"]["enum"])

    # Python tools: scrape send_command("<name>") calls. We accept the
    # simple positional form because every wrapper uses it.
    py_pat = re.compile(
        r"""(?:send_command|_send|send_grasshopper_command)\(\s*["']([a-z_][a-z0-9_]*)["']"""
    )
    py_cmds: set[str] = set()
    tools_dir = repo_root / "server" / "src" / "rhinomcp" / "tools"
    for p in tools_dir.glob("*.py"):
        if p.name.startswith("_"):
            continue
        py_cmds.update(py_pat.findall(p.read_text()))

    # C# handlers: scrape [McpCommand("<name>")] attribute usages.
    cs_pat = re.compile(r'\[McpCommand\(\s*"([a-z_][a-z0-9_]*)"')
    cs_cmds: set[str] = set()
    funcs_dir = repo_root / "plugin" / "Functions"
    for p in funcs_dir.glob("*.cs"):
        cs_cmds.update(cs_pat.findall(p.read_text()))

    all_passed = True

    py_missing = sorted(py_cmds - protocol_cmds)
    if py_missing:
        print(f"  FAIL: Python wraps commands not in protocol enum: {py_missing}")
        all_passed = False

    cs_missing_in_protocol = sorted(cs_cmds - protocol_cmds)
    if cs_missing_in_protocol:
        print(f"  FAIL: C# handles commands not in protocol enum: {cs_missing_in_protocol}")
        all_passed = False

    protocol_missing_in_cs = sorted(protocol_cmds - cs_cmds)
    if protocol_missing_in_cs:
        print(f"  FAIL: protocol commands without a C# [McpCommand] handler: {protocol_missing_in_cs}")
        all_passed = False

    # Python-vs-C# is implicit (each checked against protocol), but call it
    # out for the most useful failure message.
    py_unhandled = sorted(py_cmds - cs_cmds)
    if py_unhandled:
        print(f"  FAIL: Python wrappers without a C# handler: {py_unhandled}")
        all_passed = False

    # Protocol-vs-Python: a command added to protocol.json + C# but missing
    # a Python wrapper would silently be unreachable from MCP clients.
    protocol_missing_in_py = sorted(protocol_cmds - py_cmds)
    if protocol_missing_in_py:
        print(f"  FAIL: protocol commands without a Python wrapper: {protocol_missing_in_py}")
        all_passed = False

    if all_passed:
        print(f"  PASS: {len(protocol_cmds)} commands in sync across protocol, Python, C#")
    return all_passed


# Write commands that add nothing to model space, or own the active view.
# Every other write command carries ModelView = true.
KEEPS_VIEW = {
    # These activate pages. layout_pack is ModelView instead: it bakes curves
    # before it opens pages, and a layout left active would put them in page
    # space so the detail prints a blank sheet.
    "export_pdf", "clear_layouts",
    # The user's own code or command decides.
    "run_command", "execute_rhinoscript_python_code", "execute_rhinocommon_csharp_code",
    # Sets the active view's display mode: switching first would change the target.
    "set_display_mode",
    # Delete, select, transform in place, or write attributes, layers or doc strings.
    "delete_object", "delete_layer", "clear_generated", "clear_drawings", "daylight_clear",
    "section_clear", "select_objects", "modify_object", "modify_objects",
    "update_object_attributes", "mark_as_existing", "rooms_set_type", "create_layer", "get_or_set_current_layer",
    "set_layer_material", "set_project_meta", "section_add", "print_profile",
    # Writes the forsk/details document string only.
    "details",
}


def test_write_commands_choose_model_view():
    """Every write command either runs in a model view or is listed in KEEPS_VIEW.

    While a layout is the active view (export_pdf leaves one), Rhino puts a new
    object in that page's space whatever its attributes say, and clear_layouts
    later deletes it with the page. A new command that adds objects must say so.
    """
    import re

    print("\n=== Testing write commands choose a model view ===")
    funcs_dir = CONTRACTS_DIR.parent / "plugin" / "Functions"
    attr_pat = re.compile(r'\[McpCommand\(\s*"([a-z_][a-z0-9_]*)"([^)]*)\)\]')
    model_view, read_only, writes = set(), set(), set()
    for p in funcs_dir.glob("*.cs"):
        for name, args in attr_pat.findall(p.read_text()):
            if re.search(r"ReadOnly\s*=\s*true", args):
                read_only.add(name)
            else:
                writes.add(name)
            if re.search(r"ModelView\s*=\s*true", args):
                model_view.add(name)

    # Grasshopper canvas commands add no Rhino objects.
    undecided = sorted(
        n for n in writes - model_view - KEEPS_VIEW if not n.startswith("gh_")
    )
    both = sorted(model_view & KEEPS_VIEW)
    stale = sorted(KEEPS_VIEW - writes)
    on_read_only = sorted(model_view & read_only)
    if undecided:
        print(f"  FAIL: write commands with no ModelView and not in KEEPS_VIEW: {undecided}")
    if both:
        print(f"  FAIL: ModelView set on a KEEPS_VIEW command: {both}")
    if stale:
        print(f"  FAIL: KEEPS_VIEW names no write command: {stale}")
    if on_read_only:
        print(f"  FAIL: ModelView on a read-only command: {on_read_only}")
    ok = not (undecided or both or stale or on_read_only)
    if ok:
        print(f"  PASS: {len(model_view)} write commands run in a model view")
    return ok


def test_schema_coverage_against_protocol():
    """Every command in the protocol envelope must have a schema file in commands/."""
    print("\n=== Testing schema coverage matches protocol enum ===")

    with open(CONTRACTS_DIR / "protocol.json") as f:
        protocol = json.load(f)
    commands_in_protocol = set(protocol["$defs"]["command"]["properties"]["type"]["enum"])

    commands_dir = CONTRACTS_DIR / "commands"
    schema_files = {p.stem for p in commands_dir.glob("*.json")}

    missing_schemas = sorted(commands_in_protocol - schema_files)
    orphan_schemas = sorted(schema_files - commands_in_protocol)
    # capture_viewport / get_or_set_current_layer-style fixtures are still valid orphans
    # only if they sit in the enum — anything left over is genuinely orphaned.

    if missing_schemas:
        print(f"  FAIL: commands without schemas: {missing_schemas}")
    if orphan_schemas:
        print(f"  WARN: schema files without a protocol enum entry: {orphan_schemas}")

    if not missing_schemas:
        print(f"  PASS: all {len(commands_in_protocol)} protocol commands have schemas")
    return not missing_schemas


def test_protocol_envelope():
    """Test the {type, params} envelope from protocol.json against the dispatch table."""
    print("\n=== Testing protocol envelope ===")

    with open(CONTRACTS_DIR / "protocol.json") as f:
        protocol = json.load(f)
    envelope = protocol["$defs"]["command"]
    validator = Draft202012Validator(envelope)

    # Mirrors the C# dispatch table in plugin/RhinoMCPServer.cs.
    # Update both sides together when adding a command.
    expected_commands = [
        "create_object", "create_objects", "modify_object", "modify_objects",
        "delete_object", "get_object_info", "get_selected_objects_info",
        "get_object_attributes", "update_object_attributes",
        "analyze_objects",
        "get_document_summary", "get_objects", "select_objects",
        "create_layer", "delete_layer", "get_or_set_current_layer",
        "execute_rhinoscript_python_code", "execute_rhinocommon_csharp_code",
        "capture_viewport", "undo", "redo",
        "boolean_union", "boolean_difference", "boolean_intersection",
        "loft", "extrude_curve", "sweep1", "offset_curve", "pipe",
        "project_curve", "intersect_curves", "split_curve",
        "walls_from_layer", "floor_from_layer", "roof_flat_from_walls", "openings_from_layer",
        "rooms_from_layer",
        "mark_as_existing",
        "delete_opening", "add_opening", "move_opening", "set_opening", "set_opening_type",
        "move_wall", "delete_wall", "add_wall", "split_walls", "rebuild_host_wall",
        "clear_generated",
        "make2d_view", "sheet_pack", "clear_drawings",
        "set_project_meta", "layout_pack", "export_pdf", "clear_layouts", "print_profile",
        "set_layer_material", "set_display_mode",
        "run_command", "get_commands",
        "gh_create_document",
        "gh_get_document_info", "gh_search_components",
        "gh_batch_search_components", "gh_list_component_categories",
        "gh_get_available_components", "gh_get_component_type_info",
        "gh_batch_get_component_type_info",
        "gh_get_graph", "gh_clear_graph",
        "gh_list_components", "gh_get_component_info",
        "gh_get_canvas_state", "gh_capture_preview", "gh_run_solution", "gh_expire_solution",
        "gh_build_graph", "gh_mutate_graph", "gh_add_component", "gh_delete_component", "gh_layout_components", "gh_connect_components",
        "gh_disconnect_components", "gh_set_parameter_value",
        "gh_get_parameter_value", "gh_update_component", "gh_clear_canvas",
    ]

    all_passed = True

    for cmd in expected_commands:
        envelope_msg = {"type": cmd, "params": {}}
        errors = list(validator.iter_errors(envelope_msg))
        if errors:
            print(f"  FAIL: '{cmd}' rejected by envelope ({errors[0].message})")
            all_passed = False
        else:
            print(f"  PASS: '{cmd}'")

    # Negative: unknown command type must be rejected
    unknown = {"type": "totally_made_up_command", "params": {}}
    errors = list(validator.iter_errors(unknown))
    if not errors:
        print("  FAIL: envelope accepted an unknown command type")
        all_passed = False
    else:
        print("  CORRECTLY REJECTED: unknown command type")

    return all_passed


def main():
    """Run all tests."""
    print("RhinoMCP Schema Validation Tests")
    print("=" * 40)

    results = []
    results.append(("create_object", test_create_object_commands()))
    results.append(("modify_object", test_modify_object_commands()))
    results.append(("delete_object", test_delete_object_commands()))
    results.append(("select_objects", test_select_objects_commands()))
    results.append(("layer commands", test_layer_commands()))
    results.append(("new commands", test_new_commands()))
    results.append(("other commands", test_other_commands()))
    results.append(("responses", test_responses()))
    results.append(("invalid rejection", test_invalid_examples()))
    results.append(("schema coverage", test_schema_coverage_against_protocol()))
    results.append(("contract sync (3 tiers)", test_contract_synchronization_across_tiers()))
    results.append(("model view on write", test_write_commands_choose_model_view()))
    results.append(("protocol envelope", test_protocol_envelope()))

    print("\n" + "=" * 40)
    print("SUMMARY")
    print("=" * 40)

    all_passed = True
    for name, passed in results:
        status = "PASS" if passed else "FAIL"
        print(f"  {name}: {status}")
        if not passed:
            all_passed = False

    print()
    if all_passed:
        print("All tests passed!")
        return 0
    else:
        print("Some tests failed!")
        return 1


if __name__ == "__main__":
    sys.exit(main())
