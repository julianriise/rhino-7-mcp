#!/usr/bin/env python3
"""Check that a Print PDF is vector, with pypdf.

Each page must be drawn with paths and real text, not one picture of the
sheet. A page fails when:

- an image is drawn over more than 25 % of the page, or the page's images
  together cover more than 25 % (from each image's `cm` placement);
- extract_text() finds no text, no sheet number (A-dd-ddd), or misses an
  --expect-text string;
- the text holds a "?": the PDF's Helvetica (WinAnsi) has no glyph for a
  character such as ≈ and writes "?" instead, and no sheet text asks a question;
- its content holds fewer than --min-paths path operators (m, l, c, re;
  default 20).

--expect-meta also needs the document Info's Title and Creator.

Output: at most one line per failing page, then `ok N pages vector` when
every page passes. Exit 0 or 1.

Usage:
  python3 scripts/pdf_check.py ~/Desktop/"Test house.pdf" --expect-meta --expect-text "Test house"
Install first: python3 -m pip install pypdf
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

IMAGE_SHARE = 0.25
SHEET_NO = re.compile(r"A-\d\d-\d\d\d")
PATH_OPS = {b"m", b"l", b"c", b"re"}


def _mul(m, n):
    """The product of two PDF matrices [a b c d e f]: m then n."""
    a, b, c, d, e, f = m
    a2, b2, c2, d2, e2, f2 = n
    return [
        a * a2 + b * c2, a * b2 + b * d2,
        c * a2 + d * c2, c * b2 + d * d2,
        e * a2 + f * c2 + e2, e * b2 + f * d2 + f2,
    ]


def _walk(content, resources, ctm, stats, depth=0):
    """Count path operators and the area each image is drawn over, through forms."""
    from pypdf.generic import ContentStream

    xobjects = {}
    if resources is not None and "/XObject" in resources:
        xobjects = resources["/XObject"].get_object()
    stack = []
    for operands, op in ContentStream(content, None).operations:
        if op in PATH_OPS:
            stats["paths"] += 1
        elif op == b"q":
            stack.append(ctm)
        elif op == b"Q":
            ctm = stack.pop() if stack else ctm
        elif op == b"cm" and len(operands) == 6:
            ctm = _mul([float(v) for v in operands], ctm)
        elif op == b"Do" and operands:
            obj = xobjects.get(operands[0])
            if obj is None:
                continue
            obj = obj.get_object()
            subtype = obj.get("/Subtype")
            if subtype == "/Image":
                stats["images"].append(abs(ctm[0] * ctm[3] - ctm[1] * ctm[2]))
            elif subtype == "/Form" and depth < 8:
                matrix = [float(v) for v in obj.get("/Matrix", [1, 0, 0, 1, 0, 0])]
                _walk(obj, obj.get("/Resources"), _mul(matrix, ctm), stats, depth + 1)
        elif op == b"BI":
            # An inline image: drawn on the unit square through the CTM.
            stats["images"].append(abs(ctm[0] * ctm[3] - ctm[1] * ctm[2]))


def check_page(page, number, expect_text, min_paths):
    """The failure line for one page, or None when it is vector with text."""
    box = page.mediabox
    area = abs(float(box.width) * float(box.height)) or 1.0
    stats = {"paths": 0, "images": []}
    contents = page.get_contents()
    if contents is not None:
        _walk(contents, page.get("/Resources"), [1, 0, 0, 1, 0, 0], stats)
    biggest = max(stats["images"], default=0.0) / area
    total = sum(stats["images"]) / area
    if biggest > IMAGE_SHARE or total > IMAGE_SHARE:
        return f"page {number}: mostly image ({round(100 * max(biggest, total))} % of the page)"
    text = (page.extract_text() or "").strip()
    if not text:
        return f"page {number}: no selectable text"
    if not SHEET_NO.search(text):
        return f"page {number}: no sheet number A-dd-ddd in the text"
    if "?" in text:
        line = next(l for l in text.splitlines() if "?" in l)
        return f"page {number}: a glyph printed as '?' ({line.strip()[:40]!r})"
    missing = [s for s in expect_text if s not in text]
    if missing:
        return f"page {number}: text misses {', '.join(repr(s) for s in missing)}"
    if stats["paths"] < min_paths:
        return f"page {number}: {stats['paths']} path operators, want {min_paths}"
    return None


def check(path, expect_text=(), expect_meta=False, min_paths=20):
    """The failure lines for the file; the last line is `ok N pages vector` when there are none."""
    from pypdf import PdfReader

    reader = PdfReader(str(path))
    lines = []
    if expect_meta:
        meta = reader.metadata or {}
        for key in ("/Title", "/Creator"):
            if not str(meta.get(key) or "").strip():
                lines.append(f"meta: no {key[1:]}")
    pages = len(reader.pages)
    if pages == 0:
        lines.append("no pages")
    for i, page in enumerate(reader.pages, start=1):
        line = check_page(page, i, list(expect_text), min_paths)
        if line:
            lines.append(line)
    if not lines:
        lines.append(f"ok {pages} {'page' if pages == 1 else 'pages'} vector")
    return lines


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Check that a Print PDF is vector with selectable text.")
    parser.add_argument("file")
    parser.add_argument("--expect-text", action="append", default=[])
    parser.add_argument("--expect-meta", action="store_true")
    parser.add_argument("--min-paths", type=int, default=20)
    args = parser.parse_args(argv)
    try:
        import pypdf  # noqa: F401
    except ImportError:
        print("pypdf is missing: python3 -m pip install pypdf")
        return 1
    if not Path(args.file).is_file():
        print(f"no file {args.file}")
        return 1
    try:
        lines = check(args.file, args.expect_text, args.expect_meta, args.min_paths)
    except Exception as e:  # a file pypdf cannot read is a failure, one line
        print(f"unreadable: {e}")
        return 1
    for line in lines:
        print(line)
    return 0 if lines[-1].startswith("ok ") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
