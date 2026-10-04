"""Headless checks for scripts/pdf_check.py (v3 N3): a vector sheet built
here from raw PDF bytes passes; the same sheet as one full-page picture (the
Mac's 150 dpi Print until N3) fails "mostly image"; lines with no text fail
"no selectable text". The golden sheet SheetPdf wrote passes with its Info."""

import sys
from pathlib import Path

import pytest

pytest.importorskip("pypdf")

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))

import pdf_check  # noqa: E402

GOLDEN = Path(__file__).resolve().parent / "fixtures" / "sheet_vector_sample.pdf"
A3 = (1190.55, 841.89)


def pdf_bytes(content: bytes, *, image=False, info=b"") -> bytes:
    """One A3 page with content, Helvetica as F1, and an 8 x 8 grey image as Im0."""
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %.2f %.2f] /Resources << /Font << /F1 5 0 R >>%s >> /Contents 4 0 R >>"
        % (A3[0], A3[1], b" /XObject << /Im0 6 0 R >>" if image else b""),
        b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    if image:
        pixels = bytes([128]) * 64
        objects.append(b"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 64 >>\nstream\n"
                       + pixels + b"\nendstream")
    if info:
        objects.append(info)
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    trailer = b"<< /Size %d /Root 1 0 R" % (len(objects) + 1)
    if info:
        trailer += b" /Info %d 0 R" % len(objects)
    out += b"trailer\n" + trailer + b" >>\nstartxref\n%d\n%%%%EOF\n" % xref
    return bytes(out)


LINES = b"".join(b"%d 40 m %d 400 l S\n" % (40 + 10 * i, 40 + 10 * i) for i in range(30))
TEXT = b"BT /F1 9 Tf 900 40 Td (A-20-001 Garage) Tj ET\n"
PICTURE = b"q 1190.55 0 0 841.89 0 0 cm /Im0 Do Q\n"


def write(tmp_path, name, data):
    path = tmp_path / name
    path.write_bytes(data)
    return path


def test_a_vector_sheet_passes(tmp_path):
    path = write(tmp_path, "vector.pdf", pdf_bytes(LINES + TEXT))
    assert pdf_check.check(path, ["Garage"]) == ["ok 1 page vector"]
    assert pdf_check.main([str(path), "--expect-text", "Garage"]) == 0


def test_one_full_page_image_fails_mostly_image(tmp_path):
    path = write(tmp_path, "raster.pdf", pdf_bytes(PICTURE, image=True))
    lines = pdf_check.check(path)
    assert len(lines) == 1 and lines[0].startswith("page 1: mostly image (100 %")
    assert pdf_check.main([str(path)]) == 1


def test_a_small_image_on_a_vector_sheet_still_passes(tmp_path):
    logo = b"q 100 0 0 50 1000 700 cm /Im0 Do Q\n"
    path = write(tmp_path, "logo.pdf", pdf_bytes(LINES + TEXT + logo, image=True))
    assert pdf_check.check(path) == ["ok 1 page vector"]


def test_lines_with_no_text_fail_no_selectable_text(tmp_path):
    path = write(tmp_path, "lines.pdf", pdf_bytes(LINES))
    assert pdf_check.check(path) == ["page 1: no selectable text"]


def test_too_few_paths_and_missing_text_and_meta_fail(tmp_path):
    path = write(tmp_path, "thin.pdf", pdf_bytes(b"0 0 m 10 10 l S\n" + TEXT))
    assert pdf_check.check(path) == ["page 1: 2 path operators, want 20"]
    assert pdf_check.check(path, min_paths=2) == ["ok 1 page vector"]
    assert pdf_check.check(path, ["Test house"], min_paths=2) == ["page 1: text misses 'Test house'"]
    assert pdf_check.check(path, expect_meta=True, min_paths=2) == ["meta: no Title", "meta: no Creator"]
    info = b"<< /Title (Garage drawings) /Creator (Forsk) >>"
    assert pdf_check.check(write(tmp_path, "meta.pdf", pdf_bytes(b"0 0 m 10 10 l S\n" + TEXT, info=info)),
                           expect_meta=True, min_paths=2) == ["ok 1 page vector"]


def test_a_missing_file_fails(tmp_path):
    assert pdf_check.main([str(tmp_path / "none.pdf")]) == 1


def test_the_golden_sheet_from_sheet_pdf_passes():
    """Written by SheetPdfTests with FORSK_WRITE_PDF_FIXTURE=1 from a synthetic flat A3 sheet."""
    assert GOLDEN.is_file(), GOLDEN
    assert pdf_check.check(GOLDEN, ["Garage"], expect_meta=True) == ["ok 1 page vector"]
