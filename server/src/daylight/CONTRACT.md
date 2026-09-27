# Instant Daylight v1 contract

`POST /v1/daylight` — scene in **centimetres**.

## Scope (honest)

This is a **sky-vis proxy**: relative 0–1 scores on a 2D grid. It is **not**
EN 17037, Radiance, illuminance (lux), or a permit / code document.
**Orientation and climate are out of scope for v1** (`extra=forbid` on the
request model).

Compute is a 1:1 port of `apps/demo/lib/daylight` (portals / trace / score /
geom). Direct sky visibility only. Inner openings transmit sky they can see.
Solid walls still occlude. There is no wall bounce.

## Request

| Field | Meaning |
| --- | --- |
| `walls[]` | `id`, `segments` (`[x,y]` polyline, ≥2 points), `thickness` cm |
| `openings[]` | `kind`: `WINDOW` \| `DOOR`, `wallRef` + `width` cm, optional `t` 0–1 |
| `spaces[]` | `floor` ring in cm |
| `columns[]` | optional; `center` + `size` (axis-aligned cm) |
| `grid.cellSizeCm` | default 40 (same as the demo TS engine) |

JSON Schema: [`schema.json`](schema.json) (generated from the Pydantic models).
OpenAPI: `GET /openapi.json` → `paths["/v1/daylight"]`.

## Success — HTTP 200

```
scores        Float32 0–1, row-major, index = row * cols + col
windowCount   number of WINDOW openings on the request
grid          rows, cols, cellSizeCm, originCm, maxCm, cellCount
timing        validateMs, computeMs
scope         "sky-vis-proxy"
```

Grid cells match the demo `createGrid`: cell centres, usable mask (floor minus
wall thickness and columns). Unusable cells score 0.

## Errors

| Status | Case |
| --- | --- |
| 422 | missing `wallRef`; opening wider than wall; zero-length segment; empty / degenerate floor ring; extra keys (orientation/climate) |
| 413 | implied grid cells > 250_000 |

## Fixtures

Flynn edge cases live in [`../fixtures/daylight/`](../fixtures/daylight/).
A fixture `comment` is stripped before POST (`extra=forbid` would reject it).

| File | Expected |
| --- | --- |
| `zero-windows.json` | 200, `windowCount` 0, all scores 0 |
| `outer-only.json` | 200, `windowCount` 1, bright near the north facade, falloff with distance |
| `inner-window-only.json` | 200, `windowCount` 1, all scores 0 (no outer sky source) |
| `columns.json` | 200, `windowCount` 1, column footprint unusable / score 0 |
| `bleed.json` | 200, `windowCount` 2. Tim QA: outer window + solid partition + inner opening. No wall-bleed through the solid partition span. Inner room (y < 400) lights only through the inner opening. Outer room brighter near the north facade. |
| `opening-wider-than-wall.json`, `missing-wall-ref.json`, `zero-length-segments.json`, `empty-floor-ring.json` | 422 |
| `extras-orientation.json`, `extras-climate.json` | 422 (`extra=forbid`) |
| `missing-spaces.json`, `empty-spaces.json` | 422 (at least one floor ring) |
| `duplicate-wall-id.json` | 422 (semantic unique `wall.id`) |
| `oversized-grid.json` | 413 |

## Export (scores buffer)

`daylight.export` paints SVG and PNG from a frozen 200 scores buffer
(`Float32` 0–1, row-major, plus `GridMeta`). Hosts may pass zeros or
`synthetic_skyvis_scores`, or the live `DaylightResponse.scores`.

- Ramp: `daylight/ramp.py` and `daylight/ramp.css` (same stops as
  `apps/demo/lib/daylight/score.ts`). Cool teal/blue → amber/near-white.
  No lux, no climate tint, no second palette.
- Legend strip: Low → High chip plus
  `sky-visibility proxy — not illuminance / EN 17037`.
- Filename: `daylight-{planId}.svg` / `.png`.
