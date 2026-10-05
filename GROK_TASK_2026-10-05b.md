# GROK_TASK_2026-10-05b — stair fixes + locked daylight map

Start: `git fetch origin && git merge --ff-only origin/main` (if main moved past 620a877, rebase your 620a877 commit on top; it is being merged to main separately).

Julian's screenshots: /Users/jr/Documents/hobby/forsk/.smoke-preview/stairs-1005/ (1–5). Look at them first.

## 1. Draw stair snaps flush to the wall face (img 1, 2)
In Top view, Julian's cursor sits exactly on the wall face line, but the placement "x" marker lands about a wall-thickness/offset outside it, so the stair is drawn with a gap to the wall. Wanted: when the cursor is on or near (snap tolerance) a wall face, the stair's long edge (or start edge, whichever is being placed) lies exactly on that face — flush, no gap, on the room side. Fix the snap/offset maths (probably centreline vs face, or a half-width offset applied twice). Check both wall faces and rotated (non-axis-aligned) walls (img 3 is rotated).

## 2. Plan symbol: start circle centred on the walking line (img 3)
The small start circle is currently offset beside the walking line. It should sit centred ON the walking line at the start (like the orange circle in img 3): circle centre = walking line start point. Same in DWG/DXF export and print/PDF output.

## 3. One more step so the stair reaches the full wall height (img 4)
In elevation/section the stair stops one riser short of the top (orange arrow in img 4). The last riser must land exactly on the floor-to-floor / wall height. Fix the riser vs tread count (N risers, N-1 treads, the final riser reaching the upper level) consistently in 3D, plan label ("UP n × riser/tread"), section/elevation, takeoff and IFC/DWG output. Plan run length must grow by one tread accordingly. Update tests to assert top of last riser == wall height.

## 4. Daylight map locked (img 5)
The daylight/sun analysis mesh in perspective is selectable and steals clicks from areas and walls. Make it unselectable: put it on a locked Forsk layer (or lock the objects) so it still shows but can't be picked, and Forsk's own regenerate/delete of the daylight map still works (unlock internally when replacing it). It must not appear in selection-driven chips/suggestions.

## Rules
- Keep viewports cheap; no new shading/lighting.
- No ObjC/NSWindow hooks.
- English UI text.
- Run dotnet SoftParam.Tests and pytest (PYTHONPATH=server/src); all must pass.
- One commit per item is fine. Do NOT push. Write GROK_DONE_2026-10-05b.md with commits, test counts, and a 1-minute test list for Julian.
