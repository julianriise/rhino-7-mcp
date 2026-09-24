"""
Unit tests for MCP tool functions.

These tests verify that MCP tool functions correctly:
1. Accept correct parameters
2. Call send_command with proper command type and params
3. Return properly formatted results

Uses mocking to avoid needing the actual Rhino connection.
"""

import pytest
from unittest.mock import patch, MagicMock


class TestCreateObjectTool:
    """Tests for create_object tool."""

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_box(self, mock_get_conn):
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "TestBox",
            "type": "BOX"
        }
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None,
            type="BOX",
            name="TestBox",
            params={"width": 1, "length": 1, "height": 1}
        )

        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "create_object"
        assert call_args[0][1]["type"] == "BOX"
        assert call_args[0][1]["name"] == "TestBox"
        # Structured return: callers need the id for follow-up calls.
        assert result["success"] is True
        assert result["id"] == "abc-123"
        assert result["type"] == "BOX"
        assert "Created BOX object" in result["message"]

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_sphere_with_color(self, mock_get_conn):
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "def-456",
            "name": "RedSphere",
            "type": "SPHERE"
        }
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None,
            type="SPHERE",
            name="RedSphere",
            color=[255, 0, 0],
            params={"radius": 5}
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["color"] == [255, 0, 0]
        assert result["name"] == "RedSphere"

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_with_transform(self, mock_get_conn):
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "ghi-789",
            "name": "TransformedBox",
            "type": "BOX"
        }
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None,
            type="BOX",
            name="TransformedBox",
            params={"width": 1, "length": 1, "height": 1},
            translation=[10, 20, 30],
            rotation=[0.5, 0, 0],
            scale=[2, 2, 2]
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["translation"] == [10, 20, 30]
        assert call_args[0][1]["rotation"] == [0.5, 0, 0]
        assert call_args[0][1]["scale"] == [2, 2, 2]

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_object_error_propagates(self, mock_get_conn):
        """Connection failures should surface as exceptions, not "Error ..." strings —
        otherwise MCP clients see a successful tool call with an error message."""
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.side_effect = Exception("Connection failed")
        mock_get_conn.return_value = mock_conn

        with pytest.raises(Exception, match="Connection failed"):
            create_object(ctx=None, type="BOX", params={"width": 1, "length": 1, "height": 1})

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_surfaces_bounding_box(self, mock_get_conn):
        """The plugin serializes the new object's bounding box; the wrapper must
        pass it through instead of discarding it, so the client learns where the
        object landed without a follow-up query."""
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "TestBox",
            "type": "Brep",
            "bounding_box": [[0, 0, 0], [1, 2, 3]],
        }
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None, type="BOX", name="TestBox",
            params={"width": 1, "length": 2, "height": 3},
        )

        assert result["bounding_box"] == [[0, 0, 0], [1, 2, 3]]
        # Purely additive: existing fields are untouched.
        assert result["success"] is True
        assert result["id"] == "abc-123"
        assert "geometry" not in result  # a box carries no geometry block

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_surfaces_geometry_for_curve_like(self, mock_get_conn):
        """Curve-like types (LINE here) carry a geometry block; it rides through too."""
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "line-1",
            "name": "Edge",
            "type": "LINE",
            "bounding_box": [[0, 0, 0], [3, 4, 0]],
            "geometry": {"start": [0, 0, 0], "end": [3, 4, 0]},
        }
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None, type="LINE", name="Edge",
            params={"start": [0, 0, 0], "end": [3, 4, 0]},
        )

        assert result["geometry"] == {"start": [0, 0, 0], "end": [3, 4, 0]}
        assert result["bounding_box"] == [[0, 0, 0], [3, 4, 0]]

    @patch('rhinomcp.tools.create_object.get_rhino_connection')
    def test_create_omits_perception_fields_when_plugin_absent(self, mock_get_conn):
        """If the plugin reports neither field (an older plugin, or a path that
        doesn't serialize them), the response shape stays exactly as before —
        no null keys leak in."""
        from rhinomcp.tools.create_object import create_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"id": "x", "name": "Y", "type": "BOX"}
        mock_get_conn.return_value = mock_conn

        result = create_object(
            ctx=None, type="BOX", params={"width": 1, "length": 1, "height": 1},
        )

        assert "bounding_box" not in result
        assert "geometry" not in result
        assert result["id"] == "x"


class TestCreateObjectsTool:
    """Tests for create_objects tool."""

    @patch('rhinomcp.tools.create_objects.get_rhino_connection')
    def test_create_multiple_objects(self, mock_get_conn):
        from rhinomcp.tools.create_objects import create_objects

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "objects": {
                "Box1": {"id": "id1", "name": "Box1"},
                "Box2": {"id": "id2", "name": "Box2"}
            },
            "success_count": 2,
            "failure_count": 0,
            "total": 2,
            "errors": []
        }
        mock_get_conn.return_value = mock_conn

        # create_objects takes a List of objects, not a Dict
        result = create_objects(
            ctx=None,
            objects=[
                {"type": "BOX", "name": "Box1", "params": {"width": 1, "length": 1, "height": 1}},
                {"type": "BOX", "name": "Box2", "params": {"width": 2, "length": 2, "height": 2}}
            ]
        )

        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "create_objects"
        assert "2" in result


class TestModifyObjectTool:
    """Tests for modify_object tool."""

    @patch('rhinomcp.tools.modify_object.get_rhino_connection')
    def test_modify_by_id(self, mock_get_conn):
        from rhinomcp.tools.modify_object import modify_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "NewName"
        }
        mock_get_conn.return_value = mock_conn

        result = modify_object(
            ctx=None,
            id="abc-123",
            new_name="NewName"
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "modify_object"
        assert call_args[0][1]["id"] == "abc-123"
        assert call_args[0][1]["new_name"] == "NewName"

    @patch('rhinomcp.tools.modify_object.get_rhino_connection')
    def test_modify_color(self, mock_get_conn):
        from rhinomcp.tools.modify_object import modify_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "ColoredObject"
        }
        mock_get_conn.return_value = mock_conn

        result = modify_object(
            ctx=None,
            id="abc-123",
            new_color=[255, 128, 0]
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["new_color"] == [255, 128, 0]

    @patch('rhinomcp.tools.modify_object.get_rhino_connection')
    def test_modify_surfaces_new_bounding_box(self, mock_get_conn):
        """The plugin re-serializes after the edit, so bounding_box is the NEW
        post-transform extent. A translate must report the moved box — that is
        the whole point of surfacing it."""
        from rhinomcp.tools.modify_object import modify_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "Moved",
            "bounding_box": [[10, 20, 30], [11, 22, 33]],
        }
        mock_get_conn.return_value = mock_conn

        result = modify_object(ctx=None, id="abc-123", translation=[10, 20, 30])

        assert result["bounding_box"] == [[10, 20, 30], [11, 22, 33]]
        assert result["success"] is True
        assert result["id"] == "abc-123"

    @patch('rhinomcp.tools.modify_object.get_rhino_connection')
    def test_modify_surfaces_geometry(self, mock_get_conn):
        from rhinomcp.tools.modify_object import modify_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "line-1",
            "name": "Edge",
            "bounding_box": [[0, 0, 0], [5, 0, 0]],
            "geometry": {"start": [0, 0, 0], "end": [5, 0, 0]},
        }
        mock_get_conn.return_value = mock_conn

        result = modify_object(ctx=None, id="line-1", scale=[5, 1, 1])

        assert result["geometry"] == {"start": [0, 0, 0], "end": [5, 0, 0]}
        assert result["bounding_box"] == [[0, 0, 0], [5, 0, 0]]

    @patch('rhinomcp.tools.modify_object.get_rhino_connection')
    def test_modify_omits_perception_fields_when_plugin_absent(self, mock_get_conn):
        """A rename carries no spatial change worth surfacing if the plugin
        doesn't include it; the response stays minimal with no null keys."""
        from rhinomcp.tools.modify_object import modify_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"id": "abc-123", "name": "NewName"}
        mock_get_conn.return_value = mock_conn

        result = modify_object(ctx=None, id="abc-123", new_name="NewName")

        assert "bounding_box" not in result
        assert "geometry" not in result
        assert result["name"] == "NewName"


class TestObjectAttributesTools:
    """Tests for object attribute read/update tools."""

    @patch('rhinomcp.tools.object_attributes.get_rhino_connection')
    def test_get_object_attributes_by_id(self, mock_get_conn):
        from rhinomcp.tools.object_attributes import get_object_attributes

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "Box1",
            "user_strings": {"PartNo": "A-100"}
        }
        mock_get_conn.return_value = mock_conn

        result = get_object_attributes(ctx=None, id="abc-123")

        mock_conn.send_command.assert_called_once_with("get_object_attributes", {"id": "abc-123"})
        assert result["user_strings"]["PartNo"] == "A-100"

    @patch('rhinomcp.tools.object_attributes.get_rhino_connection')
    def test_update_object_attributes(self, mock_get_conn):
        from rhinomcp.tools.object_attributes import update_object_attributes

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "Panel",
            "layer": {"name": "Parts"},
            "visible": True,
            "locked": False,
            "user_strings": {"PartNo": "A-100", "Count": "3"}
        }
        mock_get_conn.return_value = mock_conn

        result = update_object_attributes(
            ctx=None,
            id="abc-123",
            new_name="Panel",
            layer="Parts",
            color=[10, 20, 30],
            user_strings={"PartNo": "A-100", "Count": 3},
            delete_user_strings=["OldKey"],
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "update_object_attributes"
        assert call_args[0][1]["id"] == "abc-123"
        assert call_args[0][1]["new_name"] == "Panel"
        assert call_args[0][1]["layer"] == "Parts"
        assert call_args[0][1]["color"] == [10, 20, 30]
        assert call_args[0][1]["user_strings"]["Count"] == 3
        assert call_args[0][1]["delete_user_strings"] == ["OldKey"]
        assert result["user_strings"]["Count"] == "3"

    @patch('rhinomcp.tools.object_attributes.get_rhino_connection')
    def test_update_object_attributes_rejects_noop(self, mock_get_conn):
        from rhinomcp.tools.object_attributes import update_object_attributes

        mock_conn = MagicMock()
        mock_get_conn.return_value = mock_conn

        with pytest.raises(ValueError, match="at least one attribute update"):
            update_object_attributes(ctx=None, id="abc-123")

        mock_conn.send_command.assert_not_called()

    @patch('rhinomcp.tools.object_attributes.get_rhino_connection')
    def test_update_object_attributes_rejects_nested_user_strings(self, mock_get_conn):
        from rhinomcp.tools.object_attributes import update_object_attributes

        mock_conn = MagicMock()
        mock_get_conn.return_value = mock_conn

        with pytest.raises(ValueError, match="User string values"):
            update_object_attributes(ctx=None, id="abc-123", user_strings={"nested": {"bad": True}})

        mock_conn.send_command.assert_not_called()


class TestAnalyzeObjectsTool:
    """Tests for analyze_objects tool."""

    @patch('rhinomcp.tools.analyze_objects.get_rhino_connection')
    def test_analyze_object_by_id(self, mock_get_conn):
        from rhinomcp.tools.analyze_objects import analyze_objects

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "object_count": 1,
            "analyses": [
                {
                    "id": "abc-123",
                    "name": "Line1",
                    "type": "LINE",
                    "valid": True,
                    "metrics": {"length": 10},
                }
            ],
        }
        mock_get_conn.return_value = mock_conn

        result = analyze_objects(ctx=None, id="abc-123")

        mock_conn.send_command.assert_called_once_with("analyze_objects", {"id": "abc-123"})
        assert result["object_count"] == 1
        assert result["analyses"][0]["metrics"]["length"] == 10

    @patch('rhinomcp.tools.analyze_objects.get_rhino_connection')
    def test_analyze_objects_by_ids(self, mock_get_conn):
        from rhinomcp.tools.analyze_objects import analyze_objects

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"object_count": 2, "analyses": []}
        mock_get_conn.return_value = mock_conn

        analyze_objects(ctx=None, object_ids=["id-1", "id-2"])

        mock_conn.send_command.assert_called_once_with("analyze_objects", {"object_ids": ["id-1", "id-2"]})

    @patch('rhinomcp.tools.analyze_objects.get_rhino_connection')
    def test_analyze_objects_rejects_mixed_selectors(self, mock_get_conn):
        from rhinomcp.tools.analyze_objects import analyze_objects

        mock_conn = MagicMock()
        mock_get_conn.return_value = mock_conn

        with pytest.raises(ValueError, match="exactly one"):
            analyze_objects(ctx=None, id="abc-123", selected=True)

        mock_conn.send_command.assert_not_called()

    @patch('rhinomcp.tools.analyze_objects.get_rhino_connection')
    def test_analyze_objects_rejects_empty_object_ids(self, mock_get_conn):
        from rhinomcp.tools.analyze_objects import analyze_objects

        mock_conn = MagicMock()
        mock_get_conn.return_value = mock_conn

        with pytest.raises(ValueError, match="at least one id"):
            analyze_objects(ctx=None, object_ids=[])

        mock_conn.send_command.assert_not_called()


