# Night log: v4 detail slices on `cursor/add-detail-2118`

Base: main `0f51473` (Draw wall, Draw stair, the suite under nb-NO). The brief named `night-v4` and main `8783beb`; this run works on its own cloud branch. Built on `b82f07b`, then rebased onto `0f51473` with no conflicts; the SHAs below are after the rebase.

| Slice | Commit | State |
|---|---|---|
| D1 Add detail replaces Add dimensions | `9fce4dc` | done headless |
| D2 plan detail | `84e52b6` | done headless; bake unverified |
| D2b break lines for long walls | `dbba948` | done headless; bake unverified |
| D3 wall section, opening elevation and section | `48aeec0` | done headless; bake unverified |
| D4 detail sheets and callouts | `0a16918` | done headless; bake unverified |
| D5 detail sheets in DWG | `bea17e8` | done headless; export unverified |

## Follow-up: bubbles fit their sheet number (`cursor/callout-bubble-fit-2118`)

Root cause: `DetailCallout.Of` worked out whether the sheet number fitted (`Fits`), but nothing acted on it. A 9 mm mark has about 6.1 mm of room on the sheet number's line, and "A-50-002" in Arial at 1.8 mm is about 7.4 mm wide. Now `Of` grows the radius until the number and the sheet number each clear the circle by 0.8 mm. The 9 and 12 mm sizes are minimums. The text stays 1.8 / 2.5 mm. Mark placement (`PlaceMarks`, which now also reads each mark's number and sheet), the plan callouts' `PlaceLeader`, the leaders and the arrows all use that one radius. The DWG flattens the same circle and writes the texts at their paper height, so it matches the PDF. Unverified: the live widths come from Rhino's text measure.

## Fixes after Julian's garage smoke on `ece9153`

Office smoke passed; the garage export step failed. Root causes and fixes:

| Fix | Commit | Root cause |
|---|---|---|
| Wall plan breaks again at 1:20 | `71f4d58` | The high window (sill 1300, above the 1200 cut) kept jamb zones at 4200/5400, so no gap ≥ 1000 was left: no break, 1:25. The plan now keeps zones and dimension stops only for openings its cut passes through (`Details.PlanOpenings`); the section cut moves to the break's `u1` when `u0` is inside an opening. |
| Cut lines on `A-WALL-CUT` at 0.70 | `afd1e74` | The bake trims the solids itself, so HLD never marks a section cut; every line went out as beyond/silhouette (`A-ELEV`, ≤ 0.50) and the poché was hatched, not outlined. The clipped poché's edges off the crop are now stroked with `PenCut`, role `cut` (`DetailClip.CutRuns`); seen lines on them are dropped. |
| Marks clear of the values | `0be150c` | The marks sat at the opening's or the cut's u, where the centred values go, and were baked without the laid-out dims. `DetailCallout.PlaceMarks` takes `LayoutFixed`'s obstacles and the poché, keeps a clear mark on its point, else `PlanDims.PlaceLeader` moves it inside the drawing's band, with a leader and dot. |
| Export step finds D01 | `6429202` | Garage door markers carry no `forsk:id`. The step now uses `forsk:id`, else the marker's object id (as the plugin does), and takes D01, else the lowest mark. |

Tests on the smoke garage's real south wall (`DetailFixtures.SmokeGarage`: D01, flip, sliding, the high window, pocket, roof): one break 4450…6230, drawn 7020, 1:20, values 750 · 900 · 200 · 900 · 200 · 900 · 2980 (underlined) · 900 · 270, the section cut at 6230. The bake reads the same `Details.Resolve` / `DetailSheet.Plan` data as these tests; the old tests used only the brief's one-door wall.

