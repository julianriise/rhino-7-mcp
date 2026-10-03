# Drag a wall — live checks

Rhino was not opened for these slices. They are local commits on `grok` and are not pushed. D1 to D3 are headless. D4 to D5 need a model with straight walls and at least one room.

Start from one selected Forsk wall. Use **Drag wall** on “What can I do here?”, or the command `ForskDragWall`. The bar stays Print, Move, Add a door.

## D1 — the distance

No Rhino. `WallDragTests` locks the projection, the snap, and the compass word.

- A diagonal drag keeps only the part along the wall’s normal. A sideways drag is 0.
- Step 10: 14 is 10, 15 is 20, 5 is 10, and the negatives match. Step 50: 25 is 50, −25 is −50. Step 100: 150 is 200.
- Under one step is “Not moved: the wall was not dragged.” The Norwegian line is “Ikke flyttet: veggen ble ikke dratt.”

## D2 — one straight wall

No Rhino for the check itself. `WallDragTests` accepts one straight run, including one run inside a joined plan, and refuses the rest without a coordinate.

- A whole wall record, before Split walls: “Drag works on one wall. Split walls for picking first.” Nothing moves.
- A curved wall: “A curved wall can't be dragged; it stays one record.” Nothing moves.
- Norwegian: “Du kan dra én vegg. Del veggene for plukking først.” and “En buet vegg kan ikke dras. Den blir stående som én vegg.”

## D3 — the sentence

No Rhino. The receipt is one sentence, with no point in it.

- An outer wall on its own side: “North wall moved 300 mm out”. The other way: “in”.
- An inner wall: “Wall between Kitchen and Bath moved 300 mm east”.
- Neighbours add “; 2 walls followed”, or “; 1 wall followed”. None when nothing followed.
- Norwegian: “Nordveggen flyttet 300 mm ut”, “inn”, and “Vegg mellom Kitchen og Bath flyttet 300 mm mot øst; 2 vegger fulgte”.

## D4 — the drag and the dimension

Select the north wall. Drag it outward.

- The preview keeps the old outline thin grey. The moved walls, including the neighbours that stretch, are Forsk blue `(41, 72, 245)`.
- A dimension is drawn at the middle of the wall, from the old face to the new one, along the normal. Arrow ticks at both ends. The text is “300 mm out”, in the same blue, and it updates as the snapped distance changes.
- Zoom and pan while dragging. The text and the arrow heads stay the same size on screen.
- The dimension is not an object. It is gone on release and on Esc. Nothing named like a dimension is left in the document.
- Release runs one `move_wall`. Neighbours stay stretched. Rooms are detected again. One Cmd+Z from the command undoes the wall, the neighbours, and the rooms together. The undo name is `Forsk: move_wall`.
- Drag the same wall back in. While dragging, the dimension reads “300 mm in”. The receipt says “in”.
- Type `250` and press Enter. The wall moves 250 mm, not the nearest step. The receipt names 250. The sign is the side the mouse is on. Before the mouse has chosen, it is outward.
- Set `Step=50`. The preview and the dimension jump by 50 mm. `Step=10` and `Step=100` do the same for their own step.
- A drag shorter than one step releases as “Not moved: the wall was not dragged.” At rest, before the mouse moves, the dimension reads “0 mm”.
- Esc. Nothing moves. The dimension disappears. The chat line is “Cancelled.” The previous Undo pill is still the previous action.
- Drag an outer wall past the depth of the room beyond it. The preview of that wall turns red and the line is the refusal, with no coordinate. Release does not move the wall and does not add an undo record.
- An inner wall’s dimension uses a sign: “+300 mm” outward along the normal, “-300 mm” the other way. “0 mm” in both languages.

## D5 — the pill

One wall is selected.

- The bar is still Print, Move, Add a door. **Drag wall** is not in the bar and is not slot 1.
- It is on “What can I do here?” for that one wall, in Model, after Move and before Delete. It is hidden with no wall, with two or more walls, with a room, and with an opening. A whole record that is one selected object still shows the pill. The command then refuses it, as in D2.
- The pill writes “Drag wall”, then “Drag the wall in the view, or type a distance. Esc cancels.” Rhino takes the click.
- Release uses the same joined move as a typed move: one undo record, the D3 sentence as the receipt, the wall review card under it when neighbours followed, and a stale daylight map when one was showing. The undo name is `Forsk: Drag wall`. The Undo pill appears and undoes that record.
- A refusal or Esc is one chat line. It does not replace the Undo pill.
- Write a Norwegian line first, for example “dra veggen”, then use the pill. The bubble is “Dra vegg”. The prompt is “Dra veggen i visningen, eller skriv en avstand. Esc avbryter.” The dimension says “300 mm ut” or “300 mm inn”. The receipt is the Norwegian D3 sentence. Esc says “Avbrutt.”

## D6 — this list

The checks above are the live pass. Headless gates for these commits are `dotnet build plugin/rhinomcp.csproj` with 0 warnings and `dotnet test tests/SoftParam.Tests`.