class TestModifyObjectsTool:
    """Tests for modify_objects tool."""

    @patch('rhinomcp.tools.modify_objects.get_rhino_connection')
    def test_modify_multiple(self, mock_get_conn):
        from rhinomcp.tools.modify_objects import modify_objects

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success_count": 2,
            "failure_count": 0,
            "total": 2,
            "errors": []
        }
        mock_get_conn.return_value = mock_conn

        result = modify_objects(
            ctx=None,
            objects=[
                {"id": "id1", "new_name": "Name1"},
                {"id": "id2", "new_name": "Name2"}
            ]
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "modify_objects"
        assert "2" in result


class TestDeleteObjectTool:
    """Tests for delete_object tool."""

    @patch('rhinomcp.tools.delete_object.get_rhino_connection')
    def test_delete_by_id(self, mock_get_conn):
        from rhinomcp.tools.delete_object import delete_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "DeletedObject",
            "deleted": True
        }
        mock_get_conn.return_value = mock_conn

        result = delete_object(ctx=None, id="abc-123")

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "delete_object"
        assert call_args[0][1]["id"] == "abc-123"

    @patch('rhinomcp.tools.delete_object.get_rhino_connection')
    def test_delete_all(self, mock_get_conn):
        from rhinomcp.tools.delete_object import delete_object

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": True,
            "count": 5,
            "scope": "all"
        }
        mock_get_conn.return_value = mock_conn

        result = delete_object(ctx=None, all=True)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1].get("all") is True
        # Wrapper must not crash on the all=true response (no "name" key) and must report count.
        assert result["success"] is True
        assert result["count"] == 5
        assert result["scope"] == "all"

    def test_delete_no_selector_raises(self):
        from rhinomcp.tools.delete_object import delete_object

        with pytest.raises(ValueError, match="must specify"):
            delete_object(ctx=None)

    def test_delete_mixed_selector_raises(self):
        """Mixed selectors (e.g. id + all=True) must be rejected before dispatch —
        otherwise C# prioritizes all and silently wipes the document."""
        from rhinomcp.tools.delete_object import delete_object

        with pytest.raises(ValueError, match="exactly one"):
            delete_object(ctx=None, id="abc-123", all=True)


class TestGetObjectInfoTool:
    """Tests for get_object_info tool."""

    @patch('rhinomcp.tools.get_object_info.get_rhino_connection')
    def test_get_by_id(self, mock_get_conn):
        from rhinomcp.tools.get_object_info import get_object_info

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "abc-123",
            "name": "TestObject",
            "type": "BOX"
        }
        mock_get_conn.return_value = mock_conn

        result = get_object_info(ctx=None, id="abc-123")

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "get_object_info"
        assert call_args[0][1]["id"] == "abc-123"


class TestGetDocumentSummaryTool:
    """Tests for get_document_summary tool."""

    @patch('rhinomcp.tools.get_document_summary.get_rhino_connection')
    def test_get_document_summary(self, mock_get_conn):
        from rhinomcp.tools.get_document_summary import get_document_summary

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "meta_data": {"name": "test.3dm", "units": "Millimeters"},
            "object_count": 10,
            "objects_by_type": {"CURVE": 5, "BREP": 3, "POINT": 2},
            "objects_by_layer": {"Default": 10},
            "layer_count": 1,
            "layer_hierarchy": []
        }
        mock_get_conn.return_value = mock_conn

        result = get_document_summary(ctx=None)

        mock_conn.send_command.assert_called_once_with("get_document_summary")


class TestGetSelectedObjectsInfoTool:
    """Tests for get_selected_objects_info tool."""

    @patch('rhinomcp.tools.get_selected_objects_info.get_rhino_connection')
    def test_get_selected_objects_info(self, mock_get_conn):
        from rhinomcp.tools.get_selected_objects_info import get_selected_objects_info

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "selected_objects": [
                {"id": "1", "name": "Selected1"},
                {"id": "2", "name": "Selected2"}
            ]
        }
        mock_get_conn.return_value = mock_conn

        result = get_selected_objects_info(ctx=None, include_attributes=True)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "get_selected_objects_info"


class TestSelectObjectsTool:
    """Tests for select_objects tool."""

    @patch('rhinomcp.tools.select_objects.get_rhino_connection')
    def test_select_by_name(self, mock_get_conn):
        from rhinomcp.tools.select_objects import select_objects

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"count": 3}
        mock_get_conn.return_value = mock_conn

        result = select_objects(
            ctx=None,
            filters={"name": ["TestObject"]},
            filters_type="or"
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "select_objects"
        assert call_args[0][1]["filters"]["name"] == ["TestObject"]


class TestCreateLayerTool:
    """Tests for create_layer tool."""

    @patch('rhinomcp.tools.create_layer.get_rhino_connection')
    def test_create_layer(self, mock_get_conn):
        from rhinomcp.tools.create_layer import create_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "layer-123",
            "name": "NewLayer",
            "color": {"r": 255, "g": 0, "b": 0}
        }
        mock_get_conn.return_value = mock_conn

        result = create_layer(
            ctx=None,
            name="NewLayer",
            color=[255, 0, 0]
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "create_layer"
        assert call_args[0][1]["name"] == "NewLayer"
        assert call_args[0][1]["color"] == [255, 0, 0]


class TestDeleteLayerTool:
    """Tests for delete_layer tool."""

    @patch('rhinomcp.tools.delete_layer.get_rhino_connection')
    def test_delete_layer_by_name(self, mock_get_conn):
        from rhinomcp.tools.delete_layer import delete_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "name": "DeletedLayer",
            "deleted": True
        }
        mock_get_conn.return_value = mock_conn

        result = delete_layer(ctx=None, name="DeletedLayer")

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "delete_layer"
        assert call_args[0][1]["name"] == "DeletedLayer"


class TestGetOrSetCurrentLayerTool:
    """Tests for get_or_set_current_layer tool."""

    @patch('rhinomcp.tools.get_or_set_current_layer.get_rhino_connection')
    def test_set_current_layer(self, mock_get_conn):
        from rhinomcp.tools.get_or_set_current_layer import get_or_set_current_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "layer-123",
            "name": "MyLayer"
        }
        mock_get_conn.return_value = mock_conn

        result = get_or_set_current_layer(ctx=None, name="MyLayer")

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "get_or_set_current_layer"
        assert call_args[0][1]["name"] == "MyLayer"

    @patch('rhinomcp.tools.get_or_set_current_layer.get_rhino_connection')
    def test_get_current_layer(self, mock_get_conn):
        from rhinomcp.tools.get_or_set_current_layer import get_or_set_current_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "id": "layer-123",
            "name": "CurrentLayer"
        }
        mock_get_conn.return_value = mock_conn

        result = get_or_set_current_layer(ctx=None)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "get_or_set_current_layer"


class TestBooleanOperationsTools:
    """Tests for boolean operation tools."""

    @patch('rhinomcp.tools.boolean_operations.get_rhino_connection')
    def test_boolean_union(self, mock_get_conn):
        from rhinomcp.tools.boolean_operations import boolean_union

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["result-123"],
            "count": 1,
            "message": "Boolean union created 1 object(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = boolean_union(
            ctx=None,
            object_ids=["id1", "id2"],
            delete_sources=True,
            name="UnionResult"
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "boolean_union"
        assert call_args[0][1]["object_ids"] == ["id1", "id2"]
        assert call_args[0][1]["delete_sources"] is True
        assert call_args[0][1]["name"] == "UnionResult"
        assert "Boolean union created" in result

    @patch('rhinomcp.tools.boolean_operations.get_rhino_connection')
    def test_boolean_difference(self, mock_get_conn):
        from rhinomcp.tools.boolean_operations import boolean_difference

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["result-456"],
            "count": 1,
            "message": "Boolean difference created 1 object(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = boolean_difference(
            ctx=None,
            base_id="base-id",
            subtract_ids=["sub1", "sub2"],
            delete_sources=False,
            name="DiffResult"
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "boolean_difference"
        assert call_args[0][1]["base_id"] == "base-id"
        assert call_args[0][1]["subtract_ids"] == ["sub1", "sub2"]
        assert call_args[0][1]["delete_sources"] is False
        assert "Boolean difference created" in result

    @patch('rhinomcp.tools.boolean_operations.get_rhino_connection')
    def test_boolean_intersection(self, mock_get_conn):
        from rhinomcp.tools.boolean_operations import boolean_intersection

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["result-789"],
            "count": 1,
            "message": "Boolean intersection created 1 object(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = boolean_intersection(
            ctx=None,
            object_ids=["id1", "id2", "id3"],
            delete_sources=True
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "boolean_intersection"
        assert call_args[0][1]["object_ids"] == ["id1", "id2", "id3"]
        assert "Boolean intersection created" in result


class TestUndoRedoTools:
    """Tests for undo and redo tools."""

    @patch('rhinomcp.tools.undo.get_rhino_connection')
    def test_undo_single(self, mock_get_conn):
        from rhinomcp.tools.undo import undo

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "undone_steps": 1,
            "requested_steps": 1,
            "message": "Undid 1 operation(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = undo(ctx=None, steps=1)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "undo"
        assert call_args[0][1]["steps"] == 1
        assert "Undid 1" in result

    @patch('rhinomcp.tools.undo.get_rhino_connection')
    def test_undo_multiple(self, mock_get_conn):
        from rhinomcp.tools.undo import undo

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "undone_steps": 3,
            "requested_steps": 3,
            "message": "Undid 3 operation(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = undo(ctx=None, steps=3)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["steps"] == 3

    @patch('rhinomcp.tools.undo.get_rhino_connection')
    def test_redo(self, mock_get_conn):
        from rhinomcp.tools.undo import redo

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "redone_steps": 1,
            "requested_steps": 1,
            "message": "Redid 1 operation(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = redo(ctx=None, steps=1)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "redo"
        assert call_args[0][1]["steps"] == 1
        assert "Redid 1" in result


class TestExecuteRhinoscriptTool:
    """Tests for execute_rhinoscript_python_code tool."""

    @patch('rhinomcp.tools.execute_rhinoscript_python_code.get_rhino_connection')
    def test_execute_script(self, mock_get_conn):
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": True,
            "result": "Script executed successfully"
        }
        mock_get_conn.return_value = mock_conn

        result = execute_rhinoscript_python_code(
            ctx=None,
            code="print('Hello')"
        )

        call_args = mock_conn.send_command.call_args
        # The command is "execute_rhinoscript_python_code"
        assert call_args[0][0] == "execute_rhinoscript_python_code"
        assert call_args[0][1]["code"] == "print('Hello')"

    @patch('rhinomcp.tools.execute_rhinoscript_python_code.get_rhino_connection')
    def test_execute_script_error(self, mock_get_conn):
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": False,
            "message": "Syntax error"
        }
        mock_get_conn.return_value = mock_conn

        result = execute_rhinoscript_python_code(
            ctx=None,
            code="invalid python code"
        )

        # Result is a Dict with the command response
        assert isinstance(result, dict)
        assert result.get("success") is False

    @patch('rhinomcp.tools.execute_rhinoscript_python_code.get_rhino_connection')
    def test_execute_script_success_returns_output(self, mock_get_conn):
        """Successful execution surfaces captured output (Python print + Rhino lines)."""
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": True,
            "output": "Hello from print()\nRhino: Box created."
        }
        mock_get_conn.return_value = mock_conn

        result = execute_rhinoscript_python_code(ctx=None, code="print('Hello from print()')")
        assert result["success"] is True
        assert "Hello from print()" in result["output"]
        assert "Rhino: Box created." in result["output"]

    @patch('rhinomcp.tools.execute_rhinoscript_python_code.get_rhino_connection')
    def test_execute_script_failure_preserves_partial_output(self, mock_get_conn):
        """When the script raises, captured output up to the exception is preserved."""
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": False,
            "output": "Step 1 done\nStep 2 done\n",
            "message": "ZeroDivisionError: division by zero"
        }
        mock_get_conn.return_value = mock_conn

        result = execute_rhinoscript_python_code(ctx=None, code="raise ZeroDivisionError()")
        assert result["success"] is False
        # Partial output must survive — that's the whole point of the slice.
        assert "Step 1 done" in result["output"]
        assert "Step 2 done" in result["output"]
        assert "ZeroDivisionError" in result["message"]

    @patch('rhinomcp.tools.execute_rhinoscript_python_code.get_rhino_connection')
    def test_execute_script_failure_without_exception_has_message(self, mock_get_conn):
        """ExecuteScript may return false without throwing; a message must still be set."""
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": False,
            "output": "",
            "message": "Script execution returned false (no exception raised)."
        }
        mock_get_conn.return_value = mock_conn

        result = execute_rhinoscript_python_code(ctx=None, code="# triggers ExecuteScript false")
        assert result["success"] is False
        assert result.get("message"), "failure response must include a non-empty message"
        assert "returned false" in result["message"]


class TestSearchRhinoscriptFunctionsTool:
    """Tests for search_rhinoscript_functions tool."""

    def test_search_functions(self):
        from rhinomcp.tools.rhinoscript_docs import search_rhinoscript_functions

        # This tool doesn't use the connection, it reads static data
        result = search_rhinoscript_functions(ctx=None, query="loft surface")

        # Should return a list of matching functions
        assert isinstance(result, list)
        assert len(result) > 0
        # Each result should have name, signature, description, module
        assert "name" in result[0]
        assert "signature" in result[0]

    def test_search_functions_with_limit(self):
        from rhinomcp.tools.rhinoscript_docs import search_rhinoscript_functions

        result = search_rhinoscript_functions(ctx=None, query="curve", limit=3)

        assert isinstance(result, list)
        assert len(result) <= 3


