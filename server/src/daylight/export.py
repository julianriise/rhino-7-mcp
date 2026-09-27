# -*- coding: utf-8 -*-
"""SVG/PNG from a frozen 200 scores buffer."""

from __future__ import annotations

import math
import re
import struct
import zlib
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal
from xml.sax.saxutils import escape

from daylight.models import GridMeta
from daylight.ramp import DAYLIGHT_LEGEND_DISCLAIMER, STOPS, score_to_rgb

_PLAN_ID = re.compile(r"[^A-Za-z0-9._-]+")
_LEGEND_H = 28
_CELL_PX = 8
_STRIP_H = 8


@dataclass(frozen=True)
class DaylightExport:
    filename: str
    media_type: str
    body: bytes


def daylight_export_filename(plan_id: str, fmt: Literal["svg", "png"]) -> str:
    token = _PLAN_ID.sub("-", plan_id.strip()) if plan_id else ""
    token = token.strip("-._") or "plan"
    return f"daylight-{token}.{fmt}"


def synthetic_skyvis_scores(rows: int, cols: int) -> list[float]:
    if rows < 1 or cols < 1:
        raise ValueError("grid rows and cols must be >= 1")
    n = rows * cols
    if n == 1:
        return [0.5]
    return [i / (n - 1) for i in range(n)]


def _scores_row_major(scores: Sequence[float], grid: GridMeta) -> list[float]:
    expected = grid.rows * grid.cols
    if expected <= 0:
        raise ValueError("grid rows and cols must be >= 1")
    if len(scores) != expected:
        raise ValueError(f"scores length {len(scores)} != rows*cols {expected}")
    out: list[float] = []
    for value in scores:
        if not math.isfinite(value):
            raise ValueError("scores must be finite 0–1 values")
        out.append(float(value))
    return out


def _png_chunk(tag: bytes, data: bytes) -> bytes:
    crc = zlib.crc32(tag + data) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)


def _rgb_png(width: int, height: int, rgb: bytes) -> bytes:
    raw = bytearray()
    stride = width * 3
    for y in range(height):
        raw.append(0)
        raw.extend(rgb[y * stride : (y + 1) * stride])
    return (
        b"\x89PNG\r\n\x1a\n"
        + _png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + _png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _png_chunk(b"IEND", b"")
    )


def export_daylight_svg(
    scores: Sequence[float],
    grid: GridMeta,
    plan_id: str,
) -> DaylightExport:
    values = _scores_row_major(scores, grid)
    width = grid.cols * _CELL_PX
    height = grid.rows * _CELL_PX + _LEGEND_H
    cells: list[str] = []
    for row in range(grid.rows):
        for col in range(grid.cols):
            r, g, b = score_to_rgb(values[row * grid.cols + col])
            cells.append(
                f'<rect x="{col * _CELL_PX}" y="{row * _CELL_PX}" '
                f'width="{_CELL_PX}" height="{_CELL_PX}" fill="rgb({r},{g},{b})"/>'
            )
    stops = "".join(
        f'<stop offset="{stop.t * 100:g}%" stop-color="rgb({stop.r},{stop.g},{stop.b})"/>'
        for stop in STOPS
    )
    gy = grid.rows * _CELL_PX + 6
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" '
        f'viewBox="0 0 {width} {height}">'
        f'<g id="scores">{"".join(cells)}</g>'
        f'<g id="legend" data-slot="daylight-legend">'
        f"<defs><linearGradient id=\"daylight-ramp\" x1=\"0\" x2=\"1\">{stops}</linearGradient></defs>"
        f'<text x="4" y="{gy + 10}" font-size="9" fill="#6b7280">Low</text>'
        f'<rect x="28" y="{gy + 2}" width="{max(width - 64, 32)}" height="8" '
        f'rx="4" fill="url(#daylight-ramp)"/>'
        f'<text x="{width - 4}" y="{gy + 10}" font-size="9" fill="#6b7280" text-anchor="end">High</text>'
        f'<text x="4" y="{gy + 22}" font-size="8" fill="#6b7280">{escape(DAYLIGHT_LEGEND_DISCLAIMER)}</text>'
        f"</g></svg>"
    )
    return DaylightExport(
        filename=daylight_export_filename(plan_id, "svg"),
        media_type="image/svg+xml",
        body=svg.encode("utf-8"),
    )


def export_daylight_png(
    scores: Sequence[float],
    grid: GridMeta,
    plan_id: str,
) -> DaylightExport:
    values = _scores_row_major(scores, grid)
    width = grid.cols * _CELL_PX
    height = grid.rows * _CELL_PX + _LEGEND_H
    rgb = bytearray(width * height * 3)
    for row in range(grid.rows):
        for col in range(grid.cols):
            color = score_to_rgb(values[row * grid.cols + col])
            for dy in range(_CELL_PX):
                for dx in range(_CELL_PX):
                    px = (row * _CELL_PX + dy) * width + (col * _CELL_PX + dx)
                    rgb[px * 3 : px * 3 + 3] = color
    strip_y = grid.rows * _CELL_PX + 6
    for x in range(width):
        t = x / (width - 1) if width > 1 else 0.0
        color = score_to_rgb(t)
        for dy in range(_STRIP_H):
            y = strip_y + dy
            if y >= height:
                break
            px = y * width + x
            rgb[px * 3 : px * 3 + 3] = color
    return DaylightExport(
        filename=daylight_export_filename(plan_id, "png"),
        media_type="image/png",
        body=_rgb_png(width, height, bytes(rgb)),
    )
