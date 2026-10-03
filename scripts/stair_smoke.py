#!/usr/bin/env python3
"""R5 smoke: a straight stair on the garage, edited, printed and exported.

Run after the garage smoke has built the garage. Adds a stair with nothing
given (it runs along the longest room wall), makes it 1000 wide, steps at
most 170, flips it, sets the rise to 2750 (16 risers of 171.875) and back
to auto, lays out the plan (its symbol must be on it), exports the model as
IFC (one IfcStair, checked with ifc_check.py), then deletes the stair so the
garage is as it was. Receipts must carry no stair id. Stdout at most 25
lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/stair_smoke.py
Install first: python -m pip install ifcopenshell
"""

from __future__ import annotations

import json
import os
import re
import socket
import sys
from pathlib import Path

import ifc_check

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))
IFC_PATH = Path("/tmp/forsk-stair-smoke.ifc")
STAIR_ID = re.compile(r"\bS\d{2,}\b")


def run(send, ifc_path: Path = IFC_PATH, check_ifc=ifc_check.check_path) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines: list[str] = []
    ok = True

    def step(label: str, cmd: str, params: dict, want) -> dict:
        nonlocal ok
        result = send(cmd, params)
        message = str(result.get("message") or "")
        lines.append(f"{label}: {message}")
        why = want(result)
        if STAIR_ID.search(message):
            why = (why + "; " if why else "") + "the receipt names a stair id"
        if why:
            lines.append(f"FAIL {label}: {why}")
            ok = False
        return result

    added = step("add", "add_stair", {}, lambda r: None if r.get("risers", 0) >= 2 and r.get("rise_auto")
                 and str(r.get("message", "")).startswith("Added a straight stair") else f"got {r}")
    if not added.get("id"):
        return lines[:24], False
    risers = added.get("risers", 0)
    step("width", "edit_stair", {"width": 1000}, lambda r: None if r.get("width") == 1000 else f"width {r.get('width')}")
    step("steps", "edit_stair", {"riser_max": 170},
         lambda r: None if r.get("riser", 999) <= 170 and r.get("risers", 0) >= risers else f"riser {r.get('riser')} risers {r.get('risers')}")
    step("flip", "edit_stair", {"flip": True}, lambda r: None if str(r.get("message", "")).startswith("Flipped") else "not flipped")
    step("rise", "edit_stair", {"rise": 2750, "riser_max": 180},
         lambda r: None if r.get("risers") == 16 and abs(r.get("riser", 0) - 171.875) < 1e-6 and not r.get("rise_auto") else f"got {r.get('risers')} × {r.get('riser')}")
    step("auto", "edit_stair", {"rise": "auto"}, lambda r: None if r.get("rise_auto") else "rise is not auto")

    pack = send("layout_pack", {"views": ["plan"]})
    plan = next((p for p in pack.get("pages") or [] if p.get("view") == "plan" or "Plan" in str(p.get("title", ""))), None)
    drawn = (plan or {}).get("stairs", 0)
    lines.append(f"plan: {drawn} stair symbol(s), {(plan or {}).get('symbol_dashed', 0)} dashed pieces")
    if drawn != 1:
        lines.append(f"FAIL plan: {pack.get('message')}")
        ok = False

    if ifc_path.exists():
        ifc_path.unlink()
    exported = send("export_ifc", {"path": str(ifc_path)})
    lines.append(f"ifc: {exported.get('stairs', 0)} stair(s) · {exported.get('message')}")
    if not exported.get("path") or not ifc_path.is_file():
        lines.append("FAIL ifc: nothing written")
        ok = False
    else:
        for problem in check_ifc(ifc_path, "stair")[:4]:
            lines.append(f"FAIL ifc: {problem}")
            ok = False
        lines.append(f"  {ifc_path}")

    step("delete", "delete_stair", {}, lambda r: None if r.get("count") == 1 else f"count {r.get('count')}")
    return lines[:24], ok


def main() -> int:
    def send(cmd: str, params: dict) -> dict:
        with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as sock:
            payload = json.dumps({"type": cmd, "params": params}).encode()
            sock.sendall(len(payload).to_bytes(4, "big") + payload)
            length = int.from_bytes(_recv(sock, 4), "big")
            response = json.loads(_recv(sock, length).decode())
        if response.get("status") == "error":
            return {"message": response.get("message")}
        return response.get("result") or {}

    try:
        lines, ok = run(send)
    except OSError as e:
        lines, ok = [f"FAIL Rhino not reachable on {HOST}:{PORT}: {e}"], False
    for line in lines:
        print(line)
    print("stair smoke: " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def _recv(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise OSError("connection closed")
        buf.extend(chunk)
    return bytes(buf)


if __name__ == "__main__":
    sys.exit(main())
