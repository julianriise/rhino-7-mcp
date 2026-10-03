#!/usr/bin/env python3
"""R3 smoke: export the garage's sheet set as DXF and as DWG, then check it.

Run after the garage smoke has built the garage (forsk scripts/smoke_garage.sh
calls it). Writes /tmp/forsk-export-garage/Garage DXF/ and .../Garage DWG/,
checks the plan DXF with dxf_check.py (ezdxf) and every DWG's header.
Stdout at most 25 lines; exit 0 when everything passes.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/export_smoke.py
Install first: python -m pip install ezdxf
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import sys
from pathlib import Path

import dxf_check

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))
ROOT = Path("/tmp/forsk-export-garage")
PLAN_NUMBER = "A-20-001"


def plan_file(names: list[str]) -> str | None:
    """The plan sheet among the files: its number is in the name."""
    return next((n for n in names if PLAN_NUMBER in n), None)


def run(send, root: Path) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines: list[str] = []
    ok = True
    for fmt in ("dxf", "dwg"):
        folder = root / f"Garage {fmt.upper()}"
        if folder.exists():
            shutil.rmtree(folder)
        result = send("export_sheets", {"folder": str(folder), "format": fmt})
        files = list(result.get("files") or [])
        lines.append(
            f"{fmt}: {result.get('count', 0)} files · writer {result.get('writer') or '-'}"
            + (f" · misc roles {','.join(result.get('misc_roles') or [])}" if result.get("misc") else "")
        )
        if not files or not str(result.get("message", "")).startswith("Exported"):
            lines.append(f"FAIL {fmt}: {result.get('message')}")
            ok = False
            continue
        if result.get("misc"):
            lines.append(f"FAIL {fmt}: roles with no export layer: {result.get('misc_roles')}")
            ok = False
        plan = plan_file(files)
        if plan is None:
            lines.append(f"FAIL {fmt}: no {PLAN_NUMBER} sheet among {len(files)} files")
            ok = False
        checked = [plan] if fmt == "dxf" and plan else files if fmt == "dwg" else []
        for name in checked:
            problems = dxf_check.check_file(folder / name, PLAN_NUMBER)
            if problems:
                ok = False
                lines.append(f"FAIL {name}: " + "; ".join(problems))
        lines.append(f"  {folder}/")
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
        lines, ok = run(send, ROOT)
    except OSError as e:
        lines, ok = [f"FAIL Rhino not reachable on {HOST}:{PORT}: {e}"], False
    for line in lines:
        print(line)
    print("export smoke: " + ("PASS" if ok else "FAIL"))
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
