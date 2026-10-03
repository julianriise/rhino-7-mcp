# Night log: v3 release slices on `night-v3`

Base: main `d17499b`, then R1 `7f2fd0b` and the R3 WIP `a04ce5d`.

| Slice | Commit | State |
|---|---|---|
| R1 type picker | `7f2fd0b` | from the earlier session |
| R3 DWG/DXF export | `81c5cb5` | done headless |
| R2 selection dimensions | `43863a5` | done headless |
| R4 IFC export | `c05268a` | done headless |
| R5 stair | — | left for a later run |

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

- R5 stair.
- `delete_opening` still puts an id in its receipt.

## Morning checklist (about 2 minutes)

1. `pip install ezdxf ifcopenshell`, build, install, run the garage smoke, then `python3 scripts/export_smoke.py`. It should end with `export smoke: PASS`. Note which `writer` it reports.
2. Shift+click the two long walls. The window should say "2 walls". Run **Add dimensions**, then **Print**: the plan should have one more chain across them, reading 200 · 3600 · 200.
3. Run **Export DWG** (from the bar right after the Print). Open the plan file: it should have named layers, a heavy cut line and the poché hatch.
4. Run **Export IFC** and open the file in an IFC viewer: walls, doors sitting in their holes, and the room with its name.
