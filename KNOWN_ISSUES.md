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

## Layout PDF on Rhino 7 Mac

`layout_pack` frames each Detail, locks the scale, then pans the locked view
back onto the clay. `CommitChanges` while the detail is still active puts
zoom-extents back (see `9fd120c`). If the frustum still misses the clay, that
page fails with `Layout detail is empty. The sheet does not show the clay.`

`ViewCaptureSettings` on this Mac wrote a white page, including the title
block. `GetPreviewImage` of the same Layout, taken on its own at 2480×1754,
has the plan and the title block, and `FilePdf.DrawBitmap` places that bitmap
when width and height are the bitmap's pixel size. A redraw that is captured
immediately photographs an unpainted white frame. Skipping the redraw did not
fix the Print button: `/tmp/forsk-print.log` still showed ink 0 at 2480×1754.
Mac layout capture is asynchronous. The button now resolves the save path
first, then for each Forsk page sets that page active (detail not active),
keeps the paper in Wireframe, sets each detail to the `Forsk Pen` copy,
redraws, calls `RhinoApp.Wait`,
lets one idle pass, and only then calls `GetPreviewImage`. Off Mac the
capture stays vector (`RasterMode` false). Do not put `ViewCaptureSettings`
back on the Mac path until this preview has ink.

Each export appends one line to `/tmp/forsk-print.log` and prints that path
in the Rhino command line. Ink 0 also writes `/tmp/forsk-print-page-N.png`.
The write is refused only after that activate/Wait still has no ink, with
`capture failed after activate/Wait` and those PNG paths. A frustum miss is
still `PDF detail is empty. The sheet does not show the clay.`

If the button log is still ink 0, the optional Mac fallback is `ExportAll`,
file type Rhino PDF, Multiple layouts, only the `Forsk —` pages, one PDF.
That command opens Rhino's export dialog (`! _ExportAll` in the Mac menu),
so Print does not run it.

The plan detail is a horizontal cut. `layout_pack` places one clipping
plane at the top of the floor solids plus 1200 mm, normal pointing down,
and assigns it only to that detail. Elevations are not clipped. The roof
above the cut drops out of the plan. Rhino 7 Mac still captures the page
as a bitmap. `Forsk Pen` is display quality, not an Rhino 8 vector
Technical print. On this Rhino 7 the public attributes have surface-edge
thickness and curve color, not a silhouette-color property. The mode file
sets edge color usage to a single black color (usage 2; 0 is the object
color) and silhouette thickness to 2 px. The detail display color is black
because `GetPreviewImage` ignores plot color. RhinoCommon does not expose the hidden-line switch.
Pen leaves hidden lines off. Tangent and iso edges are turned off on the
`Forsk Pen` copy. To inspect that switch: Rhino Options, View, Display
Modes, Forsk Pen.

Live check: quit, reinstall, reopen, `mcpstart`, Generate 3D, then Print.
Open the PDF and `/tmp/forsk-print.log`. Plan shows wall openings at the
cut, not a filled roof. Elevations show the facade outline. Log ink must
be greater than 0.

## Upstream

`upstream` is `https://github.com/jingcheng-chen/rhinomcp.git`. Merge
`upstream/main` periodically. Keep our net48 csproj and `plugin/Compat/`
shims. See `DEVELOPMENT.md`.