class TestGetRhinoscriptDocsTool:
    """Tests for get_rhinoscript_docs tool."""

    def test_get_docs(self):
        from rhinomcp.tools.rhinoscript_docs import get_rhinoscript_docs

        # This tool reads static data
        result = get_rhinoscript_docs(
            ctx=None,
            topic="add point"
        )

        # Should return a Dict with documentation
        assert isinstance(result, dict)
        assert result.get("success") is True
        assert "documentation" in result
        assert len(result["documentation"]) > 0

    def test_get_docs_not_found(self):
        from rhinomcp.tools.rhinoscript_docs import get_rhinoscript_docs

        result = get_rhinoscript_docs(
            ctx=None,
            topic="xyznonexistentfunction123"
        )

        # Should return a Dict with success=False
        assert isinstance(result, dict)
        assert result.get("success") is False


class TestListRhinoscriptModulesTool:
    """Tests for list_rhinoscript_modules tool."""

    def test_list_modules(self):
        from rhinomcp.tools.rhinoscript_docs import list_rhinoscript_modules

        result = list_rhinoscript_modules(ctx=None)

        assert isinstance(result, dict)
        assert "modules" in result
        assert "total_modules" in result
        assert result["total_modules"] > 0


class TestGetModuleFunctionsTool:
    """Tests for get_module_functions tool."""

    def test_get_module_functions(self):
        from rhinomcp.tools.rhinoscript_docs import get_module_functions

        result = get_module_functions(ctx=None, module_name="curve")

        assert isinstance(result, dict)
        assert "functions" in result
        assert len(result["functions"]) > 0

    def test_get_module_functions_not_found(self):
        from rhinomcp.tools.rhinoscript_docs import get_module_functions

        result = get_module_functions(ctx=None, module_name="nonexistentmodule")

        assert isinstance(result, dict)
        assert "error" in result


# =============================================================================
# Advanced Geometry Tools Tests
# =============================================================================

class TestLoftTool:
    """Tests for loft tool."""

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_loft_success(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import loft

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["guid-1", "guid-2"],
            "count": 2,
            "message": "Loft created 2 surface(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = loft(
            ctx=None,
            curve_ids=["curve-1", "curve-2", "curve-3"],
            name="test_loft",
            closed=False,
            loft_type=0
        )

        assert result["success"] is True
        assert "result_ids" in result
        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "loft"
        assert call_args[0][1]["curve_ids"] == ["curve-1", "curve-2", "curve-3"]

    def test_loft_insufficient_curves(self):
        from rhinomcp.tools.advanced_geometry import loft

        result = loft(ctx=None, curve_ids=["only-one"])

        assert result["success"] is False
        assert "at least 2 curves" in result["message"]


class TestExtrudeCurveTool:
    """Tests for extrude_curve tool."""

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_extrude_curve_success(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import extrude_curve

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_id": "extruded-guid",
            "message": "Extrusion created successfully"
        }
        mock_get_conn.return_value = mock_conn

        result = extrude_curve(
            ctx=None,
            curve_id="curve-guid",
            direction=[0, 0, 10],
            name="test_extrusion",
            cap=True
        )

        assert result["success"] is True
        assert result["result_id"] == "extruded-guid"
        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "extrude_curve"
        assert call_args[0][1]["direction"] == [0, 0, 10]

    def test_extrude_curve_invalid_direction(self):
        from rhinomcp.tools.advanced_geometry import extrude_curve

        result = extrude_curve(ctx=None, curve_id="curve-guid", direction=[0, 0])

        assert result["success"] is False
        assert "direction" in result["message"].lower()


class TestSweep1Tool:
    """Tests for sweep1 tool."""

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_sweep1_success(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import sweep1

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["swept-guid"],
            "count": 1,
            "message": "Sweep created 1 surface(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = sweep1(
            ctx=None,
            rail_id="rail-guid",
            profile_ids=["profile-1", "profile-2"],
            name="test_sweep"
        )

        assert result["success"] is True
        assert "result_ids" in result
        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "sweep1"
        assert call_args[0][1]["rail_id"] == "rail-guid"

    def test_sweep1_no_profiles(self):
        from rhinomcp.tools.advanced_geometry import sweep1

        result = sweep1(ctx=None, rail_id="rail-guid", profile_ids=[])

        assert result["success"] is False
        assert "profile" in result["message"].lower()


class TestOffsetCurveTool:
    """Tests for offset_curve tool."""

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_offset_curve_success(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import offset_curve

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["offset-guid"],
            "count": 1,
            "message": "Offset created 1 curve(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = offset_curve(
            ctx=None,
            curve_id="curve-guid",
            distance=2.5,
            name="test_offset"
        )

        assert result["success"] is True
        assert "result_ids" in result
        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "offset_curve"
        assert call_args[0][1]["distance"] == 2.5

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_offset_curve_with_plane(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import offset_curve

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["offset-guid"],
            "count": 1,
            "message": "Offset created 1 curve(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = offset_curve(
            ctx=None,
            curve_id="curve-guid",
            distance=1.0,
            plane=[0, 0, 1]
        )

        assert result["success"] is True
        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["plane"] == [0, 0, 1]


class TestPipeTool:
    """Tests for pipe tool."""

    @patch('rhinomcp.tools.advanced_geometry.get_rhino_connection')
    def test_pipe_success(self, mock_get_conn):
        from rhinomcp.tools.advanced_geometry import pipe

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "result_ids": ["pipe-guid"],
            "count": 1,
            "message": "Pipe created 1 object(s)"
        }
        mock_get_conn.return_value = mock_conn

        result = pipe(
            ctx=None,
            curve_id="curve-guid",
            radius=0.5,
            name="test_pipe",
            cap=True
        )

        assert result["success"] is True
        assert "result_ids" in result
        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "pipe"
        assert call_args[0][1]["radius"] == 0.5

    def test_pipe_invalid_radius(self):
        from rhinomcp.tools.advanced_geometry import pipe

        result = pipe(ctx=None, curve_id="curve-guid", radius=0)

        assert result["success"] is False
        assert "radius" in result["message"].lower()

    def test_pipe_negative_radius(self):
        from rhinomcp.tools.advanced_geometry import pipe

        result = pipe(ctx=None, curve_id="curve-guid", radius=-1.0)

        assert result["success"] is False
        assert "radius" in result["message"].lower()


class TestRunCommandTool:
    """Tests for run_command tool (Rhino command escape hatch)."""

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_returns_output(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": True,
            "command": "_Box 0,0,0 10,10,10",
            "output": "Box command completed."
        }
        mock_get_conn.return_value = mock_conn

        result = run_command(ctx=None, command="_Box 0,0,0 10,10,10")

        mock_conn.send_command.assert_called_once()
        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "run_command"
        assert call_args[0][1]["command"] == "_Box 0,0,0 10,10,10"
        assert call_args[0][1]["echo"] is False
        assert "Box command completed" in result

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_passes_echo_flag(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True, "command": "_Box", "output": ""}
        mock_get_conn.return_value = mock_conn

        run_command(ctx=None, command="_Box", echo=True)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["echo"] is True

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_empty_output_returns_done(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True, "command": "_NoOp", "output": ""}
        mock_get_conn.return_value = mock_conn

        result = run_command(ctx=None, command="_NoOp")
        assert result == "Done."

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_handles_connection_error(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_get_conn.side_effect = Exception("Connection refused")

        result = run_command(ctx=None, command="_Box")
        assert "Error running Rhino command" in result
        assert "Connection refused" in result

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_failure_is_flagged(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": False,
            "command": "_NotARealCommand",
            "output": "Unknown command: _NotARealCommand"
        }
        mock_get_conn.return_value = mock_conn

        result = run_command(ctx=None, command="_NotARealCommand")
        assert result.startswith("Command failed:")
        assert "Unknown command" in result

    @patch('rhinomcp.tools.run_command.get_rhino_connection')
    def test_run_command_failure_no_output(self, mock_get_conn):
        from rhinomcp.tools.run_command import run_command

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "success": False,
            "command": "_X",
            "output": ""
        }
        mock_get_conn.return_value = mock_conn

        result = run_command(ctx=None, command="_X")
        assert result.startswith("Command failed:")


class TestGetCommandsTool:
    """Tests for get_commands tool (Rhino command discovery)."""

    @patch('rhinomcp.tools.get_commands.get_rhino_connection')
    def test_get_commands_no_filter(self, mock_get_conn):
        from rhinomcp.tools.get_commands import get_commands

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "count": 3,
            "commands": ["Box", "Circle", "Sphere"]
        }
        mock_get_conn.return_value = mock_conn

        result = get_commands(ctx=None)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "get_commands"
        assert call_args[0][1]["filter"] == ""
        assert call_args[0][1]["loaded_only"] is True
        assert "Box" in result
        assert "Sphere" in result

    @patch('rhinomcp.tools.get_commands.get_rhino_connection')
    def test_get_commands_with_filter(self, mock_get_conn):
        from rhinomcp.tools.get_commands import get_commands

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "count": 2,
            "commands": ["BooleanDifference", "BooleanUnion"]
        }
        mock_get_conn.return_value = mock_conn

        get_commands(ctx=None, filter="boolean", loaded_only=False)

        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["filter"] == "boolean"
        assert call_args[0][1]["loaded_only"] is False


class TestExecutionSafetyGates:
    """The arbitrary-code execution tools must refuse calls when their
    env flag is off, before reaching Rhino."""

    @patch.dict("os.environ", {"RHINO_MCP_ENABLE_RUN_COMMAND": "0"})
    def test_run_command_disabled(self):
        from rhinomcp.tools.run_command import run_command
        result = run_command(ctx=None, command="_Box")
        assert "disabled" in result.lower()
        assert "RHINO_MCP_ENABLE_RUN_COMMAND" in result

    @patch.dict("os.environ", {"RHINO_MCP_ENABLE_RHINOSCRIPT": "0"})
    def test_rhinoscript_disabled(self):
        from rhinomcp.tools.execute_rhinoscript_python_code import execute_rhinoscript_python_code
        result = execute_rhinoscript_python_code(ctx=None, code="print('x')")
        assert result["success"] is False
        assert "disabled" in result["message"].lower()
        assert "RHINO_MCP_ENABLE_RHINOSCRIPT" in result["message"]

    @patch.dict("os.environ", {"RHINO_MCP_ENABLE_CSHARP": "0"})
    def test_csharp_disabled(self):
        from rhinomcp.tools.execute_rhinocommon_csharp_code import execute_rhinocommon_csharp_code
        result = execute_rhinocommon_csharp_code(ctx=None, code="// noop")
        assert result["success"] is False
        assert "disabled" in result["message"].lower()
        assert "RHINO_MCP_ENABLE_CSHARP" in result["message"]


