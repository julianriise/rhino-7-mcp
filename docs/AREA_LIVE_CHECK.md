# Area statistics live checks

Rhino was not opened for these slices. They are local commits on `grok` and are not pushed. Ask in the Forsk chat. The files are garage and office.

## Chat

- "how big is the flat?", "areal per etasje", and "BRA?" each get a short answer under the purple Analyser mark.
- The answer leads with the totals, then at most eight rooms, largest first, then "+N more". Areas are m² to one decimal. No coordinates and no ids.
- A file with no rooms offers Make rooms. The line is "No rooms. Make rooms finds them from the walls."
- Move a wall, then ask again. The net area, and BRA and BTA where they are shown, change with the model.
- Area statistics is on the help card when the file has rooms. It is not on the bar. Print stays the first bar slot.
- A card click shows the first two sentences: the net total, then the floor, including BRA and BTA when the tool has them. The chat sees the full answer.
- A failure is a mark other than Analyser, a second area that is not the plan tag, or a figure with no reason when the walls cannot give it.

## Sheets

- Print the schedules. Under the Romliste Sum, each floor that has a figure gets "BRA 1. etasje" and "BTA 1. etasje" for the ground floor.
- Those rows match the chat. The room names and the Sum match the chat's rooms and net total.
- A floor with no figure adds no row. Sum stays the net room area.
- Glance at the chat and the Romliste on garage, then the same on office.

## Assumptions

- Room area is the stamped net area on A-ROOM, the same number as the plan tag and the Romliste, not a second measurement.
- The sum of those areas is the net room area, labelled "estimate", summed in mm² and then rounded to one decimal.
- Per floor uses the stored `forsk:level`. 0 is the ground floor: "1. etasje" in a Norwegian answer and on the Romliste, "Ground floor" in an English answer. The next stored level is "2. etasje" / "1st floor". Below grade, -1 is "U. etasje" / "Lower ground" and -2 is "Kjeller" / "Basement". An empty level is that ground floor.
- Per use maps soverom/bedroom to Soverom, bad/wc/bath/bathroom/toalett to Bad, kjøkken/kitchen to Kjøkken, stue/living to Stue, bod/storage to Bod, gang/entré/hall to Gang, and anything else, including "Rom", to Annet.
- The first matching word wins, so "Stue/kjøkken" is Stue. An explicit use is kept as given.
- BRA and BTA come from the outer face of the wall-union outline (`forsk:path`), not from the floor slab, which is extruded from that same ring and has no thickness of its own.
- BTA is the area of that outer loop, minus a closed courtyard. BRA insets the outer face by the floor's one wall thickness and outs the courtyard by the same, so inner walls stay inside. The hole that is the inner face of the outer walls is the rooms, and it is not subtracted.
- When the walls on a floor do not share one thickness, the thickness is missing, or the inset fails, both BRA and BTA for that floor are left out with one reason. No partial figure is printed.
- "print the room schedule" stays Print, "romliste" stays Sheets, and "how big is the daylight" stays Daylight.
- "the area is wrong", "problem med arealene", and "how do I find the area" stay Support.
- "bra" in "ser bra", "er bra", "veldig bra", "helt bra", or "ganske bra" is not an area question. Bare "BRA?" is.
- "m²" is read as the word "m2". "12m²", "12 m2", "make this room 12 m2" and "rommet skal være 12 m²" route to Modeller. "hvor stor er stua?", "BRA?" and "areal per etasje" stay Analyser.
- `rooms_detect` stays in the Analyser pack. `add_opening` stays in the daylight pack and stays Modeller's tool. `rooms.list` stays Planner. Nothing is removed.
- The Romliste gains a BRA or BTA row only when that figure exists. A note adds no row, and a table with no figures is unchanged.
