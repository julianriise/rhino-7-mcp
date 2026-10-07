"""The Package Manager (yak) manifest: the description is one plain line led by
the Food4Rhino tagline, so the Package Manager's Description box is never empty,
and the version is the release's."""

import re
from pathlib import Path

MANIFEST = Path(__file__).resolve().parents[2] / "plugin" / "manifest.yml"
TAGLINE = "The Rhino plugin for architects. Floor plan in, drawing set out, from one chat window."


def top_level():
    """Top-level `key: value` pairs as written, the way package_release.sh reads them."""
    fields = {}
    for line in MANIFEST.read_text(encoding="utf-8").splitlines():
        m = re.match(r"^([a-z]+):\s*(.*)$", line)
        if m:
            fields[m.group(1)] = m.group(2).strip()
    return fields


def unquote(value):
    return value[1:-1] if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"" else value


def test_description_is_one_inline_line_led_by_the_tagline():
    raw = top_level().get("description", "")
    assert raw not in ("", ">", "|", ">-", "|-"), "description must be an inline value, not empty or a block scalar"
    assert unquote(raw).startswith(TAGLINE)


def test_version_name_url_and_authors():
    fields = top_level()
    # The manifest and the assembly carry one version; a release bumps both.
    csproj = (MANIFEST.parent / "rhinomcp.csproj").read_text(encoding="utf-8")
    assert f"<Version>{fields['version']}</Version>" in csproj
    assert fields["name"] == "forsk"
    assert unquote(fields["url"]) == "https://forsk.app"
    assert "Julian Riise" in MANIFEST.read_text(encoding="utf-8")
