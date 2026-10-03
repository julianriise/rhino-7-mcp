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

