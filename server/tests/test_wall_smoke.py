"""Headless checks for the F3.1 step in scripts/garage_opening_smoke.py, against
a fake Rhino that serves the garage ring and one door on its south wall. A
right move out and back, with the move onto the door refused, passes; a door
left behind, a path that does not come back, and a refusal that goes through
fail."""

import json
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import garage_opening_smoke as smoke  # noqa: E402

HOST, DOOR = "host-1", "door-1"
REFUSAL = "Not moved: door-01 would sit in the moved wall or past the end of its own."


class FakeRhino:
    """The south wall's two faces and the door, moved by move_wall."""

    def __init__(self, carry=True, drift=0.0, refuse=True):
        self.offset = 0.0
        self.door_y = 0.0
        self.carry, self.drift, self.refuse = carry, drift, refuse

    def path(self):
        s = self.offset
        return json.dumps({
            "outer": [[0, s], [8000, s], [8000, 4000], [0, 4000]],
            "holes": [[[200, 200 + s], [7800, 200 + s], [7800, 3800], [200, 3800]]],
        })

    def __call__(self, sock, cmd, params=None):
        params = params or {}
        if cmd == "get_object_info" and params["id"] == DOOR:
            y = self.door_y
            return {"status": "success", "result": {"bounding_box": [[1200, 75 + y, 0], [2100, 125 + y, 2100]]}}
        if cmd == "get_object_info" and params["id"] == HOST:
            attrs = {"forsk:id": "w01", "forsk:thickness": "200", "forsk:path": self.path()}
            return {"status": "success", "result": {"attributes": attrs}}
        if cmd == "move_wall" and params["side"] == "south":
            before = [self.offset, self.offset + 200]
            step = -500 if params["toward"] == "south" else 500
            self.offset += step + (self.drift if step > 0 else 0)
            if self.carry:
                self.door_y += step
            return {"status": "success", "result": {
                "host_id": HOST,
                "faces_before": before,
                "faces_after": [self.offset, self.offset + 200],
                "openings_moved": [DOOR] if self.carry else [],
                "host_openings": 1,
                "host_voids": 1,
                "message": "Moved the south wall of w01 500 mm.",
            }}
        if cmd == "move_wall" and self.refuse:
            return {"status": "error", "message": REFUSAL}
        if cmd == "move_wall":
            return {"status": "success", "result": {"host_id": HOST}}
        raise AssertionError(f"unexpected {cmd} {params}")


def run(monkeypatch, capsys, fake):
    monkeypatch.setattr(smoke, "send_raw", fake)
    monkeypatch.setattr(smoke, "send_command", lambda sock, cmd, params=None: fake(sock, cmd, params)["result"])
    failures = []
    smoke.wall_move(None, HOST, DOOR, "w01", "200", failures)
    return failures, capsys.readouterr().out


def test_out_and_back_with_the_door_and_a_refusal_passes(monkeypatch, capsys):
    failures, out = run(monkeypatch, capsys, FakeRhino())
    assert failures == []
    assert "==> F3.1 wall: move the door's wall out 500 mm and back" in out
    assert "door 500.0" in out and "door 0.0" in out
    assert f"refused west 1650 status=error {REFUSAL} path same" in out


def test_a_door_left_behind_fails(monkeypatch, capsys):
    failures, _ = run(monkeypatch, capsys, FakeRhino(carry=False))
    assert "wall out carried [], expected the door" in failures
    assert "wall out door shift 0.0 expected 500.0" in failures


def test_a_path_that_does_not_come_back_fails(monkeypatch, capsys):
    failures, _ = run(monkeypatch, capsys, FakeRhino(drift=10))
    assert "wall back: the path is not the one it started from" in failures


def test_a_move_onto_the_door_that_goes_through_fails(monkeypatch, capsys):
    failures, _ = run(monkeypatch, capsys, FakeRhino(refuse=False))
    assert "wall onto the door status=success ''" in failures
