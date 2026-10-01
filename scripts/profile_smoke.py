"""F5.4 smoke step: the print profiles, as chat and the panel set them
(print_profile), checked against the objects the sheets are drawn with.

For each shipped profile (default, grey, hatch) the step sets it, lays out the
plan and section A, and reads the S-DRAW objects Rhino stored:

- the poché hatch's object colour is the profile's poché colour, and its
  pattern (forsk:poche_pattern, the pattern the document really used) is the
  profile's, Solid or Hatch1, on the plan and on the section;
- the cut outline, the lines beyond, the symbols (thin) and the dashed roof
  outline carry the profile's colour for that tier.

The expected colours come from the profile the tool returns, so a sheet that
disagrees with what print_profile says fails; the step also pins that the
default is solid black everywhere and that grey and hatch differ from it. At
the end the document is back on the default, and the grey and hatch sheets are
exported (plan, section A) for a glance. Weights are not read back here: a
stroke's weight is its ribbon's width. PrintProfilesTests pin the pens, and
the 1000 px PNGs show the lines.

One summary line per run ("profile ..."), the rest in failures.
"""

from __future__ import annotations

from typing import Callable

import section_smoke

Send = Callable[[str, dict], dict]

NAMES = ("default", "grey", "hatch")
SAMPLES = 4


def _rgb(info: dict) -> str:
    color = info.get("color") or {}
    return f"{color.get('r')},{color.get('g')},{color.get('b')}"


def _sample(send: Send, rows: list, role: str, want: str, what: str, reasons: list) -> tuple[int, int]:
    """Up to SAMPLES objects of a role: how many carry the colour want, of how many checked."""
    found = [r for r in rows if section_smoke._attr(r).get("forsk:role") == role]
    checked = good = 0
    for row in found[:SAMPLES]:
        info = send("get_object_info", {"id": row.get("id")})
        checked += 1
        if _rgb(info) == want:
            good += 1
    if checked and good != checked:
        reasons.append(f"{what} {role} colour {_rgb(info)} expected {want} ({good}/{checked})")
    return good, checked


def run_step(send: Send, label: str, room: str, pdf_prefix: str, failures: list, panel: Send | None = None) -> None:
    """The three profiles on the plan and section A of the document. PDFs of grey and hatch at pdf_prefix-<name>.pdf."""
    reasons: list[str] = []

    # Chat's path: a profile request is the Sheets turn; print in a profile is the Print turn.
    if panel is not None:
        for text, expect in (
            ("use the grey profile", "sheets"),
            ("bytt til hatch profil", "sheets"),
            ("print with the grey profile", "print"),
        ):
            intent = panel("state", {"text": text}).get("intent")
            if intent != expect:
                reasons.append(f"intent {text!r}={intent} expected {expect}")

    send("section_clear", {})
    added = send("section_add", {"letter": "A", "room": room, "axis": "long"})
    if (added.get("section") or {}).get("view") != "section_a":
        reasons.append(f"section_add A {added.get('message')}")

    unknown = send_expect_error(send, "print_profile", {"name": "neon"})
    if not unknown:
        reasons.append("print_profile accepted the name neon")

    parts: list[str] = []
    poche_seen: dict[str, str] = {}
    pdf_pages = []
    for name in NAMES:
        done = send("print_profile", {"name": name})
        profile = done.get("profile") or {}
        if profile.get("name") != name:
            reasons.append(f"print_profile {name} answered {profile.get('name')}")
            continue
        poche = profile.get("poche") or {}
        poche_seen[name] = f"{poche.get('rgb')} {poche.get('pattern')}"
        send("clear_layouts", {})
        packed = send("layout_pack", {"views": ["plan", "section_a"], "replace": True})
        pages = {str(p.get("view")): p for p in packed.get("pages") or []}
        for view in ("plan", "section_a"):
            if view not in pages:
                reasons.append(f"{name} page {view} missing: {packed.get('message')}")

        tiers = [0, 0]  # good, checked
        for layer, what, roles in (
            ("S-DRAW::Plan", "plan", (
                ("cut", profile["cut"]["rgb"]),
                ("greyscale", profile["beyond"]["rgb"]),
                ("symbol", profile["thin"]["rgb"]),
                ("roof_outline", profile["dashed"]),
            )),
            ("S-DRAW::Section A", "section", (
                ("cut", profile["cut"]["rgb"]),
                ("beyond", profile["beyond"]["rgb"]),
            )),
        ):
            rows = section_smoke._rows(send, layer)
            fills = [r for r in rows if section_smoke._attr(r).get("forsk:role") == "section_fill"]
            if not fills:
                reasons.append(f"{name} {what} has no poché")
            else:
                patterns = {str(section_smoke._attr(r).get("forsk:poche_pattern")) for r in fills}
                if patterns != {poche.get("pattern")}:
                    reasons.append(f"{name} {what} poché pattern {sorted(patterns)} expected {poche.get('pattern')}")
                good, checked = _sample(send, fills, "section_fill", poche.get("rgb"), f"{name} {what}", reasons)
                tiers[0] += good
                tiers[1] += checked
            for role, want in roles:
                good, checked = _sample(send, rows, role, want, f"{name} {what}", reasons)
                tiers[0] += good
                tiers[1] += checked
        parts.append(f"{name} {tiers[0]}/{tiers[1]} poché {poche_seen[name]}")

        if name != "default":
            pdf = f"{pdf_prefix}-{name}.pdf"
            out = send("export_pdf", {"path": pdf})
            written = {str(p) for p in out.get("pages") or []}
            wanted = [str(p.get("page")) for p in packed.get("pages") or []]
            pdf_pages.append(f"{len(written)}/{len(wanted)}")
            if set(wanted) - written:
                reasons.append(f"{name} pdf missing {sorted(set(wanted) - written)}: {out.get('message')}")

    # The look must differ, or the profile does nothing: grey and hatch against the default.
    for name in NAMES[1:]:
        if poche_seen.get(name) == poche_seen.get("default"):
            reasons.append(f"{name} poché looks like the default: {poche_seen.get(name)}")
    if poche_seen.get("default") != "0,0,0 Solid":
        reasons.append(f"default poché {poche_seen.get('default')} expected 0,0,0 Solid")

    # Put the document back: the default is stored as no choice at all.
    back = send("print_profile", {"name": "default"})
    restored = (back.get("profile") or {}).get("name") == "default"
    if not restored:
        reasons.append(f"print_profile default answered {(back.get('profile') or {}).get('name')}")
    read = send("print_profile", {})
    if (read.get("profile") or {}).get("name") != "default" or read.get("changed"):
        reasons.append("reading the profile after reset did not give the default")

    print(f"    profile {label} " + "; ".join(parts) + f"; restored {'default' if restored else 'no'}; pdf "
          + " ".join(pdf_pages or ["0/0"]))
    for reason in reasons:
        failures.append(f"{label} profile: {reason}")


def send_expect_error(send: Send, cmd: str, params: dict) -> bool:
    """True when the command is refused (the smoke's send raises on an error reply)."""
    try:
        send(cmd, params)
    except Exception:
        return True
    return False
