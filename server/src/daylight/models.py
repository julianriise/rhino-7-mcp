# -*- coding: utf-8 -*-
"""Frozen Instant Daylight v1 contract. Units: centimetres. No climate/orientation."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

DAYLIGHT_SCOPE = (
    "sky-vis proxy. Not EN 17037, not Radiance, not illuminance (lux), "
    "and not a permit or code document. Scores are relative 0–1 sky visibility "
    "on a 2D grid. Orientation and climate are out of scope for v1."
)

# Hard cap so a tiny cell size on a large floor cannot allocate a huge grid.
DEFAULT_CELL_SIZE_CM = 40.0
MAX_DAYLIGHT_CELLS = 250_000

PointCm = list[float]


class Wall(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = Field(..., min_length=1, description="Stable wall id; openings reference this")
    segments: list[PointCm] = Field(
        ...,
        min_length=2,
        description="Polyline in cm, at least two [x, y] vertices",
    )
    thickness: float = Field(..., gt=0, description="Wall thickness in centimetres")


class Opening(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str | None = Field(None, description="Optional opening id")
    kind: Literal["WINDOW", "DOOR"] = Field(..., description="WINDOW counts toward windowCount")
    wallRef: str = Field(..., min_length=1, description="Must match a wall.id")
    width: float = Field(..., gt=0, description="Opening width in centimetres, along the wall")
    t: float | None = Field(
        None,
        ge=0,
        le=1,
        description="Optional 0–1 parameter along the wall polyline (centre of the opening)",
    )


class Space(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str | None = Field(None, description="Optional space id")
    floor: list[PointCm] = Field(
        ...,
        description="Floor ring in cm, [x, y] vertices. Empty or <3 distinct points is invalid",
    )


class Column(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str | None = Field(None, description="Optional column id")
    center: PointCm = Field(..., min_length=2, max_length=2, description="Column centre [x, y] cm")
    size: PointCm = Field(
        ...,
        min_length=2,
        max_length=2,
        description="Axis-aligned width and height in cm",
    )


class GridSpec(BaseModel):
    model_config = ConfigDict(extra="forbid")

    cellSizeCm: float = Field(
        DEFAULT_CELL_SIZE_CM,
        gt=0,
        description="Grid pitch in centimetres. Default matches the demo TS engine (40 cm)",
    )


class DaylightRequest(BaseModel):
    """Scene in centimetres. Extra keys (orientation, climate, …) are rejected."""

    model_config = ConfigDict(extra="forbid")

    walls: list[Wall] = Field(default_factory=list)
    openings: list[Opening] = Field(default_factory=list)
    spaces: list[Space] = Field(default_factory=list, description="Occupiable floor rings")
    columns: list[Column] = Field(default_factory=list, description="Optional rectangular columns")
    grid: GridSpec = Field(default_factory=GridSpec)


class GridMeta(BaseModel):
    rows: int
    cols: int
    cellSizeCm: float
    originCm: list[float] = Field(..., min_length=2, max_length=2, description="[minX, minY] cm")
    maxCm: list[float] = Field(..., min_length=2, max_length=2, description="[maxX, maxY] cm")
    cellCount: int


class DaylightTiming(BaseModel):
    validateMs: float = Field(..., description="Request validation wall time")
    computeMs: float | None = Field(
        None,
        description="Trace wall time in milliseconds",
    )


class DaylightResponse(BaseModel):
    """Implemented compute payload (PR2). Scores are Float32 0–1, row-major."""

    scores: list[float] = Field(
        ...,
        description="Row-major Float32 scores in [0, 1]; index = row * cols + col",
    )
    windowCount: int
    grid: GridMeta
    timing: DaylightTiming
    scope: Literal["sky-vis-proxy"] = "sky-vis-proxy"


class DaylightValidated(BaseModel):
    """Kept for export hosts that still paint from a scores buffer without calling compute."""

    error: Literal["DAYLIGHT_COMPUTE_NOT_IMPLEMENTED"] = "DAYLIGHT_COMPUTE_NOT_IMPLEMENTED"
    message: str = (
        "Request is valid. Hosts that skip live compute can still paint export "
        "stubs from a scores buffer."
    )
    scope: str = DAYLIGHT_SCOPE
    windowCount: int
    grid: GridMeta
    timing: DaylightTiming
