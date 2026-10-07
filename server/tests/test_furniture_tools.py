"""add_furniture, move_furniture and delete_furniture: what reaches Rhino, and what is refused before it."""

from unittest.mock import MagicMock, patch


def _conn(mock_get_conn, reply=None):
    conn = MagicMock()
    conn.send_command.return_value = reply or {"message": "ok"}
    mock_get_conn.return_value = conn
    return conn


@patch("rhinomcp.tools.add_furniture.get_rhino_connection")
def test_add_sends_the_item_and_only_what_was_given(mock_get_conn):
    from rhinomcp.tools.add_furniture import add_furniture

    conn = _conn(mock_get_conn, {"forsk_id": "F01", "message": "Added a double bed 160 to the bedroom, against the east wall."})
    result = add_furniture(ctx=None, item=" double bed ", room="R02")
    conn.send_command.assert_called_once_with("add_furniture", {"item": "double bed", "room": "R02"})
    assert result["success"] is True and result["forsk_id"] == "F01"
    add_furniture(ctx=None, item="dining table", seats=6, at=[4000, 3000], rotation=90)
    conn.send_command.assert_called_with("add_furniture", {"item": "dining table", "seats": 6, "at": [4000, 3000], "rotation": 90})


@patch("rhinomcp.tools.add_furniture.get_rhino_connection")
def test_add_refuses_bad_input_before_rhino(mock_get_conn):
    from rhinomcp.tools.add_furniture import add_furniture

    assert add_furniture(ctx=None, item="")["success"] is False
    assert add_furniture(ctx=None, item="bed", width=-1)["success"] is False
    assert add_furniture(ctx=None, item="sofa", seats=0)["success"] is False
    assert add_furniture(ctx=None, item="sofa", seats=True)["success"] is False
    assert add_furniture(ctx=None, item="bed", at=[1])["success"] is False
    assert add_furniture(ctx=None, item="bed", rotation=90)["success"] is False
    mock_get_conn.assert_not_called()


@patch("rhinomcp.tools.move_furniture.get_rhino_connection")
def test_move_sends_where_and_needs_something(mock_get_conn):
    from rhinomcp.tools.move_furniture import move_furniture

    conn = _conn(mock_get_conn)
    assert move_furniture(ctx=None, id="F01")["success"] is False
    assert move_furniture(ctx=None, by=[1, 2, 3])["success"] is False
    conn.send_command.assert_not_called()
    move_furniture(ctx=None, id="F01", by=[-500, 0], rotate=90)
    conn.send_command.assert_called_once_with("move_furniture", {"id": "F01", "by": [-500, 0], "rotate": 90})


@patch("rhinomcp.tools.delete_furniture.get_rhino_connection")
def test_delete_sends_the_target(mock_get_conn):
    from rhinomcp.tools.delete_furniture import delete_furniture

    conn = _conn(mock_get_conn, {"deleted": ["g"], "count": 1, "message": "Removed the sofa."})
    assert delete_furniture(ctx=None)["success"] is True
    conn.send_command.assert_called_with("delete_furniture", {})
    delete_furniture(ctx=None, room="kitchen")
    conn.send_command.assert_called_with("delete_furniture", {"room": "kitchen"})
    delete_furniture(ctx=None, all=True)
    conn.send_command.assert_called_with("delete_furniture", {"all": True})


@patch("rhinomcp.tools.furnish_room.get_rhino_connection")
def test_furnish_sends_the_room_and_refuses_bad_words(mock_get_conn):
    from rhinomcp.tools.furnish_room import furnish_room

    conn = _conn(mock_get_conn, {"rooms": [], "count": 0, "message": "ok"})
    assert furnish_room(ctx=None, density="tight")["success"] is False
    assert furnish_room(ctx=None, variant="random")["success"] is False
    conn.send_command.assert_not_called()
    furnish_room(ctx=None, room="all", density="compact", variant="creative", replace=True)
    conn.send_command.assert_called_once_with(
        "furnish_room", {"room": "all", "density": "compact", "variant": "creative", "replace": True})


@patch("rhinomcp.tools.furnish_room.get_rhino_connection")
def test_furnish_preview_is_sent(mock_get_conn):
    from rhinomcp.tools.furnish_room import furnish_room

    conn = _conn(mock_get_conn, {"preview": True, "count": 0, "message": "ok"})
    furnish_room(ctx=None, room="R02", preview=True)
    conn.send_command.assert_called_once_with("furnish_room", {"room": "R02", "preview": True})


@patch("rhinomcp.tools.show_view.get_rhino_connection")
def test_show_view_sends_the_view(mock_get_conn):
    from rhinomcp.tools.show_view import show_view

    conn = _conn(mock_get_conn, {"view": "south", "message": "ok"})
    show_view(ctx=None, view="South")
    conn.send_command.assert_called_with("show_view", {"view": "south"})
    assert show_view(ctx=None, view="section_a")["success"] is False


@patch("rhinomcp.tools.jump_inside.get_rhino_connection")
def test_jump_inside_sends_the_room(mock_get_conn):
    from rhinomcp.tools.jump_inside import jump_inside

    conn = _conn(mock_get_conn, {"view": "Interior: Bedroom", "message": "ok"})
    jump_inside(ctx=None)
    conn.send_command.assert_called_with("jump_inside", {})
    jump_inside(ctx=None, room="bedroom")
    conn.send_command.assert_called_with("jump_inside", {"room": "bedroom"})
