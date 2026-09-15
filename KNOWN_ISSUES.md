# Known issues (Rhino 7 macOS)

Runtime notes for this fork. Headless build/tests are green. Live Rhino 7
smoke on this Mac (2026-09-15): `mcpstart` on 127.0.0.1:1999, then
`python3 scripts/mvp_smoke.py` returned **smoke OK**. Viewport capture:
`docs/assets/mvp_smoke_box.png`.

## Build

- **RhinoCommon 7.33 is not on NuGet.** The original pin
  `7.33.23306.15001` does not exist as a stable package (7.33 is RC-only).
  NuGet was already resolving `7.34.23267.11001`. The csproj now pins 7.34
  explicitly. Still net48 / Rhino 7, not the R8 `8.x` line.
- This Mac's Rhino is **7.38.24338.17002** (x86_64, Rosetta). 7.34 refs load
  in 7.38. If a 7.38-only API ever sneaks in, bump the package pin, not the
  target framework.

## Mac plugin load (this bit us)

- Dragging a raw `.rhp` onto the **viewport** is wrong on Rhino 7 Mac. Rhino
  asks Open / Import because it thinks the file is a document. Commands such as
  `mcpstart` never appear.
- Correct location is a **package folder**:
  `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/`
  containing the `rhinomcp.rhp` assembly plus its dlls. Quit and reopen Rhino
  after copying. Optional: drag `plugin/bin/Debug/rhinomcp.macrhi` onto the
  **Dock icon**, not the viewport.
- The Windows-style `.../7.0/Plug-ins/rhinomcp/` path does not get scanned on
  Mac. `plugin/install.sh` now writes `MacPlugIns`.

## Runtime, proven 2026-09-15 (Rhino 7.38 Mac / Rosetta)

Smoke against the live plugin (`scripts/mvp_smoke.py`):

| Step | Result |
|---|---|
| `get_document_summary` | units=Millimeters, layers=6, objects=0 |
| `create_layer` A-WALL | GUID returned |
| `create_object` BOX 4000×6000×3000 mm | EXTRUSION, bbox `[[0,0,0],[4000,6000,3000]]` |
| `get_object_info` | same GUID + bbox |
| `analyze_objects` | count=1 |
| `capture_viewport` | Perspective 800×600 PNG |
| `undo` | Undid 1 operation (the box) |

`capture_viewport` PNG encoding works on this Mac (Rhino's libgdiplus). The
BOX lands as an Extrusion, not a Brep. Fine for the MVP.

Still unverified, out of MVP scope:

- Grasshopper 1 tools (compiled against GH 7.34, not driven on this install)
- `execute_rhinocommon_csharp_code` (Roslyn 4.8 on Mono). Disabled for MVP
  via `RHINO_MCP_ENABLE_CSHARP=0`.

## Do not do

- Do not install Package Manager **`rhinomcp`**. That yak is Rhino 8.
- Do not point Grok at `uvx rhinomcp` from PyPI while developing this fork.
  Use `server/` from this checkout so the Python side matches the plugin.
- Do not run two MCP clients against port 1999 at once.

## Grok Build

- Viewport captures are large. Project config sets `[mcp] max_output_bytes = 2000000`
  so the PNG is not truncated at the default 20 KB cap.
- `create_object` BOX is authored **centered on the origin**. The smoke script
  translates by half-size so a corner sits at `(0,0,0)`.

## Upstream

`upstream` is `https://github.com/jingcheng-chen/rhinomcp.git`. Merge
`upstream/main` periodically. Keep our net48 csproj and `plugin/Compat/`
shims. See `DEVELOPMENT.md`.