class TestGrasshopperTools:
    """Tests for Grasshopper MCP wrappers."""

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_document_setup_tool(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_document import gh_create_document

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_create_document(ctx=None, new_if_missing=True, make_active=False, open_canvas=True)

        mock_conn.send_command.assert_called_once_with(
            "gh_create_document",
            {
                "new_if_missing": True,
                "make_active": False,
                "open_canvas": True,
            },
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_readonly_discovery_tools(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_catalog import (
            gh_batch_get_component_type_info,
            gh_batch_search_components,
            gh_get_available_components,
            gh_get_component_type_info,
            gh_list_component_categories,
            gh_search_components,
        )
        from rhinomcp.tools.grasshopper_components import gh_get_component_info, gh_list_components
        from rhinomcp.tools.grasshopper_document import gh_get_canvas_state, gh_get_document_info
        from rhinomcp.tools.grasshopper_graph import gh_get_graph

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_get_document_info(ctx=None)
        gh_search_components(ctx=None, query="add", category="Maths", limit=5)
        gh_batch_search_components(ctx=None, queries=["Circle", "Panel"], max_matches=8)
        gh_list_component_categories(ctx=None)
        gh_get_available_components(ctx=None, category="Curve", include_description=True, limit=25)
        gh_get_component_type_info(ctx=None, name="Circle")
        gh_batch_get_component_type_info(
            ctx=None,
            components=[{"name": "Circle"}, {"guid": "12345678-1234-1234-1234-123456789012"}],
        )
        gh_list_components(ctx=None, category="Curve", name="Circle", limit=10)
        gh_get_component_info(ctx=None, instance_id="abc")
        gh_get_canvas_state(ctx=None, include_connections=False, include_values=True, max_items=3)
        gh_get_graph(ctx=None, graph_id="TestGraph", include_values=True, max_items=5)

        calls = mock_conn.send_command.call_args_list
        assert calls[0][0] == ("gh_get_document_info", {})
        assert calls[1][0] == ("gh_search_components", {"limit": 5, "query": "add", "category": "Maths"})
        assert calls[2][0] == (
            "gh_batch_search_components",
            {"queries": ["Circle", "Panel"], "max_matches": 8},
        )
        assert calls[3][0] == ("gh_list_component_categories", {})
        assert calls[4][0] == (
            "gh_get_available_components",
            {"include_description": True, "limit": 25, "category": "Curve"},
        )
        assert calls[5][0] == ("gh_get_component_type_info", {"name": "Circle"})
        assert calls[6][0] == (
            "gh_batch_get_component_type_info",
            {"components": [{"name": "Circle"}, {"guid": "12345678-1234-1234-1234-123456789012"}]},
        )
        assert calls[7][0] == ("gh_list_components", {"limit": 10, "category": "Curve", "name": "Circle"})
        assert calls[8][0] == ("gh_get_component_info", {"instance_id": "abc"})
        assert calls[9][0] == (
            "gh_get_canvas_state",
            {"include_connections": False, "include_values": True, "max_items": 3},
        )
        assert calls[10][0] == (
            "gh_get_graph",
            {"graph_id": "TestGraph", "include_values": True, "max_items": 5},
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_solution_tools(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_solution import gh_expire_solution, gh_run_solution

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_run_solution(ctx=None, expire_all=True)
        gh_expire_solution(
            ctx=None,
            nickname="Circle",
            component_ids=["abc"],
            expire_downstream=False,
            recompute=True,
        )

        calls = mock_conn.send_command.call_args_list
        assert calls[0][0] == ("gh_run_solution", {"expire_all": True})
        assert calls[1][0] == (
            "gh_expire_solution",
            {
                "expire_downstream": False,
                "recompute": True,
                "nickname": "Circle",
                "component_ids": ["abc"],
            },
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_capture_preview_tool(self, mock_get_conn):
        import base64

        from rhinomcp.tools.grasshopper_preview import gh_capture_preview

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "image_data": base64.b64encode(b"preview-png").decode("ascii"),
            "width": 320,
            "height": 240,
            "viewport_name": "Perspective",
            "captured_preview_object_count": 2,
        }
        mock_get_conn.return_value = mock_conn

        image = gh_capture_preview(
            ctx=None,
            viewport="top",
            width=320,
            height=240,
            show_grid=False,
            show_axes=False,
            graph_id="TestGraph",
            targets=["output"],
            include_hidden=True,
            recompute=False,
            open_canvas=True,
            padding_factor=1.25,
        )

        mock_conn.send_command.assert_called_once_with(
            "gh_capture_preview",
            {
                "viewport": "top",
                "width": 320,
                "height": 240,
                "show_grid": False,
                "show_axes": False,
                "show_cplane_axes": False,
                "include_hidden": True,
                "recompute": False,
                "open_canvas": True,
                "padding_factor": 1.25,
                "graph_id": "TestGraph",
                "targets": ["output"],
            },
        )
        assert image.data == b"preview-png"

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_build_graph_tool(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_build import gh_build_graph

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_build_graph(
            ctx=None,
            components=[
                {
                    "alias": "slider_a",
                    "component_name": "Number Slider",
                    "nickname": "A",
                    "value": 3.5,
                    "min": 0,
                    "max": 10,
                    "decimals": 2,
                },
                {"alias": "add", "component_name": "Addition", "nickname": "Add"},
            ],
            connections=[
                {
                    "source": "slider_a",
                    "target": "add",
                    "target_input_index": 0,
                }
            ],
            values=[
                {
                    "target": "slider_a",
                    "value": 4.0,
                    "decimals": 1,
                }
            ],
            preview_updates={"enabled": False},
            layout={"enabled": True, "start_position": [40, 40], "max_columns": 6},
            recompute=False,
            rollback_on_error=True,
        )

        mock_conn.send_command.assert_called_once_with(
            "gh_build_graph",
            {
                "components": [
                    {
                        "alias": "slider_a",
                        "component_name": "Number Slider",
                        "nickname": "A",
                        "value": 3.5,
                        "min": 0,
                        "max": 10,
                        "decimals": 2,
                    },
                    {"alias": "add", "component_name": "Addition", "nickname": "Add"},
                ],
                "connections": [
                    {
                        "source": "slider_a",
                        "target": "add",
                        "target_input_index": 0,
                    }
                ],
                "values": [
                    {
                        "target": "slider_a",
                        "value": 4.0,
                        "decimals": 1,
                    }
                ],
                "preview_updates": {"enabled": False},
                "layout": {"enabled": True, "start_position": [40, 40], "max_columns": 6},
                "recompute": False,
                "rollback_on_error": True,
                "open_canvas": True,
            },
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_mutate_graph_tool(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_mutation import gh_mutate_graph

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_mutate_graph(
            ctx=None,
            graph_id="PointAttractor_20260604",
            operations=[
                {
                    "op": "create",
                    "alias": "height",
                    "component_name": "Number Slider",
                    "value": 8,
                    "min": 0,
                    "max": 20,
                    "role": "control",
                },
                {
                    "op": "update",
                    "target": "cylinder",
                    "preview": False,
                },
                {
                    "op": "connect",
                    "source": "height",
                    "target": "cap",
                    "target_input_index": 0,
                },
            ],
            preview_policy={"mode": "only", "targets": ["cap"]},
            groups=[{"name": "Output", "targets": ["cap"], "color": [180, 220, 255]}],
            layout={"enabled": True, "targets": ["height", "cap"], "max_columns": 6},
            verify={
                "run_solution": True,
                "outputs": [
                    {
                        "target": "cap",
                        "output_index": 0,
                        "expect_count_min": 1,
                        "expect_type": "Brep",
                    }
                ],
            },
            fail_on_verification_error=True,
            recompute=True,
            rollback_on_error=True,
        )

        mock_conn.send_command.assert_called_once_with(
            "gh_mutate_graph",
            {
                "operations": [
                    {
                        "op": "create",
                        "alias": "height",
                        "component_name": "Number Slider",
                        "value": 8,
                        "min": 0,
                        "max": 20,
                        "role": "control",
                    },
                    {
                        "op": "update",
                        "target": "cylinder",
                        "preview": False,
                    },
                    {
                        "op": "connect",
                        "source": "height",
                        "target": "cap",
                        "target_input_index": 0,
                    },
                ],
                "fail_on_verification_error": True,
                "recompute": True,
                "rollback_on_error": True,
                "open_canvas": True,
                "graph_id": "PointAttractor_20260604",
                "preview_policy": {"mode": "only", "targets": ["cap"]},
                "groups": [{"name": "Output", "targets": ["cap"], "color": [180, 220, 255]}],
                "layout": {"enabled": True, "targets": ["height", "cap"], "max_columns": 6},
                "verify": {
                    "run_solution": True,
                    "outputs": [
                        {
                            "target": "cap",
                            "output_index": 0,
                            "expect_count_min": 1,
                            "expect_type": "Brep",
                        }
                    ],
                },
            },
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_component_lifecycle_tools(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_components import (
            gh_add_component,
            gh_clear_canvas,
            gh_delete_component,
            gh_layout_components,
            gh_update_component,
        )
        from rhinomcp.tools.grasshopper_graph import gh_clear_graph

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_add_component(
            ctx=None,
            component_name="Number Slider",
            position=[10, 20],
            nickname="Radius",
            value=5,
            min=0,
            max=10,
            decimals=1,
        )
        gh_add_component(ctx=None, component_guid="12345678-1234-1234-1234-123456789012")
        gh_add_component(ctx=None, component_name="Panel", nickname="AutoPlaced")
        gh_update_component(
            ctx=None,
            instance_id="abc",
            new_nickname="Radius2",
            position=[30, 40],
            enabled=False,
            preview=False,
        )
        gh_layout_components(
            ctx=None,
            component_ids=["abc", "def"],
            start_position=[20, 30],
            x_spacing=200,
            y_spacing=75,
            recompute=True,
        )
        gh_delete_component(ctx=None, nickname="Radius2")
        gh_clear_canvas(ctx=None, include_groups=False, recompute=True)
        gh_clear_graph(ctx=None, graph_id="TestGraph", include_groups=False, recompute=True)

        calls = mock_conn.send_command.call_args_list
        assert calls[0][0] == (
            "gh_add_component",
            {
                "position": [10, 20],
                "component_name": "Number Slider",
                "nickname": "Radius",
                "value": 5,
                "min": 0,
                "max": 10,
                "decimals": 1,
            },
        )
        assert calls[1][0] == (
            "gh_add_component",
            {
                "component_guid": "12345678-1234-1234-1234-123456789012",
            },
        )
        assert calls[2][0] == (
            "gh_add_component",
            {
                "component_name": "Panel",
                "nickname": "AutoPlaced",
            },
        )
        assert calls[3][0] == (
            "gh_update_component",
            {
                "instance_id": "abc",
                "new_nickname": "Radius2",
                "position": [30, 40],
                "enabled": False,
                "preview": False,
            },
        )
        assert calls[4][0] == (
            "gh_layout_components",
            {
                "include_groups": False,
                "x_spacing": 200,
                "y_spacing": 75,
                "recompute": True,
                "component_ids": ["abc", "def"],
                "start_position": [20, 30],
            },
        )
        assert calls[5][0] == ("gh_delete_component", {"nickname": "Radius2"})
        assert calls[6][0] == ("gh_clear_canvas", {"include_groups": False, "recompute": True})
        assert calls[7][0] == (
            "gh_clear_graph",
            {"graph_id": "TestGraph", "include_groups": False, "recompute": True},
        )

    @patch("rhinomcp.tools._grasshopper_common.get_rhino_connection")
    def test_gh_connection_and_parameter_tools(self, mock_get_conn):
        from rhinomcp.tools.grasshopper_connections import (
            gh_connect_components,
            gh_disconnect_components,
        )
        from rhinomcp.tools.grasshopper_parameters import (
            gh_get_parameter_value,
            gh_set_parameter_value,
        )

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {"success": True}
        mock_get_conn.return_value = mock_conn

        gh_connect_components(
            ctx=None,
            source_instance_id="src",
            source_output_index=0,
            target_nickname="Circle",
            target_input_name="Radius",
        )
        gh_disconnect_components(ctx=None, target_instance_id="dst", disconnect_all=True)
        gh_set_parameter_value(
            ctx=None,
            nickname="Radius",
            value=7.5,
            input_name="R",
            min=0,
            max=10,
            decimals=2,
        )
        gh_get_parameter_value(ctx=None, instance_id="circle-id", output_name="C", max_items=12)

        calls = mock_conn.send_command.call_args_list
        assert calls[0][0] == (
            "gh_connect_components",
            {
                "source_instance_id": "src",
                "source_output_index": 0,
                "target_nickname": "Circle",
                "target_input_name": "Radius",
            },
        )
        assert calls[1][0] == (
            "gh_disconnect_components",
            {"disconnect_all": True, "target_instance_id": "dst"},
        )
        assert calls[2][0] == (
            "gh_set_parameter_value",
            {
                "value": 7.5,
                "input_index": 0,
                "nickname": "Radius",
                "input_name": "R",
                "min": 0,
                "max": 10,
                "decimals": 2,
            },
        )
        assert calls[3][0] == (
            "gh_get_parameter_value",
            {"output_index": 0, "max_items": 12, "instance_id": "circle-id", "output_name": "C"},
        )


class TestToolAnnotations:
    """Source-level guard that the readOnly/destructive ToolAnnotations stay attached.
    A live-runtime check is fragile under pytest's import ordering, but the source
    pattern is what actually drives the annotation, so checking it catches drift."""

    def _module_source(self, rel_path):
        from pathlib import Path
        root = Path(__file__).parent.parent / "src" / "rhinomcp"
        return (root / rel_path).read_text()

    def test_read_only_tools_marked(self):
        for rel in [
            "tools/get_document_summary.py",
            "tools/get_objects.py",
            "tools/get_object_info.py",
            "tools/object_attributes.py",
            "tools/analyze_objects.py",
            "tools/get_selected_objects_info.py",
            "tools/get_commands.py",
            "tools/rhinoscript_docs.py",
            "tools/grasshopper_catalog.py",
            "tools/grasshopper_components.py",
            "tools/grasshopper_document.py",
            "tools/grasshopper_graph.py",
            "tools/grasshopper_parameters.py",
        ]:
            src = self._module_source(rel)
            assert "readOnlyHint=True" in src, f"{rel} missing readOnlyHint annotation"

    def test_destructive_tools_marked(self):
        for rel in [
            "tools/delete_object.py",
            "tools/delete_layer.py",
            "tools/run_command.py",
            "tools/execute_rhinoscript_python_code.py",
            "tools/execute_rhinocommon_csharp_code.py",
            "tools/grasshopper_components.py",
            "tools/grasshopper_graph.py",
        ]:
            src = self._module_source(rel)
            assert "destructiveHint=True" in src, f"{rel} missing destructiveHint annotation"


class TestPackageApi:
    """Lock in that the rhinomcp package re-exports tool functions at the top level.

    Slice 3 replaced the manual import list in __init__.py with auto-discovery.
    We preserve the pre-existing package API (`from rhinomcp import <tool>`) by
    re-exporting tool-module callables during discovery; these tests guard that.
    """

    def test_classic_tools_are_top_level_attrs(self):
        import rhinomcp
        # A representative sample across tool categories — covers single-method
        # modules and modules that export multiple functions.
        for name in [
            "create_object",
            "delete_object",
            "create_layer",
            "boolean_union",
            "boolean_intersection",
            "loft",
            "pipe",
            "walls_from_layer",
            "floor_from_layer",
            "roof_flat_from_walls",
            "openings_from_layer",
            "rooms_from_layer",
            "mark_as_existing",
            "delete_opening",
            "add_opening",
            "move_opening",
            "set_opening",
            "rebuild_host_wall",
            "clear_generated",
            "set_layer_material",
            "set_display_mode",
            "undo",
            "redo",
            "analyze_objects",
            "gh_create_document",
            "gh_get_document_info",
            "gh_search_components",
            "gh_get_graph",
            "gh_clear_graph",
            "gh_capture_preview",
            "gh_build_graph",
            "gh_mutate_graph",
            "gh_add_component",
            "gh_layout_components",
            "gh_run_solution",
            "gh_clear_canvas",
        ]:
            assert hasattr(rhinomcp, name), f"rhinomcp.{name} missing — re-export regression"
            assert callable(getattr(rhinomcp, name))

    def test_new_slice1_tools_are_top_level_attrs(self):
        import rhinomcp
        assert callable(getattr(rhinomcp, "run_command", None))
        assert callable(getattr(rhinomcp, "get_commands", None))

    def test_private_names_are_not_exported(self):
        import rhinomcp
        # The discovery loop skips names starting with "_"; verify nothing internal leaked.
        leaked = [n for n in dir(rhinomcp) if n in {"_TOOLS_DIR", "_info", "_mod", "_attr", "_value"}]
        assert not leaked, f"loop locals leaked into package namespace: {leaked}"


class TestWallsFromLayerTool:
    @patch("rhinomcp.tools.walls_from_layer.get_rhino_connection")
    def test_defaults(self, mock_get_conn):
        from rhinomcp.tools.walls_from_layer import walls_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["aaa-111"],
            "count": 1,
            "source_curves": 16,
            "joined": 16,
            "closed": 16,
            "skipped": 0,
            "warnings": [],
            "message": "Created 1 wall solid(s) on A-WALL from layer 'wall'.",
        }
        mock_get_conn.return_value = mock_conn

        result = walls_from_layer(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "walls_from_layer",
            {
                "layer": "wall",
                "height": 3000.0,
                "target_layer": "A-WALL",
                "name_prefix": "wall-",
                "apply_default_materials": True,
            },
        )
        assert result["success"] is True
        assert result["count"] == 1
        assert result["ids"] == ["aaa-111"]

    @patch("rhinomcp.tools.walls_from_layer.get_rhino_connection")
    def test_passes_stated_height(self, mock_get_conn):
        from rhinomcp.tools.walls_from_layer import walls_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["aaa-111"],
            "count": 1,
            "warnings": [],
            "message": "Created 1 wall solid(s) on A-WALL from layer 'wall'.",
        }
        mock_get_conn.return_value = mock_conn

        result = walls_from_layer(ctx=None, height=2700)
        assert result["success"] is True
        assert mock_conn.send_command.call_args[0][1]["height"] == 2700

    @patch("rhinomcp.tools.walls_from_layer.get_rhino_connection")
    def test_rejects_non_positive_height(self, mock_get_conn):
        from rhinomcp.tools.walls_from_layer import walls_from_layer

        result = walls_from_layer(ctx=None, height=0)
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.walls_from_layer.get_rhino_connection")
    def test_can_skip_default_materials(self, mock_get_conn):
        from rhinomcp.tools.walls_from_layer import walls_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["aaa-111"],
            "count": 1,
            "warnings": [],
            "message": "Created 1 wall solid(s) on A-WALL from layer 'wall'.",
        }
        mock_get_conn.return_value = mock_conn

        walls_from_layer(ctx=None, apply_default_materials=False)
        assert mock_conn.send_command.call_args[0][1]["apply_default_materials"] is False


class TestRoomsFromLayerTool:
    @patch("rhinomcp.tools.rooms_from_layer.get_rhino_connection")
    def test_defaults(self, mock_get_conn):
        from rhinomcp.tools.rooms_from_layer import rooms_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["room-guid"],
            "forsk_ids": ["r01"],
            "count": 1,
            "source_curves": 2,
            "joined": 2,
            "closed": 2,
            "skipped": 0,
            "warnings": [],
            "message": "Created 1 room marker(s) on A-ROOM from layer 'A-ROOM'.",
        }
        mock_get_conn.return_value = mock_conn

        result = rooms_from_layer(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "rooms_from_layer",
            {
                "layer": "A-ROOM",
                "target_layer": "A-ROOM",
                "name_prefix": "room-",
            },
        )
        assert result["success"] is True
        assert result["count"] == 1
        assert result["ids"] == ["room-guid"]
        assert result["forsk_ids"] == ["r01"]

    @patch("rhinomcp.tools.rooms_from_layer.get_rhino_connection")
    def test_empty_layer_is_success(self, mock_get_conn):
        from rhinomcp.tools.rooms_from_layer import rooms_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": [],
            "forsk_ids": [],
            "count": 0,
            "warnings": [],
            "message": "No closed room curves. Layer 'A-ROOM' not found.",
        }
        mock_get_conn.return_value = mock_conn

        result = rooms_from_layer(ctx=None, layer="room")
        assert result["success"] is True
        assert result["count"] == 0
        assert mock_conn.send_command.call_args[0][1]["layer"] == "room"

    @patch("rhinomcp.tools.rooms_from_layer.get_rhino_connection")
    def test_forwards_join_tolerance(self, mock_get_conn):
        from rhinomcp.tools.rooms_from_layer import rooms_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": [],
            "count": 0,
            "warnings": [],
            "message": "No curves on layer 'A-ROOM'.",
        }
        mock_get_conn.return_value = mock_conn

        rooms_from_layer(ctx=None, join_tolerance=2.5)
        assert mock_conn.send_command.call_args[0][1]["join_tolerance"] == 2.5


class TestFloorFromLayerTool:
    @patch("rhinomcp.tools.floor_from_layer.get_rhino_connection")
    def test_defaults(self, mock_get_conn):
        from rhinomcp.tools.floor_from_layer import floor_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["floor-1"],
            "count": 1,
            "thickness": 400,
            "warnings": [],
            "message": "Created 1 floor slab(s) on A-FLOR, thickness 400, top at plan Z.",
        }
        mock_get_conn.return_value = mock_conn

        result = floor_from_layer(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "floor_from_layer",
            {
                "layer": "wall",
                "thickness": 400.0,
                "target_layer": "A-FLOR",
                "name_prefix": "floor-",
                "apply_default_materials": True,
            },
        )
        assert result["success"] is True
        assert result["count"] == 1
        assert result["thickness"] == 400

    @patch("rhinomcp.tools.floor_from_layer.get_rhino_connection")
    def test_rejects_non_positive_thickness(self, mock_get_conn):
        from rhinomcp.tools.floor_from_layer import floor_from_layer

        result = floor_from_layer(ctx=None, thickness=0)
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.floor_from_layer.get_rhino_connection")
    def test_can_skip_default_materials(self, mock_get_conn):
        from rhinomcp.tools.floor_from_layer import floor_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["floor-1"],
            "count": 1,
            "thickness": 400,
            "warnings": [],
            "message": "Created 1 floor slab(s) on A-FLOR, thickness 400, top at plan Z.",
        }
        mock_get_conn.return_value = mock_conn

        floor_from_layer(ctx=None, apply_default_materials=False)
        assert mock_conn.send_command.call_args[0][1]["apply_default_materials"] is False


class TestRoofFlatFromWallsTool:
    @patch("rhinomcp.tools.roof_flat_from_walls.get_rhino_connection")
    def test_defaults(self, mock_get_conn):
        from rhinomcp.tools.roof_flat_from_walls import roof_flat_from_walls

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["roof-1"],
            "count": 1,
            "kind": "roof",
            "roof_type": "flat",
            "thickness": 200,
            "overhang": 0,
            "bbox": [[0, 0, 2800], [10000, 8000, 3000]],
            "warnings": [],
            "message": "Created 1 roof slab(s) on A-ROOF, thickness 200, overhang 0, top at Z=3000.",
        }
        mock_get_conn.return_value = mock_conn

        result = roof_flat_from_walls(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "roof_flat_from_walls",
            {
                "layer": "wall",
                "thickness": 200.0,
                "overhang": 0.0,
                "target_layer": "A-ROOF",
                "name_prefix": "roof-",
            },
        )
        assert result["success"] is True
        assert result["count"] == 1
        assert result["kind"] == "roof"
        assert result["roof_type"] == "flat"
        assert result["thickness"] == 200
        assert result["overhang"] == 0
        assert result["bbox"] == [[0, 0, 2800], [10000, 8000, 3000]]

    @patch("rhinomcp.tools.roof_flat_from_walls.get_rhino_connection")
    def test_rejects_non_positive_thickness(self, mock_get_conn):
        from rhinomcp.tools.roof_flat_from_walls import roof_flat_from_walls

        result = roof_flat_from_walls(ctx=None, thickness=0)
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.roof_flat_from_walls.get_rhino_connection")
    def test_rejects_negative_overhang(self, mock_get_conn):
        from rhinomcp.tools.roof_flat_from_walls import roof_flat_from_walls

        result = roof_flat_from_walls(ctx=None, overhang=-1)
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.roof_flat_from_walls.get_rhino_connection")
    def test_optional_overhang_forwarded(self, mock_get_conn):
        from rhinomcp.tools.roof_flat_from_walls import roof_flat_from_walls

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["roof-1"],
            "count": 1,
            "kind": "roof",
            "roof_type": "flat",
            "thickness": 200,
            "overhang": 200,
            "warnings": [],
            "message": "Created 1 roof slab(s) on A-ROOF.",
        }
        mock_get_conn.return_value = mock_conn

        roof_flat_from_walls(ctx=None, overhang=200.0)
        assert mock_conn.send_command.call_args[0][1]["overhang"] == 200.0


