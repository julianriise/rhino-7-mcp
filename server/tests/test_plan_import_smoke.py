"""Headless checks for scripts/plan_import_smoke.py: how it tells that the plan image is drawn."""

import struct
import sys
import zlib
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import plan_import_smoke  # noqa: E402


def png(rows: list[list[tuple[int, int, int, int]]]) -> bytes:
    """An 8-bit RGBA PNG of the given pixel rows, every row stored unfiltered."""
    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    raw = b"".join(b"\x00" + bytes(value for pixel in row for value in pixel) for row in rows)
    header = struct.pack(">IIBBBBB", len(rows[0]), len(rows), 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


WHITE = (255, 255, 255, 255)
GREY = (128, 128, 128, 255)


def test_same_view_with_nothing_on_the_layer_changes_no_row():
    shot = png([[WHITE] * 4 for _ in range(3)])
    assert plan_import_smoke.underlay_rows(shot, shot) == (0, 3)


def test_rows_the_plan_image_inks_change_when_its_layer_goes_off():
    blank = [[WHITE] * 4 for _ in range(3)]
    inked = [[WHITE] * 4, [WHITE, GREY, GREY, WHITE], [WHITE] * 4]
    assert plan_import_smoke.underlay_rows(png(inked), png(blank)) == (1, 3)


def test_two_views_of_different_size_are_not_compared():
    with pytest.raises(plan_import_smoke.SmokeError):
        plan_import_smoke.underlay_rows(png([[WHITE] * 4] * 3), png([[WHITE] * 4] * 2))


def test_a_file_that_is_no_png_is_refused():
    with pytest.raises(plan_import_smoke.SmokeError):
        plan_import_smoke.png_rows(b"not a picture")
