"""Headless checks for scripts/pdf_check.py layer_paths / furniture_paths:
the paths inside each `/Layer BMC ... EMC` run SheetPdf writes, counted with
the stdlib only, so the furniture smokes see whether the symbols printed (the
1.1.0-1.3.0 PDF dropped every furniture insert and nothing noticed)."""

import sys
import zlib
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import pdf_check  # noqa: E402


def flate_pdf(tmp_path, content: bytes) -> Path:
    """A one-stream PDF body the way SheetPdf writes its page content."""
    packed = zlib.compress(content)
    data = (b"%%PDF-1.4\n4 0 obj\n<< /Length %d /Filter /FlateDecode >>\nstream\n" % len(packed)
            + packed + b"\nendstream\nendobj\n%EOF\n")
    path = tmp_path / "sheet.pdf"
    path.write_bytes(data)
    return path


def test_paths_are_counted_per_marked_layer(tmp_path):
    content = (b"0 J 0 j\n/A-WALL BMC\n1.417 w\n0 0 m\n10 0 l\nS\nEMC\n"
               b"/A-FURN BMC\n0.369 w\n0 0 m\n5 0 l\n5 5 l\nS\n1 1 m\n2 2 l\nS\nEMC\n"
               b"/A-FURN-FIXD BMC\n0 0 m\n3 0 l\nS\nEMC\n"
               b"9 9 m\n9 10 l\nS\n"
               b"BT\n/F1 5 Tf\n(Bad 12 m l) Tj\nET\n")
    counts = pdf_check.layer_paths(flate_pdf(tmp_path, content))
    assert counts == {"A-WALL": 2, "A-FURN": 5, "A-FURN-FIXD": 2}
    assert pdf_check.furniture_paths(tmp_path / "sheet.pdf") == 7


def test_a_plan_without_furniture_marks_counts_none(tmp_path):
    path = flate_pdf(tmp_path, b"/A-WALL BMC\n0 0 m\n10 0 l\nS\nEMC\n0 0 m\n1 1 l\nS\n")
    assert pdf_check.furniture_paths(path) == 0