class TestSetLayerMaterialTool:
    @patch("rhinomcp.tools.set_layer_material.get_rhino_connection")
    def test_preset_wood_on_walls_alias(self, mock_get_conn):
        from rhinomcp.tools.set_layer_material import set_layer_material

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "layer": "A-WALL",
            "material_name": "M-WOOD",
            "material_id": "aaa-111",
            "objects_updated": 4,
            "message": "Assigned M-WOOD to layer A-WALL (By Layer, 4 object(s)).",
        }
        mock_get_conn.return_value = mock_conn

        result = set_layer_material(ctx=None, layer_name="walls", preset="oak")

        mock_conn.send_command.assert_called_once_with(
            "set_layer_material",
            {
                "layer_name": "A-WALL",
                "ensure_objects_from_layer": True,
                "preset": "wood",
            },
        )
        assert result["success"] is True
        assert result["material_name"] == "M-WOOD"
        assert result["objects_updated"] == 4

    @patch("rhinomcp.tools.set_layer_material.get_rhino_connection")
    def test_floor_white(self, mock_get_conn):
        from rhinomcp.tools.set_layer_material import set_layer_material

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "layer": "A-FLOR",
            "material_name": "M-WHITE",
            "objects_updated": 1,
            "message": "Assigned M-WHITE to layer A-FLOR (By Layer, 1 object(s)).",
        }
        mock_get_conn.return_value = mock_conn

        result = set_layer_material(ctx=None, layer_name="floor", preset="white")
        assert result["success"] is True
        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["layer_name"] == "A-FLOR"
        assert call_args[0][1]["preset"] == "white"

    @patch("rhinomcp.tools.set_layer_material.get_rhino_connection")
    def test_rejects_unknown_preset(self, mock_get_conn):
        from rhinomcp.tools.set_layer_material import set_layer_material

        result = set_layer_material(ctx=None, layer_name="A-WALL", preset="glass")
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.set_layer_material.get_rhino_connection")
    def test_rejects_missing_selector(self, mock_get_conn):
        from rhinomcp.tools.set_layer_material import set_layer_material

        result = set_layer_material(ctx=None, layer_name="A-WALL")
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.set_layer_material.get_rhino_connection")
    def test_custom_diffuse(self, mock_get_conn):
        from rhinomcp.tools.set_layer_material import set_layer_material

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "layer": "A-WALL",
            "material_name": "M-CUSTOM",
            "objects_updated": 2,
            "message": "Assigned M-CUSTOM to layer A-WALL (By Layer, 2 object(s)).",
        }
        mock_get_conn.return_value = mock_conn

        result = set_layer_material(
            ctx=None, layer_name="A-WALL", name="M-CUSTOM", diffuse_rgb=[200, 180, 160]
        )
        assert result["success"] is True
        call_args = mock_conn.send_command.call_args
        assert call_args[0][1]["name"] == "M-CUSTOM"
        assert call_args[0][1]["diffuse_rgb"] == [200, 180, 160]
        assert "preset" not in call_args[0][1]


class TestSetDisplayModeTool:
    @patch("rhinomcp.tools.set_display_mode.get_rhino_connection")
    def test_rendered(self, mock_get_conn):
        from rhinomcp.tools.set_display_mode import set_display_mode

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "mode": "Rendered",
            "viewport": "Perspective",
            "message": "Set viewport 'Perspective' to Rendered.",
        }
        mock_get_conn.return_value = mock_conn

        result = set_display_mode(ctx=None, mode="rendered")
        mock_conn.send_command.assert_called_once_with(
            "set_display_mode", {"mode": "Rendered"}
        )
        assert result["success"] is True
        assert result["mode"] == "Rendered"

    @patch("rhinomcp.tools.set_display_mode.get_rhino_connection")
    def test_rejects_unknown_mode(self, mock_get_conn):
        from rhinomcp.tools.set_display_mode import set_display_mode

        result = set_display_mode(ctx=None, mode="Ghosted")
        assert result["success"] is False
        mock_get_conn.assert_not_called()


