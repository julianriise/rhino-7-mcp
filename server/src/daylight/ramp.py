# -*- coding: utf-8 -*-
"""Cool→warm sky-vis ramp. Same stops as apps/demo/lib/daylight/score.ts."""

from __future__ import annotations

from typing import NamedTuple

DAYLIGHT_LEGEND_DISCLAIMER = "sky-visibility proxy — not illuminance / EN 17037"


class RampStop(NamedTuple):
    t: float
    r: int
    g: int
    b: int


STOPS: tuple[RampStop, ...] = (
    RampStop(0.0, 8, 42, 82),
    RampStop(0.32, 16, 118, 128),
    RampStop(0.68, 232, 154, 28),
    RampStop(1.0, 255, 248, 230),
)


def _clamp01(value: float) -> float:
    if value <= 0:
        return 0.0
    if value >= 1:
        return 1.0
    return float(value)


def score_to_rgb(score: float) -> tuple[int, int, int]:
    t = _clamp01(score)
    i = 0
    while i < len(STOPS) - 2 and t > STOPS[i + 1].t:
        i += 1
    a = STOPS[i]
    b = STOPS[i + 1]
    span = (b.t - a.t) or 1.0
    u = (t - a.t) / span
    return (
        round(a.r + (b.r - a.r) * u),
        round(a.g + (b.g - a.g) * u),
        round(a.b + (b.b - a.b) * u),
    )


def score_to_css(score: float) -> str:
    r, g, b = score_to_rgb(score)
    return f"rgb({r},{g},{b})"


def daylight_ramp_css() -> str:
    stops = ", ".join(score_to_css(stop.t) for stop in STOPS)
    return f"linear-gradient(90deg, {stops})"
