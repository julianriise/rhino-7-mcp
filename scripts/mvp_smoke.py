#!/usr/bin/env python3
"""Live MVP smoke against a running Rhino 7 plugin (mcpstart on 127.0.0.1:1999).

Does not go through the Python MCP server. Talks the same length-prefixed TCP
protocol the plugin expects, so it works even if Grok is not connected.

Order (from the MVP brief):
  1. get_document_summary
  2. create_layer A-WALL
  3. create_object BOX 4000 x 6000 x 3000 at origin (corner at 0,0,0)
  4. get_object_info / analyze_objects
  5. capture_viewport (writes PNG under scripts/smoke_output/)
  6. undo once

Usage:
  python3 scripts/mvp_smoke.py
  RHINO_MCP_HOST=127.0.0.1 RHINO_MCP_PORT=1999 python3 scripts/mvp_smoke.py
"""

from __future__ import annotations

import base64
import json
import os
import socket
import sys
from pathlib import Path

HOST = os.getenv("RHINO_MCP_HOST", "127.0.0.1")
PORT = int(os.getenv("RHINO_MCP_PORT", "1999"))
TIMEOUT = float(os.getenv("RHINO_MCP_TIMEOUT", "30"))
FRAME_HEADER_SIZE = 4
MAX_FRAME_SIZE = 64 * 1024 * 1024
OUT_DIR = Path(__file__).resolve().parent / "smoke_output"


class SmokeError(RuntimeError):
    pass


def recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise SmokeError(
                f"connection closed mid-message ({len(buf)}/{n} bytes received)"
            )
        buf.extend(chunk)
    return bytes(buf)


def send_command(sock: socket.socket, cmd_type: str, params: dict | None = None) -> dict:
    payload = json.dumps({"type": cmd_type, "params": params or {}}).encode("utf-8")
    sock.sendall(len(payload).to_bytes(FRAME_HEADER_SIZE, "big") + payload)
    header = recv_exact(sock, FRAME_HEADER_SIZE)
    if header.startswith(b"{"):
        raise SmokeError(
            "plugin sent unframed JSON; this checkout expects length-prefixed frames"
        )
    length = int.from_bytes(header, "big")
    if length <= 0 or length > MAX_FRAME_SIZE:
        raise SmokeError(f"invalid frame length {length}")
    response = json.loads(recv_exact(sock, length).decode("utf-8"))
    if response.get("status") == "error":
        raise SmokeError(f"{cmd_type}: {response.get('message', 'unknown error')}")
    if response.get("status") != "success":
        raise SmokeError(f"{cmd_type}: unexpected response {response!r}")
    return response.get("result") or {}


def step(name: str, fn):
    print(f"==> {name}")
    result = fn()
    print(f"    ok  {summarize(name, result)}")
    return result


def summarize(name: str, result: dict) -> str:
    if name == "get_document_summary":
        meta = result.get("meta_data") or {}
        return (
            f"units={meta.get('units')} layers={result.get('layer_count')} "
            f"objects={result.get('object_count')}"
        )
    if name == "create_layer":
        return f"layer={result.get('name')} id={result.get('id')}"
    if name == "create_object":
        bbox = result.get("bounding_box") or {}
        return f"id={result.get('id')} type={result.get('type')} bbox={bbox}"
    if name == "get_object_info":
        return f"id={result.get('id')} type={result.get('type')} bbox={result.get('bounding_box')}"
    if name == "analyze_objects":
        return f"count={result.get('object_count')}"
    if name == "capture_viewport":
        return (
            f"{result.get('viewport_name')} "
            f"{result.get('width')}x{result.get('height')} png={result.get('_png_path')}"
        )
    if name == "undo":
        return result.get("message", str(result))
    return str({k: result[k] for k in list(result)[:6]})


def main() -> int:
    print(f"connecting to {HOST}:{PORT} ...")
    try:
        sock = socket.create_connection((HOST, PORT), timeout=TIMEOUT)
    except OSError as exc:
        print(
            f"could not connect to Rhino at {HOST}:{PORT}: {exc}\n"
            "start Rhino 7, load rhinomcp.rhp, run mcpstart, then retry.",
            file=sys.stderr,
        )
        return 2
    sock.settimeout(TIMEOUT)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    failures: list[str] = []

    try:
        step("get_document_summary", lambda: send_command(sock, "get_document_summary"))

        step(
            "create_layer",
            lambda: send_command(sock, "create_layer", {"name": "A-WALL"}),
        )
        # Put the box on A-WALL so the smoke also exercises layer switch.
        send_command(sock, "get_or_set_current_layer", {"name": "A-WALL"})

        created = step(
            "create_object",
            lambda: send_command(
                sock,
                "create_object",
                {
                    "type": "BOX",
                    "name": "mvp-box",
                    "params": {"width": 4000, "length": 6000, "height": 3000},
                    # BOX is authored centered on the origin. Shift so a corner
                    # sits at (0,0,0), matching the later tilbygg convention.
                    "translation": [2000, 3000, 1500],
                },
            ),
        )
        object_id = created.get("id")
        if not object_id:
            raise SmokeError("create_object did not return an id")

        step(
            "get_object_info",
            lambda: send_command(sock, "get_object_info", {"id": object_id}),
        )
        step(
            "analyze_objects",
            lambda: send_command(sock, "analyze_objects", {"id": object_id}),
        )

        def capture():
            result = send_command(
                sock,
                "capture_viewport",
                {
                    "viewport": "perspective",
                    "width": 800,
                    "height": 600,
                    "zoom_to_fit": True,
                },
            )
            png_path = OUT_DIR / "capture_viewport.png"
            png_path.write_bytes(base64.b64decode(result["image_data"]))
            result["_png_path"] = str(png_path)
            return result

        step("capture_viewport", capture)
        step("undo", lambda: send_command(sock, "undo", {"steps": 1}))
    except SmokeError as exc:
        failures.append(str(exc))
        print(f"    FAIL  {exc}", file=sys.stderr)
    finally:
        sock.close()

    if failures:
        print("smoke FAILED")
        return 1
    print("smoke OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