class TestOpeningsFromLayerTool:
    @patch("rhinomcp.tools.openings_from_layer.get_rhino_connection")
    def test_door_defaults(self, mock_get_conn):
        from rhinomcp.tools.openings_from_layer import openings_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "cut_count": 14,
            "failed_count": 0,
            "failures": [],
            "wall_ids": ["wall-1"],
            "opening_count": 14,
            "marker_ids": ["marker-1", "marker-2"],
            "block_ids": ["block-1", "block-2"],
            "sill": 0,
            "head": 2100,
            "message": "Cut 14 opening(s) from layer 'door' (0 failure(s), 2 marker(s)).",
        }
        mock_get_conn.return_value = mock_conn

        result = openings_from_layer(ctx=None, layer="door")

        mock_conn.send_command.assert_called_once_with(
            "openings_from_layer",
            {
                "layer": "door",
                "target_layer": "A-WALL",
                "pad": 50.0,
                "min_depth": 250.0,
            },
        )
        assert result["success"] is True
        assert result["cut_count"] == 14
        assert result["failed_count"] == 0
        assert result["marker_ids"] == ["marker-1", "marker-2"]
        assert result["block_ids"] == ["block-1", "block-2"]

    @patch("rhinomcp.tools.openings_from_layer.get_rhino_connection")
    def test_window_limit_and_ids(self, mock_get_conn):
        from rhinomcp.tools.openings_from_layer import openings_from_layer

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "cut_count": 3,
            "failed_count": 1,
            "failures": [{"source_id": "x", "reason": "no hit"}],
            "wall_ids": ["w1"],
            "message": "Cut 3 opening(s)",
        }
        mock_get_conn.return_value = mock_conn

        result = openings_from_layer(
            ctx=None,
            layer="window",
            sill=900,
            head=2100,
            wall_ids=["w1"],
            limit=5,
        )

        call_args = mock_conn.send_command.call_args
        assert call_args[0][0] == "openings_from_layer"
        params = call_args[0][1]
        assert params["layer"] == "window"
        assert params["sill"] == 900
        assert params["head"] == 2100
        assert params["wall_ids"] == ["w1"]
        assert params["limit"] == 5
        assert result["failed_count"] == 1


class TestDeleteOpeningTool:
    @patch("rhinomcp.tools.delete_opening.get_rhino_connection")
    def test_no_id_sends_empty(self, mock_get_conn):
        from rhinomcp.tools.delete_opening import delete_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted_marker_id": "m1",
            "host_id": "h1",
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        result = delete_opening(ctx=None)

        mock_conn.send_command.assert_called_once_with("delete_opening", {})
        assert result["success"] is True
        assert result["deleted_marker_id"] == "m1"
        assert result["host_id"] == "h1"
        assert result["ok"] is True

    @patch("rhinomcp.tools.delete_opening.get_rhino_connection")
    def test_forwards_id(self, mock_get_conn):
        from rhinomcp.tools.delete_opening import delete_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted_marker_id": "m1",
            "host_id": "h1",
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        guid = "12345678-1234-1234-1234-123456789012"
        result = delete_opening(ctx=None, id=guid)

        mock_conn.send_command.assert_called_once_with(
            "delete_opening", {"id": guid}
        )
        assert result["success"] is True


class TestAddOpeningTool:
    @patch("rhinomcp.tools.add_opening.get_rhino_connection")
    def test_door_kind_only(self, mock_get_conn):
        from rhinomcp.tools.add_opening import add_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "opening_kind": "door",
            "width": 900,
            "sill": 0,
            "head": 2100,
            "t": 0.5,
            "ok": True,
            "message": "Added door opening on wall.",
        }
        mock_get_conn.return_value = mock_conn

        result = add_opening(ctx=None, opening_kind="door")

        mock_conn.send_command.assert_called_once_with(
            "add_opening", {"opening_kind": "door"}
        )
        assert result["success"] is True
        assert result["opening_kind"] == "door"
        assert result["t"] == 0.5

    @patch("rhinomcp.tools.add_opening.get_rhino_connection")
    def test_passes_stated_sill_head_width(self, mock_get_conn):
        from rhinomcp.tools.add_opening import add_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "opening_kind": "window",
            "width": 1200,
            "sill": 1000,
            "head": 2200,
            "t": 0.5,
            "ok": True,
            "message": "Added window opening on wall.",
        }
        mock_get_conn.return_value = mock_conn

        result = add_opening(
            ctx=None, opening_kind="window", sill=1000, head=2200, width=1200
        )
        assert result["success"] is True
        assert mock_conn.send_command.call_args[0][1] == {
            "opening_kind": "window",
            "sill": 1000,
            "head": 2200,
            "width": 1200,
        }

    @patch("rhinomcp.tools.add_opening.get_rhino_connection")
    def test_window_with_t_omits_distance(self, mock_get_conn):
        from rhinomcp.tools.add_opening import add_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "opening_kind": "window",
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        add_opening(ctx=None, opening_kind="window", t=0.5)

        mock_conn.send_command.assert_called_once_with(
            "add_opening", {"opening_kind": "window", "t": 0.5}
        )

    @patch("rhinomcp.tools.add_opening.get_rhino_connection")
    def test_rejects_t_and_distance(self, mock_get_conn):
        from rhinomcp.tools.add_opening import add_opening

        result = add_opening(
            ctx=None, opening_kind="door", t=0.2, distance_mm=100
        )
        assert result["success"] is False
        assert "not both" in result["message"]
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.add_opening.get_rhino_connection")
    def test_rejects_bad_kind(self, mock_get_conn):
        from rhinomcp.tools.add_opening import add_opening

        result = add_opening(ctx=None, opening_kind="portal")
        assert result["success"] is False
        assert "door or window" in result["message"]
        mock_get_conn.assert_not_called()


class TestMoveOpeningTool:
    @patch("rhinomcp.tools.move_opening.get_rhino_connection")
    def test_delta_mm(self, mock_get_conn):
        from rhinomcp.tools.move_opening import move_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "t": 0.42,
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        result = move_opening(ctx=None, delta_mm=500)

        mock_conn.send_command.assert_called_once_with(
            "move_opening", {"delta_mm": 500}
        )
        assert result["success"] is True
        assert result["t"] == 0.42

    @patch("rhinomcp.tools.move_opening.get_rhino_connection")
    def test_absolute_t(self, mock_get_conn):
        from rhinomcp.tools.move_opening import move_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "t": 0.3,
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        move_opening(ctx=None, t=0.3)

        mock_conn.send_command.assert_called_once_with(
            "move_opening", {"t": 0.3}
        )

    @patch("rhinomcp.tools.move_opening.get_rhino_connection")
    def test_rejects_both(self, mock_get_conn):
        from rhinomcp.tools.move_opening import move_opening

        result = move_opening(ctx=None, delta_mm=500, t=0.3)
        assert result["success"] is False
        assert "not both" in result["message"]
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.move_opening.get_rhino_connection")
    def test_rejects_neither(self, mock_get_conn):
        from rhinomcp.tools.move_opening import move_opening

        result = move_opening(ctx=None)
        assert result["success"] is False
        assert "Specify delta_mm or t." in result["message"]
        mock_get_conn.assert_not_called()


class TestSetOpeningTool:
    @patch("rhinomcp.tools.set_opening.get_rhino_connection")
    def test_width_only(self, mock_get_conn):
        from rhinomcp.tools.set_opening import set_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "width": 1400,
            "sill": 900,
            "head": 2100,
            "t": 0.4,
            "ok": True,
            "message": "Set the opening to width 1400 mm, sill 900 mm, head 2100 mm.",
        }
        mock_get_conn.return_value = mock_conn

        result = set_opening(ctx=None, width=1400)

        mock_conn.send_command.assert_called_once_with(
            "set_opening", {"width": 1400}
        )
        assert result["success"] is True
        assert result["width"] == 1400
        assert result["host_id"] == "h1"

    @patch("rhinomcp.tools.set_opening.get_rhino_connection")
    def test_sill_and_head_with_id(self, mock_get_conn):
        from rhinomcp.tools.set_opening import set_opening

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "marker_id": "m1",
            "host_id": "h1",
            "width": 1200,
            "sill": 1000,
            "head": 2200,
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        guid = "12345678-1234-1234-1234-123456789012"
        set_opening(ctx=None, id=guid, sill=1000, head=2200)

        mock_conn.send_command.assert_called_once_with(
            "set_opening", {"id": guid, "sill": 1000, "head": 2200}
        )

    @patch("rhinomcp.tools.set_opening.get_rhino_connection")
    def test_rejects_empty(self, mock_get_conn):
        from rhinomcp.tools.set_opening import set_opening

        result = set_opening(ctx=None)
        assert result["success"] is False
        assert "Specify width, sill, or head." in result["message"]
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.set_opening.get_rhino_connection")
    def test_rejects_bad_width(self, mock_get_conn):
        from rhinomcp.tools.set_opening import set_opening

        result = set_opening(ctx=None, width=0)
        assert result["success"] is False
        assert "width must be positive." in result["message"]
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.set_opening.get_rhino_connection")
    def test_rejects_head_below_sill(self, mock_get_conn):
        from rhinomcp.tools.set_opening import set_opening

        result = set_opening(ctx=None, sill=2200, head=1000)
        assert result["success"] is False
        assert "head must be greater than sill." in result["message"]
        mock_get_conn.assert_not_called()


class TestRebuildHostWallTool:
    @patch("rhinomcp.tools.rebuild_host_wall.get_rhino_connection")
    def test_selection_omits_id(self, mock_get_conn):
        from rhinomcp.tools.rebuild_host_wall import rebuild_host_wall

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "host_id": "h1",
            "forsk_id": "w01",
            "level": "0",
            "height": 3000,
            "thickness": 200,
            "path_points": 12,
            "opening_count": 2,
            "marker_ids": ["m1"],
            "solid_volume": 1200000000,
            "ok": True,
            "message": "Rebuilt wall w01 with 2 openings.",
        }
        mock_get_conn.return_value = mock_conn

        result = rebuild_host_wall(ctx=None)

        mock_conn.send_command.assert_called_once_with("rebuild_host_wall", {})
        assert result["success"] is True
        assert result["host_id"] == "h1"
        assert result["forsk_id"] == "w01"
        assert result["opening_count"] == 2
        assert result["solid_volume"] == 1200000000
        assert result["ok"] is True

    @patch("rhinomcp.tools.rebuild_host_wall.get_rhino_connection")
    def test_forwards_id(self, mock_get_conn):
        from rhinomcp.tools.rebuild_host_wall import rebuild_host_wall

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "host_id": "h1",
            "forsk_id": "w01",
            "height": 3000,
            "thickness": 180,
            "opening_count": 0,
            "ok": True,
            "message": "Rebuilt wall w01 with 0 openings.",
        }
        mock_get_conn.return_value = mock_conn

        guid = "12345678-1234-1234-1234-123456789012"
        rebuild_host_wall(ctx=None, id=guid)

        mock_conn.send_command.assert_called_once_with(
            "rebuild_host_wall", {"id": guid}
        )

    @patch("rhinomcp.tools.rebuild_host_wall.get_rhino_connection")
    def test_blank_id_omitted(self, mock_get_conn):
        from rhinomcp.tools.rebuild_host_wall import rebuild_host_wall

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "host_id": "h1",
            "forsk_id": "w01",
            "height": 3000,
            "thickness": 180,
            "opening_count": 0,
            "ok": True,
        }
        mock_get_conn.return_value = mock_conn

        rebuild_host_wall(ctx=None, id="")

        mock_conn.send_command.assert_called_once_with("rebuild_host_wall", {})


class TestClearGeneratedTool:
    @patch("rhinomcp.tools.clear_generated.get_rhino_connection")
    def test_defaults_omit_kinds(self, mock_get_conn):
        from rhinomcp.tools.clear_generated import clear_generated

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": ["a", "b"],
            "count": 2,
            "dry_run": False,
        }
        mock_get_conn.return_value = mock_conn

        result = clear_generated(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "clear_generated",
            {
                "dry_run": False,
                "include_untagged_prefixes": False,
            },
        )
        assert result["success"] is True
        assert result["deleted"] == ["a", "b"]
        assert result["count"] == 2
        assert result["dry_run"] is False

    @patch("rhinomcp.tools.clear_generated.get_rhino_connection")
    def test_dry_run_forwarded(self, mock_get_conn):
        from rhinomcp.tools.clear_generated import clear_generated

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": ["x"],
            "count": 1,
            "dry_run": True,
        }
        mock_get_conn.return_value = mock_conn

        result = clear_generated(ctx=None, dry_run=True)

        params = mock_conn.send_command.call_args[0][1]
        assert mock_conn.send_command.call_args[0][0] == "clear_generated"
        assert params["dry_run"] is True
        assert result["dry_run"] is True
        assert result["deleted"] == ["x"]
        assert result["count"] == 1

    @patch("rhinomcp.tools.clear_generated.get_rhino_connection")
    def test_kinds_and_untagged(self, mock_get_conn):
        from rhinomcp.tools.clear_generated import clear_generated

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": [],
            "count": 0,
            "dry_run": False,
        }
        mock_get_conn.return_value = mock_conn

        clear_generated(
            ctx=None,
            kinds=["wall", "floor"],
            level="0",
            include_untagged_prefixes=True,
            name_prefixes=["wall-"],
        )
        params = mock_conn.send_command.call_args[0][1]
        assert params["kinds"] == ["wall", "floor"]
        assert params["level"] == "0"
        assert params["include_untagged_prefixes"] is True
        assert params["name_prefixes"] == ["wall-"]


