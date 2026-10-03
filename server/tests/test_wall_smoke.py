"""Headless checks for the wall steps in scripts/garage_opening_smoke.py, against
fake Rhinos. F3.1 serves the garage's south run and one door: a right move
out and back, with the move onto the door refused, passes; a door left
behind, a path that does not come back, and a refusal that goes through
fail. F3.2/F3.3 (--walls) serves four runs and the five south openings: the
partition as its own record, the south run deleted with its openings, the
free wall, and the wall joining it to the north run pass; a free wall that
joins, and a joining wall that is refused, fail."""

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
        if cmd == "move_wall" and "id" not in params:
            return {"status": "error", "message": "Click one wall, then say it again."}
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
                "message": "Moved the south wall of w01 500 mm. 1 room updated.",
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


# F3.2/F3.3: four butting runs. The five openings sit on the south run.
SOUTH, NORTH, WEST, EAST = "south-wall", "north-wall", "west-wall", "east-wall"
RUNS = {
    SOUTH: ("w01", [[0, 0], [8000, 0], [8000, 200], [0, 200]]),
    NORTH: ("w02", [[0, 3800], [8000, 3800], [8000, 4000], [0, 4000]]),
    WEST: ("w03", [[0, 200], [200, 200], [200, 3800], [0, 3800]]),
    EAST: ("w04", [[7800, 200], [8000, 200], [8000, 3800], [7800, 3800]]),
}
JOIN = "Not added: the wall would join 2 separate walls into one, and this version keeps each wall record as it is."


class FakeWalls:
    def __init__(self, free_joins=False, link_refused=False):
        self.paths = {key: {"outer": ring} for key, (_, ring) in RUNS.items()}
        self.ids = {key: fid for key, (fid, _) in RUNS.items()}
        self.markers = [(f"m{i}", x, w) for i, (x, w) in enumerate([(1200, 900), (2300, 900), (3400, 1200), (4800, 900), (7280, 900)])]
        self.free_joins, self.link_refused = free_joins, link_refused

    def ok(self, result):
        return {"status": "success", "result": result}

    def walls(self):
        return [{"id": key, "attributes": {"forsk:kind": "wall", "forsk:id": self.ids[key]}} for key in self.paths]

    def __call__(self, sock, cmd, params=None):
        params = params or {}
        if cmd == "get_objects" and params["layer_filter"] == "A-WALL":
            return self.ok({"objects": self.walls()})
        if cmd == "get_objects" and params["layer_filter"] == "A-OPEN":
            rows = [{"id": mid, "bounding_box": [[x - w / 2, 75, 0], [x + w / 2, 125, 2100]],
                     "attributes": {"forsk:kind": "opening_marker", "forsk:host": SOUTH}} for mid, x, w in self.markers]
            return self.ok({"objects": rows})
        if cmd == "get_object_info":
            return self.ok({"attributes": {"forsk:id": self.ids[params["id"]], "forsk:path": json.dumps(self.paths[params["id"]])}})
        if cmd == "add_wall" and params["from"][1] == 450:
            x = params["from"][0]
            self.paths["part"] = {"outer": [[x - 50, 200], [x + 50, 200], [x + 50, 3800], [x - 50, 3800]]}
            self.ids["part"] = "w05"
            return self.ok({"joined": False, "host_id": "part", "forsk_id": "w05", "joins": ["w01", "w02"],
                            "from": [x, 200.0], "to": [x, 3800.0], "holes": 0,
                            "message": "Added w05, a 100 mm wall joined to w01 and w02. 1 room updated."})
        if cmd == "add_wall" and params["from"] == [2000, 6000]:
            self.paths["free"] = {"outer": [[2000, 5900], [6000, 5900], [6000, 6100], [2000, 6100]]}
            self.ids["free"] = "w05"
            return self.ok({"joined": self.free_joins, "host_id": "free", "forsk_id": "w05", "message": "Added w05."})
        if cmd == "add_wall" and not self.link_refused:
            self.paths["link"] = {"outer": [[3900, 4000], [4100, 4000], [4100, 5900], [3900, 5900]]}
            self.ids["link"] = "w06"
            return self.ok({"joined": False, "joins": ["w02", "w05"], "host_id": "link", "forsk_id": "w06",
                            "message": "Added w06, a 200 mm wall joined to w02 and w05."})
        if cmd == "add_wall":
            return {"status": "error", "message": JOIN}
        if cmd == "delete_wall" and params["id"] == "part":
            del self.paths["part"]
            return self.ok({"record_deleted": True, "openings_deleted": [], "holes": 0, "message": "Deleted w05."})
        if cmd == "delete_wall" and params.get("side") == "south":
            gone = [mid for mid, _, _ in self.markers]
            self.markers = []
            del self.paths[SOUTH]
            return self.ok({"record_deleted": True, "openings_deleted": gone, "holes": 0,
                            "message": "Deleted the south wall, w01, and its 5 doors. Roof updated."})
        if cmd == "delete_wall" and params["id"] == "link":
            del self.paths["link"]
            return self.ok({"record_deleted": True, "openings_deleted": [], "holes": 0, "message": "Deleted w06."})
        if cmd == "delete_wall" and params["id"] == "free":
            del self.paths["free"]
            return self.ok({"record_deleted": True, "openings_deleted": [], "holes": 0, "message": "Deleted w05."})
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
        "    walls summary partition own record holes 0, south deleted 5 openings record gone, "
        "w05 free added and deleted, w06 joined both and deleted\n"
    ) in out


def test_a_free_wall_that_joins_fails(monkeypatch, capsys):
    failures, _ = run_walls(monkeypatch, capsys, FakeWalls(free_joins=True))
    assert "walls free add joined=True id w05, expected a new w05" in failures


def test_a_joining_wall_that_is_refused_fails(monkeypatch, capsys):
    failures, _ = run_walls(monkeypatch, capsys, FakeWalls(link_refused=True))
    assert f"walls join add: {JOIN}" in failures
