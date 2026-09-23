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

Sheets are a greyscale `HiddenLineDrawing`, not a photo of the clay. Rhino 7
has no ClippingDrawings. `layout_pack` bakes black curves on `S-DRAW::Plan`
and `S-DRAW::North` / `East` / `South` / `West`, then frames each Detail on
that drawing. `CommitChanges` while the detail is still active puts
zoom-extents back (see `9fd120c`). If the frustum misses the drawing, that
page fails with `Layout detail is empty. The sheet does not show the drawing.`

The plan cut is a horizontal plane at the floor top plus 1200 mm, normal
down, passed into `HiddenLineDrawingParameters.AddClippingPlane`. A hidden
clipping-plane object is also stored on the Plan detail only. Elevations
are not clipped. Hidden curves and tangent edges are off. Silhouettes plot
at 0.35 mm. Other curves plot at 0.18 mm. The detail is Wireframe, so
`GetPreviewImage` sees object-black curves. Plot weight is for the vector
path. Forsk Pen is not the sheet ink.

`ViewCaptureSettings` on this Mac wrote a white page, including the title
block. `GetPreviewImage` of the same Layout, taken after a redraw and a
wait, has the lines and the title block, and `FilePdf.DrawBitmap` places
that bitmap when width and height are the bitmap's pixel size. A redraw
that is captured immediately photographs an unpainted white frame. Mac
layout capture is asynchronous. The button resolves the save path first,
then for each Forsk page sets that page active (detail not active), keeps
the paper and the detail in Wireframe, shows only that `S-DRAW` child,
redraws, calls `RhinoApp.Wait`, lets one idle pass, and only then calls
`GetPreviewImage`. Off Mac the capture stays vector (`RasterMode` false,
`OutputColor` BlackAndWhite). Do not put `ViewCaptureSettings` back on the
Mac path until this preview has ink.

Each export appends one line to `/tmp/forsk-print.log` and prints that path
in the Rhino command line. The line includes `greyscale make2d`, curve
counts, and ink. Ink 0 also writes `/tmp/forsk-print-page-N.png`. The write
is refused only after that activate/Wait still has no ink, with
`capture failed after activate/Wait` and those PNG paths. A frustum miss is
`PDF detail is empty. The sheet does not show the drawing.`

If the button log is still ink 0, the optional Mac fallback is `ExportAll`,
file type Rhino PDF, Multiple layouts, only the `Forsk —` pages, one PDF.
That command opens Rhino's export dialog (`! _ExportAll` in the Mac menu),
so Print does not run it.

Rhino 8 ClippingDrawings, as in the McNeel drafting guide
(https://www.rhino3d.com/docs/guides/user-guide/drafting-architecture/),
are later. This slice does not call them and does not recolor the clay.

Live check: quit, reinstall, reopen, `mcpstart`, Generate 3D, then Print.
`Forsk — Plan` is thin black wall lines through the windows. Elevations are
black facades. The model viewport stays clay. Log ink must be greater than 0.

## Upstream

`upstream` is `https://github.com/jingcheng-chen/rhinomcp.git`. Merge
`upstream/main` periodically. Keep our net48 csproj and `plugin/Compat/`
shims. See `DEVELOPMENT.md`.