class TestMarkAsExistingTool:
    @patch("rhinomcp.tools.mark_as_existing.get_rhino_connection")
    def test_selection_defaults_to_x_exist(self, mock_get_conn):
        from rhinomcp.tools.mark_as_existing import mark_as_existing

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": ["aaa-111"],
            "forsk_ids": ["x01"],
            "count": 1,
            "target_layer": "X-EXIST",
            "message": "Marked 1 object(s) as existing on X-EXIST.",
        }
        mock_get_conn.return_value = mock_conn

        result = mark_as_existing(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "mark_as_existing",
            {"target_layer": "X-EXIST"},
        )
        assert result["success"] is True
        assert result["count"] == 1
        assert result["ids"] == ["aaa-111"]
        assert result["forsk_ids"] == ["x01"]
        assert result["target_layer"] == "X-EXIST"

    @patch("rhinomcp.tools.mark_as_existing.get_rhino_connection")
    def test_forwards_ids(self, mock_get_conn):
        from rhinomcp.tools.mark_as_existing import mark_as_existing

        guid = "12345678-1234-1234-1234-123456789012"
        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "ids": [guid],
            "forsk_ids": ["x01"],
            "count": 1,
            "target_layer": "X-EXIST",
            "message": "Marked 1 object(s) as existing on X-EXIST.",
        }
        mock_get_conn.return_value = mock_conn

        result = mark_as_existing(ctx=None, ids=[guid])
        assert result["success"] is True
        assert mock_conn.send_command.call_args[0][1]["ids"] == [guid]

    @patch("rhinomcp.tools.mark_as_existing.get_rhino_connection")
    def test_refuses_non_list_ids(self, mock_get_conn):
        from rhinomcp.tools.mark_as_existing import mark_as_existing

        result = mark_as_existing(ctx=None, ids="not-a-list")
        assert result["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.mark_as_existing.get_rhino_connection")
    def test_plugin_refuse_is_failure(self, mock_get_conn):
        from rhinomcp.tools.mark_as_existing import mark_as_existing

        mock_conn = MagicMock()
        mock_conn.send_command.side_effect = RuntimeError(
            "Nothing is selected. Select the existing building, then mark it again."
        )
        mock_get_conn.return_value = mock_conn

        result = mark_as_existing(ctx=None)
        assert result["success"] is False
        assert "Nothing is selected" in result["message"]


class TestExistingUnderlayGuards:
    """Lock the C# protection rules. Live Rhino is not required."""

    def _text(self, *parts):
        from pathlib import Path

        root = Path(__file__).resolve().parents[2]
        return (root.joinpath(*parts)).read_text()

    def test_stamp_existing_never_sets_generated(self):
        tags = self._text("plugin", "Functions", "ForskTags.cs")
        assert "private static void StampExisting" in tags
        assert 'SetUserString("forsk:generated", null)' in tags
        assert 'SetUserString("forsk:kind", "existing")' in tags
        assert "X-EXIST is existing underlay, not a bake source." in tags
        assert "Existing underlay is not a Forsk host wall." in tags
        # Generated solids still go through StampForskTags.
        assert 'SetUserString("forsk:generated", "1")' in tags

        mark = self._text("plugin", "Functions", "MarkAsExisting.cs")
        assert "StampExisting" in mark
        assert "StampForskTags" not in mark

    def test_clear_generated_skips_existing_underlay(self):
        clear = self._text("plugin", "Functions", "ClearGenerated.cs")
        assert "IsExistingUnderlay" in clear
        schema = self._text("contracts", "commands", "clear_generated.json")
        assert "X-EXIST" in schema
        assert "forsk:kind=existing" in schema

    def test_bake_tools_refuse_x_exist_source(self):
        for name in (
            "WallsFromLayer.cs",
            "FloorFromLayer.cs",
            "RoomsFromLayer.cs",
        ):
            src = self._text("plugin", "Functions", name)
            assert "IsExistingLayerName" in src
            assert "ExistingBakeRefusal" in src
        openings = self._text("plugin", "Functions", "OpeningsFromLayer.cs")
        assert "IsExistingLayerName" in openings
        assert "ExistingOpeningsRefusal" in openings

    def test_opening_edits_refuse_existing_host(self):
        facade = self._text("plugin", "Functions", "FacadeOpenings.cs")
        assert facade.count("RefuseExistingUnderlay") >= 3


class TestMake2dViewTool:
    @patch("rhinomcp.tools.make2d_view.get_rhino_connection")
    def test_plan_forwards_defaults(self, mock_get_conn):
        from rhinomcp.tools.make2d_view import make2d_view

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "count": 3,
            "ids": ["a", "b", "c"],
            "layer": "S-PLAN",
            "view": "plan",
            "message": "Drew 3 curve(s) on S-PLAN (plan).",
        }
        mock_get_conn.return_value = mock_conn

        result = make2d_view(ctx=None, view="plan")

        mock_conn.send_command.assert_called_once_with(
            "make2d_view",
            {"view": "plan", "include_existing": True, "replace": True},
        )
        assert result["success"] is True
        assert result["count"] == 3
        assert result["layer"] == "S-PLAN"
        assert result["view"] == "plan"
        assert result["ids"] == ["a", "b", "c"]

    @patch("rhinomcp.tools.make2d_view.get_rhino_connection")
    def test_empty_source_is_count_zero(self, mock_get_conn):
        from rhinomcp.tools.make2d_view import make2d_view

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "count": 0,
            "ids": [],
            "layer": "S-ELEV-S",
            "view": "south",
            "message": "Nothing to draw. Bake walls, floor, or roof first.",
        }
        mock_get_conn.return_value = mock_conn

        result = make2d_view(ctx=None, view="south", include_existing=False, replace=False)

        params = mock_conn.send_command.call_args[0][1]
        assert params["view"] == "south"
        assert params["include_existing"] is False
        assert params["replace"] is False
        assert "ids" not in params
        assert result["success"] is True
        assert result["count"] == 0
        assert "Nothing to draw" in result["message"]

    @patch("rhinomcp.tools.make2d_view.get_rhino_connection")
    def test_forwards_ids(self, mock_get_conn):
        from rhinomcp.tools.make2d_view import make2d_view

        guid = "12345678-1234-1234-1234-123456789012"
        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "count": 1,
            "ids": ["curve"],
            "layer": "S-ELEV-N",
            "view": "north",
            "message": "Drew 1 curve(s) on S-ELEV-N (north).",
        }
        mock_get_conn.return_value = mock_conn

        result = make2d_view(ctx=None, view="north", ids=[guid])
        assert result["success"] is True
        assert mock_conn.send_command.call_args[0][1]["ids"] == [guid]

    @patch("rhinomcp.tools.make2d_view.get_rhino_connection")
    def test_rejects_unknown_view(self, mock_get_conn):
        from rhinomcp.tools.make2d_view import make2d_view

        result = make2d_view(ctx=None, view="section")
        assert result["success"] is False
        assert "Unknown view" in result["message"]
        mock_get_conn.assert_not_called()


class TestSheetPackTool:
    @patch("rhinomcp.tools.sheet_pack.get_rhino_connection")
    def test_default_omits_views(self, mock_get_conn):
        from rhinomcp.tools.sheet_pack import sheet_pack

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "views": [],
            "count": 0,
            "message": "Drew 0 curve(s) across 5 view(s).",
        }
        mock_get_conn.return_value = mock_conn

        result = sheet_pack(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "sheet_pack",
            {"include_existing": True, "replace": True},
        )
        assert result["success"] is True
        assert result["count"] == 0
        assert "views" not in mock_conn.send_command.call_args[0][1]

    @patch("rhinomcp.tools.sheet_pack.get_rhino_connection")
    def test_forwards_views(self, mock_get_conn):
        from rhinomcp.tools.sheet_pack import sheet_pack

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "views": [{"view": "plan", "count": 1}],
            "count": 1,
            "message": "Drew 1 curve(s) across 1 view(s).",
        }
        mock_get_conn.return_value = mock_conn

        result = sheet_pack(ctx=None, views=["plan"])
        assert result["success"] is True
        assert mock_conn.send_command.call_args[0][1]["views"] == ["plan"]

    @patch("rhinomcp.tools.sheet_pack.get_rhino_connection")
    def test_rejects_unknown_view(self, mock_get_conn):
        from rhinomcp.tools.sheet_pack import sheet_pack

        result = sheet_pack(ctx=None, views=["plan", "section"])
        assert result["success"] is False
        mock_get_conn.assert_not_called()


class TestClearDrawingsTool:
    @patch("rhinomcp.tools.clear_drawings.get_rhino_connection")
    def test_defaults(self, mock_get_conn):
        from rhinomcp.tools.clear_drawings import clear_drawings

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": ["d1"],
            "count": 1,
            "dry_run": False,
        }
        mock_get_conn.return_value = mock_conn

        result = clear_drawings(ctx=None)

        mock_conn.send_command.assert_called_once_with(
            "clear_drawings",
            {"dry_run": False},
        )
        assert result["success"] is True
        assert result["deleted"] == ["d1"]
        assert result["count"] == 1
        assert "drawing" in result["message"]

    @patch("rhinomcp.tools.clear_drawings.get_rhino_connection")
    def test_dry_run_and_views(self, mock_get_conn):
        from rhinomcp.tools.clear_drawings import clear_drawings

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": [],
            "count": 0,
            "dry_run": True,
        }
        mock_get_conn.return_value = mock_conn

        result = clear_drawings(ctx=None, views=["east"], dry_run=True)
        params = mock_conn.send_command.call_args[0][1]
        assert params == {"dry_run": True, "views": ["east"]}
        assert result["dry_run"] is True
        assert result["message"].startswith("Would delete")

    @patch("rhinomcp.tools.clear_drawings.get_rhino_connection")
    def test_rejects_unknown_view(self, mock_get_conn):
        from rhinomcp.tools.clear_drawings import clear_drawings

        result = clear_drawings(ctx=None, views=["section"])
        assert result["success"] is False
        mock_get_conn.assert_not_called()


class TestSheetGuards:
    """Lock Make2D engine choice and the clear_generated exclusion. No live Rhino."""

    def _text(self, *parts):
        from pathlib import Path

        root = Path(__file__).resolve().parents[2]
        return (root.joinpath(*parts)).read_text()

    def test_make2d_uses_hidden_line_drawing(self):
        src = self._text("plugin", "Functions", "Make2dView.cs")
        assert "HiddenLineDrawing" in src
        assert "IncludeHiddenCurves = false" in src
        assert "Flatten = true" in src
        assert "Nothing to draw. Bake walls, floor, or roof first." in src
        assert 'Kind = "drawing"' in src
        assert 'SetUserString("forsk:view"' in self._text("plugin", "Functions", "ForskTags.cs")
        assert "capture_viewport" not in src
        assert "RunScript" not in src
        for layer in ("S-PLAN", "S-ELEV-N", "S-ELEV-E", "S-ELEV-S", "S-ELEV-W"):
            assert layer in src

    def test_offsets_match_product_sheets_json(self):
        import json
        from pathlib import Path

        src = self._text("plugin", "Functions", "Make2dView.cs")
        sibling = Path(__file__).resolve().parents[2].parent / "forsk" / "templates" / "sheets.json"
        if sibling.is_file():
            spec = json.loads(sibling.read_text())
            for view, row in spec["views"].items():
                offset = row["offset"]
                literal = f"{int(offset[0])}, {int(offset[1])}, {int(offset[2])}"
                assert literal in src
                assert row["layer"] in src
                assert view in src
        else:
            for token in (
                "S-PLAN",
                "0, -15000, 0",
                "15000, -15000, 0",
                "30000, -15000, 0",
                "45000, -15000, 0",
            ):
                assert token in src

    def test_replace_deletes_same_view_before_bake(self):
        src = self._text("plugin", "Functions", "Make2dView.cs")
        assert "DeleteViewDrawings" in src
        assert 'GetUserString("forsk:view")' in src
        # replace runs only after a successful hidden-line compute
        assert src.index("HiddenLineDrawing.Compute") < src.index("DeleteViewDrawings")

    def test_clear_generated_default_skips_drawings(self):
        import json
        import re

        clear = self._text("plugin", "Functions", "ClearGenerated.cs")
        match = re.search(r"new List<string>\s*\{([^}]+)\}", clear)
        assert match, "default kinds initializer missing"
        assert "drawing" not in match.group(1)
        schema = json.loads(self._text("contracts", "commands", "clear_generated.json"))
        assert "drawing" not in schema["properties"]["kinds"]["default"]
        # Explicit kinds still go through the allow-list; drawing is not hard-skipped.
        assert 'kind == "drawing"' not in clear
        drawings = self._text("plugin", "Functions", "Make2dView.cs")
        assert 'GetForskKind(obj), "drawing"' in drawings

    def test_clear_drawings_matches_kind_drawing_only(self):
        src = self._text("plugin", "Functions", "Make2dView.cs")
        start = src.index('[McpCommand("clear_drawings")]')
        body = src[start:src.index("private static JObject SheetViewResult")]
        assert '"drawing"' in body
        assert "wall" not in body
        assert "X-EXIST" not in body