**Not as expected: three 1:20 sheets, not two.** On this garage the broken wall plan is 7020 / 20 + 30 = 381 mm wide. Its 80 mm section fits neither beside it (471 > 400) nor under it (82 + 10 + 242 > 254). So wall plus D01 give `A-50-001` (wall plan), `A-50-002` (wall section) and `A-50-003` (door), all at 1:20. This is pinned in `TheSmokeGarage_WallAndDoor_TakeThreeSheetsAt1To20`. Two sheets would need a rule change (Julian's call): a smaller `KeepMm`, so more of the wall can break out, or a wall section placed on the door's sheet.

Gates after the fixes (main still `0f51473`, no rebase): build 0 warnings; `SoftParam.Tests` 2104 passed, 1 skipped (nb-NO); pytest 515 passed, 2 skipped; contracts exit 0.

## Gates (D1–D5, Linux container, after the rebase)

- `dotnet build plugin/rhinomcp.csproj -c Debug -p:UseWindowsForms=false`: exit 0, 0 warnings.
- `dotnet test tests/SoftParam.Tests`: exit 0, 2092 passed, 1 skipped, under `CommaCulture` (nb-NO). Before the rebase: D5 2004, D4 2001, D3 1986, D2b 1974, D2 1967, D1 1946; base 1926 passed.
- `server/.venv/bin/pytest -q server/tests contracts/test_schemas.py`: exit 0, 511 passed, 2 skipped. Before the rebase: D5 510, D4 499, D2 499, D1 498; base 500 (the two `check_user_dims` tests went). `test_dxf_check.py` runs with `ezdxf` 1.4.4 in the venv.
- `WindowViewTests.TheViewModelsBar_IsTheRegistrysBar("house, door selected")` (flaky once on the Mac): the detail slices leave the single-opening bar as it was (Add detail comes in for walls with openings and for two or more walls only); 5 of 5 runs of `WindowViewTests` passed here. Not changed.
- `server/.venv/bin/python contracts/test_schemas.py`: exit 0.
- Container: `apt-get update` was needed before `apt-get install -y dotnet-sdk-8.0 python3.12-venv`. The venv's `mcp` resolved to 2.x, which has no `mcp.server.fastmcp`, so `pip install "mcp<2"` (1.30.0; `uv.lock` pins 1.26.0 and is untouched).

## Unverified (needs Rhino)

- D5: that `export_sheets` writes `Garage A-50-001 Details 1-20.dwg`/`.dxf` with the cut strokes at 0.70 on `A-WALL-CUT`, the values on `A-ANNO-DIMS`, the bubbles and marks on `A-SYMB`, no `misc_roles`, and the detail page's affine at 1:20; `export_smoke.py`'s `details add` on the live garage (door marker `forsk:host_id` as the wall) and its remove at the end.
- D4: the plan callouts (12 mm bubble, divider, number over `A-50-00n`, leader and 0.8 dot) placed by `PlanDims.PlaceLeader` clear of the poché and what is on the plan layer, and `callouts`/`callouts_blocked` in the plan page record; the view titles (circle, title, 1:20, the 0.35 rule) and the companion marks inside each plan detail; the packed positions on `S-DRAW::Details 20-1`; the footer's Scale cell and scale bar at 1:20; the sheet order in the PDF and the Drawing list rows; the `forsk/user_dims` delete; the receipt's ", details at 1:20".
- D1: `details add` from a live selection (walls by `forsk:id`, markers by `forsk:id`), and the card names, which come from `ReadIfcModel` on the live document at every window refresh.
- D3: the vertical bakes: the section cut and fills on the wall cluster and the slab (and roof), the frame cut outlined only (`IsSkippedFillKind` skips `opening`), the elevation's depth band keeping only the host wall's band, the HLD map with up = Z, and the level marks (a 10 mm thin line out of the crop, a small triangle, the text). The level text goes through `AddDimensionText`, so it carries role `dimension` plus `forsk:level`.
- D2b: that the 2D pass clips the HLD lines and the poché to each kept stretch and shifts them by `Map` with no slivers, the two zigzags 10 paper mm apart at each break, and the underline under 5550, 8000, 5350 and 7600.
- D2: the whole detail bake (`DetailBake.cs`): that `KeepSectionSide` trims the unioned wall cluster by the cut and the four crop planes (the 2D clip counts what it had to shorten and the page record's `note` says "N lines clipped in 2D"), that the HLD-to-frame map puts the drawing the right way up, the poché at the cut, the break lines where the crop cuts the end walls, the dimension text size, the heavier pens at 1:20, and that `layout_pack views ["detail_20_1"]` frames the page at 1:20 with the footer's Scale cell reading it.

## Changes from the brief

- `PlanDims.FixedChain` has `Row` (an int, 1 nearest the reference) where the brief lists `Rows`.
- The card names come in through `DocInput.DetailNames` (resolved Rhino-side), so the classifier and the card stay pure.
- An opening's host run is the run of its cluster whose band holds the opening's centre as `ReadIfcModel` gives it (projected on the host record's longest run). An opening on the short leg of an L-shaped record may miss; it then drops like a gone one.
- Single wall or single opening: Add detail is on the "?" card only. Two or more walls: slot 2. Walls with openings: slot 1.
- D2 plan detail draws doors and windows as their solids cut at 1200 (frames in section), not the 1:100 plan symbols; no swing arcs yet.
- D2 lays a sheet out per sheet, not per drawing: each detail sheet has its own model region at `(25000·k, −60000)` (k from the scale's ladder index and n), wide enough for the 400 mm area at 1:50 (20000). Drawings sit in it at their paper place × scale. D2 puts every detail of a scale on sheet 1 in a plain row (`Details.Row`); D4's packer replaces that.
- "The door's drawings give 20": the door's plan alone fits 1:10 (2100 × 1000). D3's `TheDoorsDrawings_AllFit1To20` pins it on the three real frames (the elevation, 2100 × 2700, and the section, 1000 × 3800, need 1:20).
- The detail page is built by its own `AddDetailSheetPage` after the drawing sheets; it reuses `AddClayDetail` over the whole detail area box and `AddSheetFooter`. Its sheet number stays empty until D4 numbers `A-50-00n`.
- Companion marks (section marker, elevation arrow) are placed by `Details.Frame` but drawn in D4 with their numbers.
- D2b fixture: "an 8400 wall with six 1000 windows at 1500 centres" does not fit (six at 1500 span 8500 + 1000). The test uses centres 1400 apart from 700 (no gap ≥ 1000 between keep zones), which keeps the point: no break, `TooLong`, and the 9000 crop steps to 1:25.
- D2b: when every candidate gap is gone and the plan is still too long, the breaks found stay and `Scale` steps on the drawn length; `DetailBreaks.Plan.TooLong` says so (the log line comes with D4's page record).
- D2b: `PlanDims.FixedChain`/`Chain` carry `Values` and `Underline` (true values over mapped stops); `Place` prints the given values.
- D3 level marks are their own small mark (`DetailDims.Levels`, `BakeDetailLevel`), not `Sections.Levels`/`PlaceLevels`, which are laid out for a whole building section; the ground line is not drawn on details.
- D3 opening section, horizontally: frame stops come in only when `OpeningElement.FrameInsetMm` (1 mm today) reaches `MergeMm` 5, so the chain is outer face → inner face, as the brief expects. Vertical chains merge stops closer than 5 mm (a door's sill at the floor).
- D3 vertical dimensions run up the outer face (pointing out); the level marks stand at the opposite crop edge.
- `layout_pack` accepts `detail_<5|10|20|25|50>_<n>` (contract pattern, server wrapper).
- D4 `DetailSheet.Plan(items)` takes the details with their drawings (`DetailSheet.Items(facts)`) rather than `(details, sizes)`; one `DetailSheetPlan(doc)` is the source for the set, the pages card, the file facts, each sheet and the callouts. `Details.Row` (D2's plain row) is gone.
- D4 shelves stack from the area's top; a detail's drawings start a new shelf when the whole row does not fit beside the last one. Sheet counts on the brief's one-door garage: the broken south wall plus the door give 2 sheets (the smoke garage's busier wall gives 3, see above); door plus window 2; both end walls plus both openings 4 (one A3 per detail, as the brief expects).
- D4 callouts: `DetailCallout.Callouts(sheets)` gives target, number and sheet number; placement is `PlanDims.PlaceLeader` with a 0.8 mm square round the target as its "room", so the bubble keeps off it, the poché and what is drawn. The leader is redrawn from the bubble's edge to the target. A blocked callout is not drawn and is named in `callouts_blocked`.
- D4 the pack result gains `detail_scales` and `details_dropped`; the page record of the plan gains `callouts` next to `callouts_blocked`.
- D4 `TitleBlock.ScaleBar` is true on detail sheets too (their detail is locked at the sheet scale).
- D4 the receipt puts ", details at 1:20" (", details at 1:20 and 1:25" for two scales) right after "on A3", before the revision; `Details.DroppedLine` ends the line after " · ".

- D5 `SheetFlat.FileName` already turned `:` into `-`; only pinned. `export_smoke.py` reads the door and its wall off the door marker (`forsk:id`, `forsk:host_id`) through `plan_smoke.opening_markers`, lists the details before adding, and removes only the ones it added when it ends, so the garage stays as the earlier smokes left it. A missing door fails the step and still exports.
- D5 `dxf_check.py --expect detail` takes the sheet number from the file name (`A-50-00n`), else `A-50-001`; the 0.70 weight must be on an entity (the cut layer's own weight stays 0.50).

## Left

- Nothing of D1–D5 is left headless. All bakes and exports wait for the live checklist.
- Not built: a denser packer (one A3 per detailed element), swing arcs in the plan detail, a log line for `DetailBreaks.Plan.TooLong`.
- An opening detail of a window above the plan cut (a high window): its plan is cut at 1200 like the rest, so it shows no window; not handled.

## Forsk-side follow-ups (forsk repo not reachable)

- `scripts/smoke_garage.sh`: nothing called `check_user_dims` from forsk directly, but its `garage_opening_smoke.py` step no longer checks user dims.
- `docs/SMOKE.md`: replace the R2 "Add dimensions" line with the Add detail line.
- ROADMAP: R2 "Add dimensions" is replaced by "Add detail" (v4 Detail drawings, step 1).

## Live checklist (about 2 minutes, one Print, one export)

Garage from `smoke_garage`, plugin built from `cursor/add-detail-2118`.

1. Click the south wall, Shift+click door D01 in it: "1 wall, 1 door", first pill **Add detail**. Click it: "✓ 2 details added · they print on a detail sheet." **?** → **Your details**: South wall · plan, section; Door D01 · plan, elevation, section.
2. **⋯** → **Choose sheets**: `A-50-001`, `A-50-002` and `A-50-003 Details 1:20` between the sections and the lists. Close, press **Print** once. The receipt reads "… on A3, details at 1:20 · Garage.pdf".
3. In the PDF: the plan has two callouts (1 over `A-50-001`, 1 over `A-50-003`) with leaders and dots, no new chain; the Drawing list has the three A-50 rows at 1:20; `A-50-001` holds the wall plan with one zigzag break between the sliding and pocket doors (2980 and the overalls underlined, no 1430); `A-50-002` the wall section; `A-50-003` the door plan, elevation and section in one row; no mark bubble on a value (a moved one has a leader and dot); titles like "1 Door D01 — Plan 1:20"; title block `A-50-00n`, Scale `1:20`, a scale bar; cut lines heavier than on the plan.
4. **Export DWG** once. Open `Garage A-50-001 Details 1-20.dwg`: cut on `A-WALL-CUT` at 0.70, values on `A-ANNO-DIMS`, bubbles on `A-SYMB`.
5. Type "remove the details": one line back; **Choose sheets** no longer lists the A-50 sheets. Do not print again.

# Night log: v3 release slices on `night-v3`

Base: main `d17499b`, then R1 `7f2fd0b` and the R3 WIP `a04ce5d`.

| Slice | Commit | State |
|---|---|---|
| R1 type picker | `7f2fd0b` | from the earlier session |
| R3 DWG/DXF export | `81c5cb5` | done headless |
| R2 selection dimensions | `43863a5` | done headless |
| R4 IFC export | `c05268a` | done headless |
| R5 stair | `ad5edb5` … `6ef2ce4` | done headless (session 2, below) |

## Gates (last run, at `c05268a`, Linux container)

- `dotnet build plugin/rhinomcp.csproj -c Debug -p:UseWindowsForms=false`: exit 0, 0 warnings. On Linux the build needs `-p:UseWindowsForms=false` because there is no WindowsDesktop SDK. It builds against the net48 reference assemblies. The Mac build is unchanged.
- `dotnet test tests/SoftParam.Tests`: exit 0, 1839 passed, 1 skipped.
- `server/.venv/bin/pytest -q server/tests contracts/test_schemas.py`: exit 0, 481 passed, 1 skipped.
- `python contracts/test_schemas.py`: exit 0.
- `python scripts/ifc_check.py /tmp/forsk-ifc-garage.ifc --expect garage`: exit 0. It checks counts and relations, `ifcopenshell.validate` and a shape for every wall.
- `ezdxf` 1.4.4 and `ifcopenshell` 0.9.0 are installed in the venv by hand and are not in `pyproject`/`uv.lock`. The two test files skip when either is missing.

## Unverified (nothing ran in Rhino)

- **DWG/DXF writer.** Does `CreateHeadless` plus `WriteFile` reach `export_ACAD.rhp` on the Mac without a dialog? The fallback is a selected-only write from the active doc. The result's `writer` field and `/tmp/forsk-print.log` say which one worked. Also unverified: lineweights, hatch patterns and text heights in the written files.
- **GeometryGym in Rhino 7 Mono.** It loads under CoreCLR in the tests only.
- **Dimensions on a real plan.** Whether user chains print, how they clear the tags, and the `plan_smoke` garage step (`check_user_dims`).

## Changes from the brief

- **IFC walls.** IFC4 deprecates IfcWallStandardCase, and GeometryGym writes it as `IfcWall` anyway. A one-run wall is therefore `IfcWall` with PredefinedType `STANDARD`, an axis and a layer set usage. The tests pin that form.
- **User dimensions.**
  - A wall ref measures its record's longest run.
  - A dimension drops when fewer than two refs are left.
  - The list card rows read "U01 · 2 walls, 1 opening", without the total.
- **Not done here** because they live in the forsk repo:
  - the `smoke_garage.sh` hook that calls `python3 scripts/export_smoke.py`
  - the `docs/SMOKE.md` lines
  - the FOOD4RHINO demo line

## Left

- Nothing from the v3 list. Follow-ups from session 2 are under "Session 2: R5 stair".

## Morning checklist (about 2 minutes)

1. `pip install ezdxf ifcopenshell`, build, install, run the garage smoke, then `python3 scripts/export_smoke.py` and `python3 scripts/stair_smoke.py`. Both should end in `PASS`. Note which `writer` the export smoke reports.
2. Shift+click the two long walls. The window should say "2 walls". Run **Add dimensions**, then **Print**: the plan should have one more chain across them, reading 200 · 3600 · 200.
3. Type "add a straight stair". The receipt should read "Added a straight stair along the … wall, 19 steps of 179." (19 × 179 assumes 3000 walls and a 400 slab.) Click the stair: the window says "Stair · 19 risers" and the bar offers **Edit stair**, **Delete stair**. Orbit: a solid sawtooth against the wall. ⌘Z removes it in one step; add it again.
4. **Print**, then **Export DWG**. In the PDF plan, the stair should have step lines, a walking line from a dot to an arrow, a diagonal break, dashed steps past it, and "UP 19 × 179/260" (same assumption). In the DWG, those lines are on layer A-STAIR. The DWG plan should also have named layers, a heavy cut line and the poché hatch.
5. Run **Export IFC** and open the file in an IFC viewer: walls, doors in their holes, the room with its name, and the stair as one stair object.

## Session 2: R5 stair

### Commits on `night-v3`

| Commit | What |
|---|---|
| `ad5edb5` | `feat(model): R5 stair`: the pure `Stairs` core and its tests, `add_stair` / `edit_stair` / `delete_stair`, the re-plan in `walls_from_layer`, contracts, server wrappers, test_schemas |
| `783db26` | `feat(print)`: the plan symbol on the sheet, the stair as a section/facade source, role `stair` → DWG layer `A-STAIR` |
| `da07330` | `feat(window)`: pick line, bar, Edit stair card, Add stair (two view points or along a picked wall), chat routing incl. "trapp", tool catalog |
| `e4c6bbc` | `feat(export)`: takeoff Stairs line, IfcStair + IfcStairFlight, `ifc_check --expect stair` |
| `882c806` | fix: the solid has one face per riser and tread (a kinked single face would draw no nosings in sections) |
| `6af68e5` | polish: `delete_opening` says "Removed 2 windows." (no wall id); prompts and both smokes follow |
| `97cf7a5` | `scripts/stair_smoke.py` and its headless test; layout_pack's plan page reports `stairs` |
| `6ef2ce4` | fix: a flip turns about the stair as built |

### Gates (last run, at `6ef2ce4`, Linux container)

- `dotnet build plugin/rhinomcp.csproj -c Debug -p:UseWindowsForms=false`: exit 0, 0 warnings.
- `dotnet test tests/SoftParam.Tests`: exit 0, 1905 passed, 1 skipped (66 new).
- `server/.venv/bin/pytest -q server/tests contracts/test_schemas.py`: exit 0, 495 passed, 1 skipped (14 new).
- `python contracts/test_schemas.py`: exit 0.
- `python scripts/ifc_check.py /tmp/forsk-ifc-garage.ifc --expect garage`: exit 0.
- `python scripts/ifc_check.py /tmp/forsk-ifc-stair.ifc --expect stair`: exit 0. The flight's body lands at x 1000–4900, y 2900–3800, z 0–2578.
- The container had no dotnet: installed `dotnet-sdk-8.0` from the Ubuntu archive (`dotnet-install.sh` is blocked by the proxy). ezdxf and ifcopenshell were installed in the venv by hand again.
- Every new test was checked to fail first: the core with a rounded instead of a ceiled riser count (2 fail), the routing without `IsStairEdit` (8 of 9 phrases fail), `RemovalLine` against the old signature.

### Unverified (nothing ran in Rhino)

- **The solid.** `Surface.CreateExtrusion` + `SplitKinkyFaces` + `CapPlanarHoles` gives a closed, outward solid. Also unverified: the solid in a section's poché and HLD.
- **The plan symbol on paper.** Pens, dash length, label size (2 mm, under 0.4 × width) and the label reading up for a stair that climbs south. `AddStroke` makes the dashes, the same path as the roof outline.
- **The two-point pick** (`ForskStair`, Top view, arrow preview) and its single undo record.
- **"Along this wall" on a real garage**: the room face, and the receipt's "along the north wall".
- **The re-plan after a rebuild.** `clear_generated` leaves stairs alone, and `walls_from_layer` re-plans the auto ones.
- **The IFC stair in a real viewer.** Only ifcopenshell has read it.

### Decisions

- **Rise "auto"** = the walls' most common height + the floor slab's thickness, read off the model (`kind=floor`, the slab the bake made from `model_settings.json`), else 400. The stair does not read `model_settings.json` itself: ForskBake owns that file and the slab already holds its number. Defaults give 3000 + 400 = 3400, so 19 risers of 178.9.
- **The re-plan hook** is `walls_from_layer`. It is the only path that sets wall height (Build and Rebuild both run it). Stairs survive `clear_generated`, which deletes only the kinds it lists.
- **The body** ends at the last tread: the top riser is the upper floor's edge (treads = n − 1, as specified). On a one-storey model the top step is open until a floor exists.
- **The start point** is the foot of the first riser on the centre line. `forsk:stair_against` (left/right) remembers the side a wall-placed stair stands against, so a wider stair grows away from the wall. Flip swaps the side.
- **Comfort**: steep when the riser is over 180 or 2R+G is over 640; shallow when 2R+G is under 600; narrow under 800. One clause, e.g. "(steep: 2R+G = 660)". The comment says TEK17 §12-16 must be checked before release.
- **Plan label** is one line, "UP 16 × 180/260", in the left half below the break. Two lines overflowed a 900 flight at 1:100.
- **Edit stair** is a card with fields (Width, Step height at most, Going) and Save / Flip / Cancel, not four prefill pills. Save sends only what changed.
- **Add stair** is on the ? card, not a bar slot, so the bar keeps its slots for a house. With one wall picked it runs along that wall; with nothing picked it asks for two points.
- **The record keys** are prefixed `forsk:stair_*` (plus the shared `forsk:width`), not bare `shape`, `start`.
- **The flight read**: the plan, the takeoff, the IFC and a flip read the stair as built (stored risers × riser), so they never drift from the 3D.

### Follow-ups (not done)

- Room tags and marks do not avoid the stair label yet. They are placed before or after it without a collision check.
- Floor openings over the stair, landings, L/U stairs, railings: out of scope. The `shape` key is there for them.
- `dxf_check.py` does not require A-STAIR. The garage set has no stair. The stair smoke checks the plan symbol count instead.
- The forsk repo's `smoke_garage.sh` could call `stair_smoke.py` after `export_smoke.py`. That lives in forsk and was not touched.

