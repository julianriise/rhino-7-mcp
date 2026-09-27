# Vendored: Planwire Instant Daylight

Upstream: `planwire/apps/engine/daylight` at `e8dbeb61`. Contract: [`CONTRACT.md`](CONTRACT.md).

Copied unchanged. Imports stay `daylight.*`, so this directory is the top-level
`daylight` package under `server/src`. Sync by copying the files again. Do not edit the tracer here.

Left out:

- `validate.py`: it raises FastAPI `HTTPException`. The Forsk adapter
  (`server/src/forsk_daylight.py`) builds unique wall ids and checks the cell cap itself.
- `routers/daylight_routes.py` and the rest of `apps/engine`.

Fixtures and TS goldens live in `server/tests/fixtures/daylight/`, and
`server/tests/test_daylight_vendor.py` runs them headless.

Scope: sky-visibility proxy. Not EN 17037, not lux, not a code check.
