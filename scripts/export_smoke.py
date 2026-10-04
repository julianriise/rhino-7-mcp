#!/usr/bin/env python3
"""R3/R4/D5 smoke: add a detail of a garage door and its wall, export the
sheet set as DXF and as DWG, and the model as IFC, then check them.

Run after the garage smoke has built the garage (forsk scripts/smoke_garage.sh
calls it). Writes /tmp/forsk-export-garage/Garage DXF/, .../Garage DWG/ and
.../Garage.ifc; checks every DXF for paper-size text and the A3 sheet
(dxf_check.py), the plan for its layers, every elevation for facade lines,
the detail sheets (A-50-…) for their cut weight, a 200 value and their
number, every DWG's header, and the IFC with ifc_check.py (ifcopenshell),
which also proves the IFC library loads in Rhino. The details it added are
removed again. Stdout at most 25 lines; exit 0 when all pass.

Usage:
  RHINO_MCP_TIMEOUT=300 python3 scripts/export_smoke.py
Install first: python -m pip install ezdxf ifcopenshell
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import sys
from pathlib import Path

import dxf_check
import ifc_check
import plan_smoke

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "300"))
ROOT = Path("/tmp/forsk-export-garage")
PLAN_NUMBER = "A-20-001"
DETAIL_PREFIX = " A-50-"


def plan_file(names: list[str]) -> str | None:
    """The plan sheet among the files: its number is in the name."""
    return next((n for n in names if PLAN_NUMBER in n), None)


def add_details(send) -> tuple[list[str], list[str]]:
    """A detail of door D01 (else the lowest mark) and of its host wall.
    The opening goes by its marker's forsk:id, else the marker's object id,
    as the plugin names a picked one. Returns the report lines and the ids
    it added, to remove at the end."""
    doors = sorted((
        m for m in plan_smoke.opening_markers(send)
        if (m.get("attributes") or {}).get("forsk:opening_kind") == "door"
        and (m.get("attributes") or {}).get("forsk:host_id")
        and ((m.get("attributes") or {}).get("forsk:id") or m.get("id"))
    ), key=lambda m: (m["attributes"].get("forsk:mark") != "D01", str(m["attributes"].get("forsk:mark") or "~")))
    if not doors:
        return ["FAIL details: no door with a host wall"], []
    attrs = doors[0]["attributes"]
    refs = [{"wall": attrs["forsk:host_id"]}, {"opening": attrs.get("forsk:id") or doors[0]["id"]}]
    before = {row.get("id") for row in send("details", {"action": "list"}).get("details") or []}
    result = send("details", {"action": "add", "refs": refs})
    added = [row.get("id") for row in result.get("details") or [] if row.get("id") not in before]
    line = f"details: {len(added)} added ({refs[0]['wall']}, {refs[1]['opening']})"
    if not result.get("details"):
        return [line, f"FAIL details: {result.get('message')}"], added
    return [line], added


def run(send, root: Path, check_ifc=ifc_check.check_path) -> tuple[list[str], bool]:
    """send(cmd, params) -> result. Returns the report lines and whether all passed."""
    lines, added = add_details(send)
    ok = not any(line.startswith("FAIL") for line in lines)
    try:
        ok = _export(send, root, check_ifc, lines) and ok
    finally:
        if added:
            send("details", {"action": "remove", "ids": added})
    return lines[:24], ok


def _export(send, root: Path, check_ifc, lines: list[str]) -> bool:
    ok = True
    for fmt in ("dxf", "dwg"):
        folder = root / f"Garage {fmt.upper()}"
        if folder.exists():
            shutil.rmtree(folder)
        result = send("export_sheets", {"folder": str(folder), "format": fmt})
        files = list(result.get("files") or [])
        lines.append(
            f"{fmt}: {result.get('count', 0)} files · writer {result.get('writer') or '-'}"
            + f" · {result.get('acad_version') or '-'}"
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
        if fmt == "dxf":
            elevations = [name for name in files if " A-40-" in f" {name}"]
            if not elevations:
                lines.append("FAIL dxf: no elevation sheet (A-40-…)")
                ok = False
            for name in elevations:
                doc, why = dxf_check.read_dxf(folder / name)
                problems = [why] if why else dxf_check.check_elevation(doc)
                if problems:
                    ok = False
                    lines.append(f"FAIL {name}: " + "; ".join(problems))
            details = [name for name in files if DETAIL_PREFIX in f" {name}"]
            if not details:
                lines.append("FAIL dxf: no detail sheet (A-50-…)")
                ok = False
            for name in details:
                doc, why = dxf_check.read_dxf(folder / name)
                problems = [why] if why else dxf_check.check_detail(doc, dxf_check.sheet_number(name))
                if problems:
                    ok = False
                    lines.append(f"FAIL {name}: " + "; ".join(problems))
            # Front sheet and schedule are neither the plan, A-40 nor A-50. Same text scale.
            rest = [name for name in files if name != plan and name not in elevations and name not in details]
            for name in rest:
                doc, why = dxf_check.read_dxf(folder / name)
                problems = [why] if why else dxf_check.check_fit(doc)
                if problems:
                    ok = False
                    lines.append(f"FAIL {name}: " + "; ".join(problems))
        lines.append(f"  {folder}/")

    path = root / "Garage.ifc"
    if path.exists():
        path.unlink()
    result = send("export_ifc", {"path": str(path)})
    lines.append(
        f"ifc: {result.get('walls', 0)} walls, {result.get('doors', 0)} doors, "
        f"{result.get('windows', 0)} windows, {result.get('spaces', 0)} spaces"
    )
    if not result.get("path") or not path.is_file():
        lines.append(f"FAIL ifc: {result.get('message')}")
        ok = False
    else:
        problems = check_ifc(path)
        for problem in problems[:5]:
            lines.append(f"FAIL ifc: {problem}")
        ok = ok and not problems
        lines.append(f"  {path}")
    return ok


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
