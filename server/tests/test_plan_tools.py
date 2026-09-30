"""The plan tools: plan_import and plan_scale validate their arguments and pass the command on."""

from __future__ import annotations

from unittest.mock import MagicMock, patch


class TestPlanTools:
    """The MCP tools validate their arguments and pass the command on as the contract has it."""

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_sends_only_what_was_given(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        conn = MagicMock()
        conn.send_command.return_value = {"walls": 5, "message": "Imported 5 walls."}
        mock_get_conn.return_value = conn
        result = plan_import(ctx=None, image_path="/tmp/plan.png", plan_path="/tmp/plan.json")
        conn.send_command.assert_called_once_with(
            "plan_import", {"image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json"})
        assert result == {"success": True, "walls": 5, "message": "Imported 5 walls."}

        plan_import(ctx=None, image_path="/tmp/plan.png", plan_path="/tmp/plan.json",
                    scale_hint="1:100", image_dpi=200, image_width_mm=42012, replace=True)
        assert conn.send_command.call_args[0][1] == {
            "image_path": "/tmp/plan.png", "plan_path": "/tmp/plan.json",
            "scale_hint": "1:100", "image_dpi": 200, "image_width_mm": 42012, "replace": True,
        }

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_needs_absolute_paths(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        assert plan_import(ctx=None, image_path="plan.png", plan_path="/tmp/plan.json") == {
            "success": False, "message": "plan_import needs an absolute image_path, or a pdf_path."}
        assert plan_import(ctx=None, image_path="/tmp/plan.png") == {
            "success": False, "message": "plan_import needs an absolute plan_path, or a pdf_path."}
        assert plan_import(ctx=None, pdf_path="plan1.pdf") == {
            "success": False, "message": "plan_import needs an absolute pdf_path."}
        assert plan_import(ctx=None, pdf_path="/tmp/plan1.pdf", image_path="/tmp/plan.png")["message"] == (
            "plan_import takes pdf_path, or image_path with plan_path, not both.")
        assert plan_import(ctx=None, pdf_path="/tmp/plan1.pdf", page=0)["message"] == "plan_import page is 1-based."
        mock_get_conn.assert_not_called()

    @patch("rhinomcp.tools.plan_import.get_rhino_connection")
    def test_plan_import_passes_a_pdf_and_its_page_on(self, mock_get_conn):
        from rhinomcp.tools.plan_import import plan_import

        conn = MagicMock()
        conn.send_command.return_value = {"walls": 46, "pdf": {"file": "plan1.pdf", "page": 1}}
        mock_get_conn.return_value = conn
        plan_import(ctx=None, pdf_path="/tmp/plan1.pdf")
        conn.send_command.assert_called_once_with("plan_import", {"pdf_path": "/tmp/plan1.pdf"})
        plan_import(ctx=None, pdf_path=" /tmp/plan1.pdf ", page=2, replace=True)
        assert conn.send_command.call_args[0][1] == {"pdf_path": "/tmp/plan1.pdf", "page": 2, "replace": True}

    @patch("rhinomcp.tools.plan_scale.get_rhino_connection")
    def test_plan_scale_measures_without_a_length_and_sets_with_one(self, mock_get_conn):
        from rhinomcp.tools.plan_scale import plan_scale

        conn = MagicMock()
        conn.send_command.return_value = {"measured_mm": 3900.0, "factor": 1.0}
        mock_get_conn.return_value = conn
        plan_scale(ctx=None, p1=[1000, -2000], p2=[4900, -2000])
        conn.send_command.assert_called_once_with(
            "plan_scale", {"p1": [1000, -2000], "p2": [4900, -2000], "frame": "model"})

        plan_scale(ctx=None, p1=[1000, -2000], p2=[4900, -2000], length_mm=4000, frame="source")
        assert conn.send_command.call_args[0][1] == {
            "p1": [1000, -2000], "p2": [4900, -2000], "frame": "source", "length_mm": 4000}

    @patch("rhinomcp.tools.plan_scale.get_rhino_connection")
    def test_plan_scale_refuses_bad_arguments(self, mock_get_conn):
        from rhinomcp.tools.plan_scale import plan_scale

        assert plan_scale(ctx=None, p1=[0, 0])["message"] == "plan_scale needs p1 and p2 as [x, y] in mm."
        assert plan_scale(ctx=None, p1=[0, 0], p2=[1, 0], frame="page")["message"] == "plan_scale frame must be model or source."
        assert plan_scale(ctx=None, p1=[0, 0], p2=[1, 0], length_mm=0)["message"] == "plan_scale length_mm must be above 0."
        mock_get_conn.assert_not_called()
