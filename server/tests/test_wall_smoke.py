"""Headless checks for the wall steps in scripts/garage_opening_smoke.py, against
fake Rhinos. F3.1 serves the garage ring and one door on its south wall: a
right move out and back, with the move onto the door refused, passes; a door
left behind, a path that does not come back, and a refusal that goes through
fail. F3.2/F3.3 (--walls) serves the ring and the five south openings: the
partition, the south wall's delete, the split and join refusals and the free
wall pass; a split that goes through and a free wall that joins fail."""

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


# F3.2/F3.3: the --walls step against a fake that keeps the record's path and
# the five openings on the garage's south wall, as the garage smoke leaves them.
RING = {"outer": [[0, 0], [8000, 0], [8000, 4000], [0, 4000]], "holes": [[[200, 200], [7800, 200], [7800, 3800], [200, 3800]]]}
U = {"outer": [[0, 0], [8000, 0], [8000, 3800], [7800, 3800], [7800, 200], [200, 200], [200, 3800], [0, 3800]]}
SPLIT = "Not deleted: the walls left would stand in 2 separate pieces, and this version keeps one wall record."
JOIN = "Not added: the wall would join 2 separate walls into one, and this version keeps each wall record as it is."


class FakeWalls:
    def __init__(self, split=True, free_joins=False):
        self.paths = {HOST: RING}
        self.markers = [(f"m{i}", x, w) for i, (x, w) in enumerate([(1200, 900), (2300, 900), (3400, 1200), (4800, 900), (7280, 900)])]
        self.split, self.free_joins = split, free_joins

    def ok(self, result):
        return {"status": "success", "result": result}

    def __call__(self, sock, cmd, params=None):
        params = params or {}
        if cmd == "get_objects" and params["layer_filter"] == "A-WALL":
            return self.ok({"objects": [{"id": key, "attributes": {"forsk:kind": "wall"}} for key in self.paths]})
        if cmd == "get_objects" and params["layer_filter"] == "A-OPEN":
            rows = [{"id": mid, "bounding_box": [[x - w / 2, 75, 0], [x + w / 2, 125, 2100]],
                     "attributes": {"forsk:kind": "opening_marker", "forsk:host": HOST}} for mid, x, w in self.markers]
            return self.ok({"objects": rows})
        if cmd == "get_object_info":
            return self.ok({"attributes": {"forsk:path": json.dumps(self.paths[params["id"]])}})
        if cmd == "add_wall" and params["from"][1] == 450:
            x = params["from"][0]
            self.paths[HOST] = {**RING, "holes": [[[200, 200], [x - 50, 200], [x - 50, 3800], [200, 3800]],
                                                  [[x + 50, 200], [7800, 200], [7800, 3800], [x + 50, 3800]]]}
            return self.ok({"joined": True, "host_id": HOST, "from": [x, 200.0], "to": [x, 3800.0], "holes": 2,
                            "host_openings": 5, "host_voids": 5, "message": "Added a 100 mm wall to w01."})
        if cmd == "add_wall" and params["from"] == [2000, 6000]:
            self.paths["free"] = {"outer": [[2000, 5900], [6000, 5900], [6000, 6100], [2000, 6100]]}
            return self.ok({"joined": self.free_joins, "host_id": "free", "forsk_id": "w02", "message": "Added w02."})
        if cmd == "add_wall":
            return {"status": "error", "message": JOIN}
        if cmd == "delete_wall" and "at" in params and params["id"] == HOST:
            self.paths[HOST] = RING
            return self.ok({"record_deleted": False, "openings_deleted": [], "holes": 1, "message": "Deleted the wall."})
        if cmd == "delete_wall" and params.get("side") == "south":
            gone = [mid for mid, _, _ in self.markers]
            self.markers, self.paths[HOST] = [], U
            return self.ok({"record_deleted": False, "openings_deleted": gone, "host_openings": 0, "host_voids": 0,
                            "holes": 0, "message": "Deleted the south wall of w01, and its 5 doors."})
        if cmd == "delete_wall" and params.get("side") == "north":
            if self.split:
                return {"status": "error", "message": SPLIT}
            self.paths[HOST] = {"outer": [[0, 0], [200, 0], [200, 3800], [0, 3800]]}
            return self.ok({"record_deleted": False, "openings_deleted": [], "holes": 0, "message": "Deleted."})
        if cmd == "delete_wall" and params["id"] == "free":
            del self.paths["free"]
            return self.ok({"record_deleted": True, "openings_deleted": [], "holes": 0, "message": "Deleted w02."})
        raise AssertionError(f"unexpected {cmd} {params}")


def run_walls(monkeypatch, capsys, fake):
    monkeypatch.setattr(smoke, "send_raw", fake)
    monkeypatch.setattr(smoke, "send_command", lambda sock, cmd, params=None: fake(sock, cmd, params)["result"])
    failures = []
    smoke.walls_step(None, failures)
    return failures, capsys.readouterr().out


def test_walls_add_delete_and_refusals_pass(monkeypatch, capsys):
    failures, out = run_walls(monkeypatch, capsys, FakeWalls())
    assert failures == []
    # 4000, 3000, 5000 and 2000 sit on openings; 6000 is clear.
    assert "partition at x 6000:" in out
    assert (
        "    walls summary partition holes 2->1, south deleted 5 openings 0/0 holes 0, "
        "split refused, w02 free added and deleted, join refused\n"
    ) in out


def test_a_split_that_goes_through_fails(monkeypatch, capsys):
    failures, out = run_walls(monkeypatch, capsys, FakeWalls(split=False))
    assert "walls split went through path changed" in failures
    assert "split not refused" in out


def test_a_free_wall_that_joins_fails(monkeypatch, capsys):
    failures, _ = run_walls(monkeypatch, capsys, FakeWalls(free_joins=True))
    assert "walls free add joined=True id w02, expected a new w02" in failures