class TestSetProjectMetaTool:
    @patch("rhinomcp.tools.set_project_meta.get_rhino_connection")
    def test_forwards_only_given_fields(self, mock_get_conn):
        from rhinomcp.tools.set_project_meta import set_project_meta

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "project": "Villa X",
            "client": "",
            "address": "",
            "date": "2026-09-21",
            "scale_label": "1:100",
        }
        mock_get_conn.return_value = mock_conn

        result = set_project_meta(ctx=None, project="Villa X")
        assert result["success"] is True
        assert result["project"] == "Villa X"
        mock_conn.send_command.assert_called_once_with(
            "set_project_meta",
            {"project": "Villa X"},
        )

    @patch("rhinomcp.tools.set_project_meta.get_rhino_connection")
    def test_rejects_non_string(self, mock_get_conn):
        from rhinomcp.tools.set_project_meta import set_project_meta

        result = set_project_meta(ctx=None, project=12)
        assert result["success"] is False
        mock_get_conn.assert_not_called()


class TestLayoutPackTool:
    @patch("rhinomcp.tools.layout_pack.get_rhino_connection")
    def test_default_omits_views(self, mock_get_conn):
        from rhinomcp.tools.layout_pack import layout_pack

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "pages": [],
            "count": 0,
            "scale": 100,
            "message": "Nothing to lay out. Bake walls first.",
        }
        mock_get_conn.return_value = mock_conn

        result = layout_pack(ctx=None)
        assert result["success"] is True
        assert result["message"].startswith("Nothing to lay out")
        params = mock_conn.send_command.call_args[0][1]
        assert params["paper"] == "A3"
        assert params["scale"] == 100
        assert "views" not in params

    @patch("rhinomcp.tools.layout_pack.get_rhino_connection")
    def test_rejects_unknown_view_and_paper(self, mock_get_conn):
        from rhinomcp.tools.layout_pack import layout_pack

        assert layout_pack(ctx=None, views=["section"])["success"] is False
        assert layout_pack(ctx=None, paper="A1")["success"] is False
        assert layout_pack(ctx=None, scale=0)["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.layout_pack.get_rhino_connection")
    def test_empty_detail_is_not_success(self, mock_get_conn):
        from rhinomcp.tools.layout_pack import layout_pack

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "pages": [],
            "count": 0,
            "scale": 100,
            "message": "Layout detail is empty. The sheet does not show the drawing.",
        }
        mock_get_conn.return_value = mock_conn

        result = layout_pack(ctx=None)
        assert result["success"] is False
        assert "does not show the drawing" in result["message"]


class TestExportPdfTool:
    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_requires_absolute_pdf_path(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        missing = export_pdf(ctx=None, path="")
        assert missing["success"] is False
        assert missing["message"] == "export_pdf requires a file path."
        relative = export_pdf(ctx=None, path="out.pdf")
        assert relative["success"] is False
        assert "absolute" in relative["message"]
        text = export_pdf(ctx=None, path="/tmp/sheet.txt")
        assert text["success"] is False
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_forwards_path(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "path": "/tmp/forsk-plan.pdf",
            "count": 1,
            "pages": ["Forsk — Plan"],
            "message": "Wrote 1 page(s) to /tmp/forsk-plan.pdf.",
        }
        mock_get_conn.return_value = mock_conn

        result = export_pdf(ctx=None, path="/tmp/forsk-plan.pdf", layout="plan")
        assert result["success"] is True
        assert result["count"] == 1
        mock_conn.send_command.assert_called_once_with(
            "export_pdf",
            {"path": "/tmp/forsk-plan.pdf", "layout": "plan"},
        )

    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_no_layouts_is_not_success(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "path": "",
            "count": 0,
            "pages": [],
            "message": "No layouts to print. Call layout_pack first.",
        }
        mock_get_conn.return_value = mock_conn

        result = export_pdf(ctx=None, path="/tmp/forsk-plan.pdf")
        assert result["success"] is False

    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_empty_detail_is_not_success(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "path": "",
            "count": 0,
            "pages": [],
            "message": "PDF detail is empty. The sheet does not show the drawing.",
        }
        mock_get_conn.return_value = mock_conn

        result = export_pdf(ctx=None, path="/tmp/forsk-plan.pdf")
        assert result["success"] is False
        assert result["message"] == "PDF detail is empty. The sheet does not show the drawing."

    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_blank_capture_is_not_success(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "path": "",
            "count": 0,
            "pages": [],
            "message": "capture failed after activate/Wait. Debug images: /tmp/forsk-print-page-1.png",
        }
        mock_get_conn.return_value = mock_conn

        result = export_pdf(ctx=None, path="/tmp/forsk-plan.pdf")
        assert result["success"] is False
        assert "capture failed after activate/Wait" in result["message"]
        assert "/tmp/forsk-print-page-1.png" in result["message"]

    @patch("rhinomcp.tools.export_pdf.get_rhino_connection")
    def test_partial_blank_still_writes(self, mock_get_conn):
        from rhinomcp.tools.export_pdf import export_pdf

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "path": "/tmp/forsk-plan.pdf",
            "count": 4,
            "pages": [
                "Forsk — North",
                "Forsk — East",
                "Forsk — South",
                "Forsk — West",
            ],
            "message": (
                "Wrote 4 page(s) to /tmp/forsk-plan.pdf. "
                "Blank preview: Forsk — Plan /tmp/forsk-print-page-1.png."
            ),
        }
        mock_get_conn.return_value = mock_conn

        result = export_pdf(ctx=None, path="/tmp/forsk-plan.pdf")
        assert result["success"] is True
        assert result["count"] == 4
        assert result["path"] == "/tmp/forsk-plan.pdf"
        assert "Forsk — Plan" in result["message"]
        assert "/tmp/forsk-print-page-1.png" in result["message"]


class TestClearLayoutsTool:
    @patch("rhinomcp.tools.clear_layouts.get_rhino_connection")
    def test_dry_run_and_views(self, mock_get_conn):
        from rhinomcp.tools.clear_layouts import clear_layouts

        mock_conn = MagicMock()
        mock_conn.send_command.return_value = {
            "deleted": ["Forsk — East"],
            "object_ids": [],
            "count": 1,
            "dry_run": True,
        }
        mock_get_conn.return_value = mock_conn

        result = clear_layouts(ctx=None, views=["east"], dry_run=True)
        assert result["success"] is True
        assert result["message"].startswith("Would delete")
        assert mock_conn.send_command.call_args[0][1] == {
            "dry_run": True,
            "views": ["east"],
        }

    @patch("rhinomcp.tools.clear_layouts.get_rhino_connection")
    def test_rejects_unknown_view(self, mock_get_conn):
        from rhinomcp.tools.clear_layouts import clear_layouts

        result = clear_layouts(ctx=None, views=["section"])
        assert result["success"] is False
        mock_get_conn.assert_not_called()


class TestPrintGuards:
    """Lock Layout/PDF engine choice. No live Rhino. No panel dialog."""

    def _text(self, *parts):
        from pathlib import Path

        root = Path(__file__).resolve().parents[2]
        return (root.joinpath(*parts)).read_text()

    def test_layout_uses_page_view_and_filepdf(self):
        src = self._text("plugin", "Functions", "LayoutPack.cs")
        assert "AddPageView" in src
        assert "AddDetailView" in src
        assert "FilePdf.Create" in src
        assert "new ViewCaptureSettings" in src
        assert "RasterMode = false" in src
        assert "pdf.Write" in src
        assert "ActiveSpace.PageSpace" in src
        assert "DisplayModeDescription.WireframeId" in src
        assert "BakeGreyscaleDrawing" in src
        assert "HiddenLineDrawing" in src
        assert "S-DRAW" in src
        assert "greyscale make2d" in src
        assert "Greyscale drawing" in src
        assert "plan_cut" in src
        assert "-Vector3d.ZAxis" in src
        assert "DefinedViewportProjection.Top" in src
        assert "TryAddPlanCut" not in src
        assert "Plan cut failed. The plan detail has no clipping plane." not in src
        aim = src[src.index("private static void AimDetailCamera"):src.index("private static void ApplyPaperDisplay")]
        assert "spec.Look" not in aim
        assert "-Vector3d.ZAxis" in aim
        assert "Vector3d.YAxis" in aim
        pan = src[src.index("private static void PanDetailOntoClay"):src.index("private static bool LockDetailScale")]
        assert "spec.Look" not in pan
        assert "-Vector3d.ZAxis" in pan
        assert "TechId" not in src
        assert "BlackAndWhite" in src
        assert "doc.Layers.Count" in src
        assert src.index("CommitViewportChanges") < src.index("SetScale")
        assert "Nothing to lay out. Bake walls first." in src
        assert "Layout detail is empty. The sheet does not show the drawing." in src
        assert "PDF detail is empty. The sheet does not show the drawing." in src
        pack = src[src.index('[McpCommand("layout_pack")]'):src.index('[McpCommand("export_pdf")]')]
        assert "EnsureForskPen" not in pack
        assert "PaintClayForPreview" not in pack
        assert "BakeGreyscaleDrawing" in pack
        prep = src[src.index("private void PrepareMacPage"):src.index("private string EnsureGreyscaleDrawings")]
        assert "EnsureForskPen" not in prep
        assert "Forsk Pen" not in prep
        assert "PaintClayForPreview" not in prep
        assert "SetDetailDrawingVisibility" in prep or "ApplyPageDrawingDisplay" in prep
        bake = self._text("plugin", "Functions", "Make2dView.cs")
        assert "HiddenLineDrawing.Compute" in bake
        assert "AddGeometryAndPlanes" in bake
        assert "KeepSectionSide" in bake
        assert "SilhouetteType.SectionCut" in bake
        assert "IsSceneSilhouette" in bake
        bake_fn = bake[bake.index("private GreyscaleDrawing BakeGreyscaleDrawing"):bake.index("private Layer EnsureDrawLayer")]
        assert "hldParams.AddClippingPlane" not in bake_fn
        assert "hldPlane.Flip()" in bake_fn
        assert "PlotWeightFromObject" in bake
        assert "ColorFromObject" in bake
        assert 'DrawParentName = "S-DRAW"' in bake
        assert "SectionFillLoops" in bake_fn
        assert "WorldToHiddenLine" in bake_fn
        fill_at = bake_fn.index("SectionFillLoops")
        assert "clip.HasValue" in bake_fn[max(0, fill_at - 180):fill_at]
        assert "PackDelta" in bake_fn
        assert "CreateContourCurves" in bake
        assert "Hatch.Create" in bake
        assert 'FindName("Solid")' in bake
        assert "section_fill" in bake
        assert "IsSkippedFillKind" in bake
        assert 'kind.Equals("floor"' in bake
        assert "SectionFillOrder = -1" in bake
        assert "SectionLineOrder = 1" in bake
        deleter = bake[bake.index("private static void DeletePrintDrawings"):bake.index("private static Layer FindDrawLayer")]
        assert "forsk:role" not in deleter
        assert "SetPageAsActive" in src
        assert "GetFrustumBoundingBox" in src
        assert "SetPerViewportPlotWeight" in src
        assert "SetPerViewportVisible" in src
        assert "RunningOnOSX" in src
        assert "GetPreviewImage" in src
        assert "DrawBitmap" in src
        assert "forsk-print.log" in src
        assert "capture failed after activate/Wait" in src
        assert "forsk-print-page-" in src
        assert "MacPreviewAttempts" in src
        assert "const int dense = 4" in src
        assert "bmp.Width / 40" not in src
        assert "RhinoApp.Wait()" in src
        assert "RhinoApp.Idle" in src
        mac = src[src.index("private JObject ExportMacPreviewPdf"):src.index("private static int CountDarkSamples")]
        assert "ViewCaptureSettings" not in mac
        assert mac.index("Redraw") < mac.index("GetPreviewImage")
        assert "SetPageAsActive" in mac
        assert mac.index("shots.Count == 0") < mac.index("pdf.Write")
        assert mac.index("pdf.Write") < mac.index("Blank preview:")
        assert "if (blanks.Count > 0)" not in mac
        assert "A-OPEN" in src
        assert 'Kind = "layout"' in src
        assert "Forsk — Plan" in src
        assert "SaveFileDialog" not in src
        assert "RunScript" not in src
        assert "sheet_pack" not in src

    def test_clear_generated_default_skips_layout_objects(self):
        import json
        import re

        clear = self._text("plugin", "Functions", "ClearGenerated.cs")
        match = re.search(r"new List<string>\s*\{([^}]+)\}", clear)
        assert match, "default kinds initializer missing"
        assert "layout" not in match.group(1)
        assert "drawing" not in match.group(1)
        schema = json.loads(self._text("contracts", "commands", "clear_generated.json"))
        assert "layout" not in schema["properties"]["kinds"]["default"]
        assert "AddPageView" not in clear
        assert "page.Close" not in clear

    def test_clear_layouts_removes_pages_and_greyscale(self):
        src = self._text("plugin", "Functions", "LayoutPack.cs")
        assert '[McpCommand("clear_layouts")]' in src
        remover = src[src.index(
            "private JObject RemoveLayoutPages(RhinoDoc doc, HashSet<string> viewSet"
        ):]
        assert "IsForskLayoutPage" in remover
        assert '"layout"' in remover
        assert "IsPrintDrawing" in remover
        assert "PlanCutRole" in remover
        assert '"wall"' not in remover
        assert '"drawing"' not in remover

