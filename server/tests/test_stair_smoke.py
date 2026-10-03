"""Headless checks for scripts/stair_smoke.py (R5) against a fake Rhino:
a right run passes, and a receipt with a stair id, a plan with no stair
symbol or a wrong re-plan fails."""

import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import stair_smoke  # noqa: E402


class FakeRhino:
    def __init__(self, **overrides):
        self.overrides = overrides
        self.calls = []
        self.state = {"id": "g", "forsk_id": "S01", "risers": 19, "riser": 3400 / 19, "width": 900, "rise_auto": True}

    def __call__(self, cmd, params):
        self.calls.append((cmd, params))
        if cmd in self.overrides:
            return self.overrides[cmd](params)
        s = self.state
        if cmd == "add_stair":
            return {**s, "message": "Added a straight stair along the north wall, 19 steps of 179."}
        if cmd == "edit_stair":
            if "width" in params:
                s["width"] = params["width"]
            if params.get("riser_max") == 170:
                s.update(risers=20, riser=170)
            if params.get("rise") == 2750:
                s.update(risers=16, riser=171.875, rise_auto=False)
            if params.get("rise") == "auto":
                s.update(risers=19, riser=3400 / 19, rise_auto=True)
            verb = "Flipped the stair" if params.get("flip") else "Changed the stair:"
            return {**s, "message": f"{verb} {s['risers']} steps of {round(s['riser'])}."}
        if cmd == "layout_pack":
            return {"pages": [{"view": "plan", "stairs": 1, "symbol_dashed": 30}], "count": 1, "message": "1 page"}
        if cmd == "export_ifc":
            Path(params["path"]).write_text("ISO-10303-21;")
            return {"path": params["path"], "stairs": 1, "message": "✓ Exported IFC · 1 stair · x.ifc"}
        if cmd == "delete_stair":
            return {"deleted": ["g"], "count": 1, "message": "Removed the stair."}
        raise AssertionError(cmd)


def run(fake, tmp_path):
    return stair_smoke.run(fake, tmp_path / "s.ifc", check_ifc=lambda path, expect: [])


def test_a_right_run_passes_and_ends_with_the_stair_gone(tmp_path):
    fake = FakeRhino()
    lines, ok = run(fake, tmp_path)
    assert ok, lines
    assert len(lines) <= 24
    assert [c for c, _ in fake.calls] == ["add_stair"] + ["edit_stair"] * 5 + ["layout_pack", "export_ifc", "delete_stair"]


def test_a_receipt_with_a_stair_id_fails(tmp_path):
    fake = FakeRhino(add_stair=lambda p: {"id": "g", "risers": 19, "rise_auto": True,
                                          "message": "Added a straight stair S01, 19 steps of 179."})
    lines, ok = run(fake, tmp_path)
    assert not ok
    assert any("stair id" in line for line in lines)


def test_a_plan_without_the_symbol_fails(tmp_path):
    fake = FakeRhino(layout_pack=lambda p: {"pages": [{"view": "plan", "stairs": 0}], "message": "1 page"})
    lines, ok = run(fake, tmp_path)
    assert not ok
    assert any(line.startswith("FAIL plan") for line in lines)


def test_a_wrong_replan_fails(tmp_path):
    fake = FakeRhino(edit_stair=lambda p: {"risers": 17, "riser": 161.76, "rise_auto": False, "width": 1000,
                                           "message": "Changed the stair: 17 steps of 162."})
    lines, ok = run(fake, tmp_path)
    assert not ok
    assert any(line.startswith("FAIL rise") for line in lines)


def test_no_stair_added_stops_early(tmp_path):
    fake = FakeRhino(add_stair=lambda p: {"message": "No walls yet. Generate the model, or pick two points for the stair."})
    lines, ok = run(fake, tmp_path)
    assert not ok
    assert [c for c, _ in fake.calls] == ["add_stair"]
