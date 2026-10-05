# Area statistics live checks

Rhino was not opened for these slices. They are local commits on `grok` and are not pushed. Ask in the Forsk chat. The files are garage and office.

## Chat

- "how big is the flat?", "areal per etasje", and "BRA?" each get a short answer under the purple Analyser mark.
- The line is the net once, then BRA and BTA when the walls give them, then the largest few uses. One floor does not get its own line. No wall-thickness note. No room list, and no second sentence that repeats the net. Areas are m² to one decimal. No coordinates and no ids.
- The card under that line is Area summary: each floor's net, then BRA and BTA when the walls give them, then each use, then the total. The note is "Estimate. Not measured to NS 3940." A click on Area statistics still shows the short answer.
- A file with no rooms offers Make rooms. The line is "No rooms. Make rooms finds them from the walls."
- Move a wall, then ask again. The net area, and BRA and BTA where they are shown, change with the model.
- Area summary is the next chip after Generate 3D or area statistics, when the file has rooms and nothing is picked. It is also on the help card. Print stays the first bar slot.
- A failure is a mark other than Analyser, a second line that repeats the net, or a BRA/BTA figure when the walls cannot give one.

## Sheets

- Print the schedules. The front sheet Areas table names the ground floor "Ground floor". When the walls give a figure, that floor has "Gross area (BTA)" and "Usable area (BRA)". The room schedule Sum is the net.
- Those figures match the chat. The room names and the Sum match the chat's rooms and net total. An unnamed room is "Room". The use row for anything else is "Other".
- A floor with no figure adds no row. Sum stays the net room area.
- Glance at the chat and the room schedule on garage, then the same on office.

## Assumptions

- Room area is the stamped net area on A-ROOM, the same number as the plan tag and the room schedule, not a second measurement.
- The sum of those areas is the net room area, labelled "estimate", summed in mm² and then rounded to one decimal.
- Per floor uses the stored `forsk:level`. 0 is the ground floor: "Ground floor" on the sheets and in an English answer, "1. etasje" in a Norwegian answer. The next stored level is "1st floor" / "2. etasje". Below grade, -1 is "Lower ground" / "U. etasje" and -2 is "Basement" / "Kjeller". An empty level is that ground floor.
- Per use maps soverom/bedroom to Soverom, bad/wc/bath/bathroom/toalett/wet room to Bad, kjøkken/kitchen to Kjøkken, stue/living to Stue, bod/storage/arkiv/bøttekott to Bod, gang/entré/hall to Gang, kontor/office/kontorplasser to Kontor, konferanse/meeting to Møterom, and anything else, including "Rom" and "Fax/kopi/printer", to Annet. The Areas table prints that row as "Other" ("Annet" when Norwegian is asked). The other use names stay as mapped. An unnamed room is "Room" ("Rom" when Norwegian is asked). A name already on the drawing stays as written.
- The first matching word wins, so "Stue/kjøkken" is Stue. An explicit use is kept as given.
- BRA and BTA come from the outer face of the wall-union outline (`forsk:path`), not from the floor slab, which is extruded from that same ring and has no thickness of its own.
- BTA is the area of that outer loop, minus a closed courtyard. BRA insets the outer face by the floor's one wall thickness and outs the courtyard by the same, so inner walls stay inside. The hole that is the inner face of the outer walls is the rooms, and it is not subtracted.
- When the walls on a floor do not share one thickness, the thickness is missing, or the inset fails, both BRA and BTA for that floor are left out. The reason stays on the floor record and off the chat line. No partial figure is printed.
- "print the room schedule" stays Print, "romliste" stays Sheets, and "how big is the daylight" stays Daylight.
- "the area is wrong", "problem med arealene", and "how do I find the area" stay Support.
- "bra" in "ser bra", "er bra", "veldig bra", "helt bra", or "ganske bra" is not an area question. Bare "BRA?" is.
- "m²" is read as the word "m2". "12m²", "12 m2", "make this room 12 m2" and "rommet skal være 12 m²" route to Modeller. "hvor stor er stua?", "BRA?" and "areal per etasje" stay Analyser.
- `rooms_detect` stays in the Analyser pack. `add_opening` stays in the daylight pack and stays Modeller's tool. `rooms.list` stays Planner. Nothing is removed.
- The Areas table gains a Gross area (BTA) or Usable area (BRA) row only when that figure exists. A note adds no row, and a table with no figures is unchanged.
