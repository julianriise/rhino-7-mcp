# Viewport live checks

Rhino was not opened for these slices. They are local commits on `grok` and are not pushed. White mode only. Use a file that already has a floor, walls, glass, and a daylight mesh.

## VP1 — Forsk White

Forsk White is on by default.

- Top and Perspective are white (`245,245,247`) with black edges. Grid and axes are off in that mode. The document grid stays.
- A new view, a split, `4View`, and Open put Forsk White on model views again.
- A layout detail stays Wireframe. A print PDF stays inked.
- The daylight ramp stays coloured. If it goes flat, the next edit is `ShadeVertexColors` true on Forsk Analysis only, with its wires left off.
- `ForskWhite` turns the mode off. Model views that were Forsk White return to Shaded. A view already in Rendered stays Rendered.
- Glass stays transparent. Object colour and object transparency are not overridden.
- Shadows are off (`CastShadows`).
- Technical lines are off. If silhouettes are missing and orbit is smooth, raising `TechnicalMask` is a later decision.
- An older Forsk White is replaced on load. Later launches leave that import in place.
- A failure line is `Forsk White did not load: ...`.

## VP2 — demo chrome

The flag stays off until `ForskDemo`.

- Tool palettes and the top toolbar hide. The right sidebar stays shut when it was already shut.
- The left sidebar and the command field stay. `ForskSection` and `ForskPlanImport` still ask for their points and scale.
- If the status strip stays, record status-bar hide as no.
- Quit and reopen. The previous chrome comes back, then hides again from that same snapshot while the flag is on.
- Kill Rhino while the chrome is hidden. The next launch restores the original snapshot, then hides again.
- If the window does not answer, the line is `Forsk demo chrome was not changed: the document frame was not ready.` and nothing is toggled.
- The plugin does not write the plist.

## VP3 — plan cut

The cut follows Forsk White. Off deletes the plane. On puts it back.

- Top reads as a plan: the roof is gone, and cut walls are filled white with black edges. Perspective shows the whole storey. Front and Right are uncut.
- The plane is the floor top plus 1200 mm. With no floor it is at 1200. The normal is `(0,0,-1)`, the same direction as the print cut. If the roof remains and the floor vanishes, the normal is backwards and that is the next edit.
- The view is named Top, or Top plus a space or a digit. A layout named Top is not clipped.
- The plane is locked, on the hidden layer `Forsk Cut`, named `forsk-plan-cut`, tagged `forsk:plan_cut=1`. `clear_generated` leaves it.
- Hiding the clipping widget still leaves the white fill. A thin white cap on glass is acceptable.
- Open walls, a wrong fill colour, or a missing cap are a failure. The next step is a Top-only conduit. Do not bake poché.
- A failure line is `Forsk plan cut did not load: ...`.

## VP4 — selected wall

- Click a wall: a blue hatch (`41,72,245`, `#2948f5`) and the same blue selection colour. Click a floor: the blue highlight, and no hatch.
- Other objects use that same blue. `IgnoreHighlights` stays false. If grips or feedback disappear, leave it false.
- The hatch is not stored in the file. A mesh wall gets the blue highlight and no hatch.
- The hatch draws in front of the view. If it paints over the model, the next step is `PostDrawObjects`, not a bake.
- If the shaded selection wash fights the hatch, the next edit is that key in the Forsk White ini, and only when the key is already present.
- A failure line is `Forsk wall hatch did not load: ...`. A missing Solid pattern uses that line and draws no hatch.
