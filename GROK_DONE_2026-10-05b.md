# Stair flush, full rise, locked daylight (2026-10-05b)

Branch `grok`. `origin/main` was `5afcf8d`, so `620a877` was not rebased. Four local commits. Not pushed. Rhino was not opened.

1. `e68a6ca75cc171203b8cfa0d364f4733e332a8a4` `fix(stair): put the placed edge on the wall face`
2. `b26772946fa78ee9911896f76ea5ca43555ad1c4` `fix(stair): centre the start circle on the walking line`
3. `82dfbbeedac07184bf765028793436b04affbe6b` `fix(stair): land the last riser on the wall height`
4. `391cda30a679b88bcbe9bd2aebc044e9262dfce8` `fix(daylight): lock the map so a click misses it`

Snap: the foot sits on the face. The half-width shift happens once, and only when the climb runs along the face, so the long edge is flush on the free side. A climb that leaves the face keeps the start edge on the face. Both faces and a rotated wall use the same normal. On an outer face the free side is outside the building.

Circle: the plan dot is at the walking-line start. The stroke starts at the dot's far edge. Preview, print and DWG share `Stairs.PlanSymbol`.

Rise: treads now equal risers. The last tread is the upper floor, so the last riser lands on the wall height and the run is one going longer. The riser count is still `ceil(rise / riser max)`. A 3000 mm wall is 17 risers and 17 treads. A dragged length still divides over those treads. The brief's N−1 treads was the old body, which stopped one riser short.

Daylight: `A-ANALYSE` and each mesh stay locked. Paint, hide, show, a stale mark and `clear_generated` unlock them, then lock them again. A selected mesh does not drive the pick line, the selection key or the suggestion bar. Hide and show stay.

Gates, on this tree: SoftParam.Tests 2255 passed, 1 skipped, exit 0. pytest (`PYTHONPATH=server/src`, `server/.venv/bin/pytest -q server/tests contracts/test_schemas.py`) 528 passed, 1 skipped, exit 0. `dotnet build plugin/rhinomcp.csproj` 0 warnings, 0 errors, exit 0. Pre-existing test warnings (CS8632, xUnit2031) are unchanged.

Live check, about a minute:
1. Top. Draw a stair with the cursor on a wall face, then the other face, then a rotated wall. The edge being placed sits on the face. No gap.
2. The plan start circle is centred on the walking line. The same circle is on a print and a DWG.
3. In elevation the last riser meets the wall top. A 3000 mm wall at going 260 runs 17 × 260.
4. Run daylight. Click the blue mesh: it does not select, and the chips stay as for the wall or the empty pick behind it. Hide the map, then run daylight again: the map is replaced.
