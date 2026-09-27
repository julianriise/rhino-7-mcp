# -*- coding: utf-8 -*-
"""Map raw visible-sky angle onto [0, 1]. 1:1 with apps/demo/lib/daylight/score.ts normalize."""

from __future__ import annotations

import numpy as np

from daylight.geom import clamp
from daylight.trace import REF_ANGLE


def normalize_daylight_scores(raw: np.ndarray) -> np.ndarray:
    """Map raw visible-sky angle onto [0, 1]. 90° of sky saturates."""
    out = np.empty(raw.shape, dtype=np.float32)
    denom = REF_ANGLE if REF_ANGLE > 0 else 1.0
    for i in range(raw.size):
        out[i] = np.float32(clamp(float(raw[i]) / denom, 0.0, 1.0))
    return out
