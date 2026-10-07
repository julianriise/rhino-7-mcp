#!/usr/bin/env python3
"""AN.5 and AN.6 smoke: save the garage as option A, move a wall, compare A with now.

Run on the garage after the garage smoke has built and saved it. save_option
writes A beside the 3dm (forsk.option.v1, the JSON reads back), the south
wall moves out 300 mm, compare_option A says a wall moved and the net area
grew. The wall goes back and the option folder is removed.
Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/option_smoke.py
"""

from __future__ import annotations

import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analysis_smoke  # noqa: E402  (the socket send and the south wall lookup)


def run(send) -> tuple[list[str], bool]:
    lines: list[str] = []
    ok = True

    def fail(why: str) -> None:
        nonlocal ok
        lines.append("FAIL " + why)
        ok = False

    saved = send("save_option", {})
    path = str(saved.get("path") or "")
    lines.append(f"save: {saved.get('message')}")
    if saved.get("name") != "A" or not os.path.exists(path):
        fail(f"no option A file: {saved}")
        return lines, ok
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        if data.get("schema") != "forsk.option.v1" or len(data.get("walls", [])) != saved.get("walls"):
            fail(f"the JSON does not read back: schema {data.get('schema')} walls {len(data.get('walls', []))}")
        wall = str(send("execute_rhinoscript_python_code", {"code": analysis_smoke.SOUTH_WALL}).get("output") or "").strip()
        send("move_wall", {"id": wall, "side": "south", "toward": "south", "distance_mm": 300})
        try:
            compared = send("compare_option", {"name": "A"})
            lines.append(f"compare: {compared.get('message')}")
            rows = {r["label"]: r for r in compared.get("rows", [])}
            for label in ("Net area", "Doors", "Windows"):
                row = rows.get(label)
                if row:
                    lines.append(f"  {label}: A {row['option']} · now {row['now']}")
            if "moved" not in str(compared.get("summary")) and "changed" not in str(compared.get("summary")):
                fail("the compare does not see the wall move")
            net = rows.get("Net area")
            if not net or float(net["now"].split()[0]) <= float(net["option"].split()[0]):
                fail(f"the net area did not grow: {net}")
        finally:
            send("move_wall", {"id": wall, "side": "south", "toward": "north", "distance_mm": 300})
    finally:
        shutil.rmtree(os.path.dirname(os.path.dirname(path)), ignore_errors=True)
    return lines[:24], ok


def main() -> int:
    try:
        lines, ok = run(_send)
    except OSError as e:
        lines, ok = [f"FAIL Rhino not reachable: {e}"], False
    for line in lines:
        print(line)
    print("option smoke: " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def _send(cmd: str, params: dict) -> dict:
    import socket

    with socket.create_connection((analysis_smoke.HOST, analysis_smoke.PORT), timeout=analysis_smoke.TIMEOUT) as sock:
        payload = json.dumps({"type": cmd, "params": params}).encode()
        sock.sendall(len(payload).to_bytes(4, "big") + payload)
        length = int.from_bytes(analysis_smoke._recv(sock, 4), "big")
        response = json.loads(analysis_smoke._recv(sock, length).decode())
    if response.get("status") == "error":
        return {"message": response.get("message")}
    return response.get("result") or {}


if __name__ == "__main__":
    sys.exit(main())
