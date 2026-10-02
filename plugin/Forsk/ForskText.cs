using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Every user-facing string of the v3 window, in one list keyed by action
    /// id. An action's label is its id; its reason is id + ".reason". Labels
    /// are English. A Norwegian list can later sit beside this one.
    /// </summary>
    public static class ForskText
    {
        static readonly Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Actions: label, then the slot 1 reason where the action can be slot 1.
            ["file.import"] = "Import a plan",
            ["file.import.reason"] = "Nothing is in this file yet.",
            ["file.import.noplan"] = "There is no plan in this file yet.",
            ["file.use_curves"] = "Use these curves as the plan",
            ["file.use_curves.reason"] = "There is geometry here that Forsk did not make.",
            ["file.use_curves.ask"] = "Select the curves that are the walls, then click Use these curves as the plan again.",
            ["file.scale"] = "Set scale",
            ["file.scale.reason"] = "The scale is not confirmed.",
            ["file.generate"] = "Generate 3D",
            ["file.generate.reason"] = "The plan is 2D. Scale is settled.",
            ["file.rebuild"] = "Rebuild",
            ["file.rebuild.reason"] = "The model is only partly built.",
            ["file.print"] = "Print PDF",
            ["file.print.reason"] = "A 3D model is in the file.",
            ["file.print.stale"] = "Sheets are older than the model.",
            ["file.print.map"] = "The sheet leaves the map off.",
            ["file.check"] = "Check the plan",
            ["file.draw"] = "Draw a wall",
            ["help.card"] = "What can I do here?",
            ["daylight.rooms"] = "Make rooms",
            ["daylight.window"] = "Add a window",
            ["daylight.window.ask"] = "add a window",
            ["daylight.run"] = "Daylight",
            ["daylight.again"] = "Run daylight again",
            ["daylight.again.reason"] = "The map is out of date.",
            ["daylight.hide"] = "Hide map",
            ["daylight.show"] = "Show map",
            ["daylight.room"] = "Daylight for this room",
            ["section.add"] = "Add a section",
            ["section.room"] = "Section through this room",
            ["room.push_pull"] = "Push or pull a side",
            ["room.push_pull.prefill"] = "Push the north side of this room {n} mm out",
            ["room.push_pull.prefill.nb"] = "Skyv nordsiden av rommet {n} mm ut",
            ["section.remove"] = "Remove a section",
            ["opening.move"] = "Move",
            ["opening.move.prefill"] = "Move this {kind} {n} mm along the wall",
            ["opening.move.prefill.nb"] = "Flytt {kind} {n} mm langs veggen",
            ["opening.resize"] = "Resize",
            ["opening.resize.prefill"] = "Make this {kind} {n} mm wide",
            ["opening.resize.prefill.nb"] = "Sett bredden på {kind} til {n} mm",
            ["opening.type"] = "Swap type",
            ["opening.delete"] = "Delete",
            ["opening.add_door"] = "Add a door here",
            ["opening.add_door.ask"] = "add a door on this wall",
            ["wall.move"] = "Move",
            ["wall.move.prefill"] = "Move the north wall {n} mm north",
            ["wall.move.prefill.nb"] = "Flytt veggen i nord {n} mm mot nord",
            ["wall.move.reason"] = "The click selects the whole wall record.",
            ["wall.delete"] = "Delete",
            ["wall.delete.ask"] = "Which wall run goes? A click selects the whole wall record.",
            ["wall.delete.inner"] = "For an inner wall, say it: “delete the wall at 4000, 2500”.",
            ["exist.mark"] = "Treat this as the existing house?",
            ["edit.undo"] = "Undo",
            ["ink.set"] = "Ink",
            ["meta.title"] = "Title block",
            ["bridge.start"] = "Start bridge",
            ["print.one"] = "Print one sheet",
            ["print.clear"] = "Clear the layouts",
            ["sheets.clear"] = "Clear the sheet cache",
            ["rooms.list"] = "List rooms",

            // One-time cards: the question, then the pills.
            ["opening.type.ask"] = "Which {kind} type?",
            ["file.check.ask"] = "What the import was not sure about",
            ["wall.review.ask"] = "What followed the wall",
            ["wall.review.longer"] = "{wall} ({id}) · {mm} mm longer",
            ["wall.review.shorter"] = "{wall} ({id}) · {mm} mm shorter",
            ["wall.review.row"] = "{wall} ({id})",
            ["ink.set.ask"] = "Ink for the next Print. Sheets already drawn keep theirs until then.",
            ["ink.set.now"] = "Now: {ink}.",
            ["ink.default"] = "Default",
            ["ink.grey"] = "Grey",
            ["ink.hatch"] = "Hatch",
            ["meta.title.ask"] = "Title block",
            ["meta.title.note"] = "An empty date reads as today. An empty scale label reads as 1:100.",
            ["meta.project"] = "Project",
            ["meta.client"] = "Client",
            ["meta.address"] = "Address",
            ["meta.date"] = "Date",
            ["meta.scale_label"] = "Scale label",
            ["print.one.ask"] = "Which sheet?",
            ["sheet.plan"] = "Plan",
            ["sheet.north"] = "North",
            ["sheet.east"] = "East",
            ["sheet.south"] = "South",
            ["sheet.west"] = "West",
            ["sheet.schedules"] = "Schedules",
            ["sheet.section"] = "Section {letter}",
            ["print.clear.ask"] = "Clear the Forsk layouts and their drawings? The model stays.",
            ["sheets.clear.ask"] = "Clear the sheet cache on S-PLAN and S-ELEV? Print does not use it.",
            ["rooms.list.ask"] = "Rooms in this file",
            ["section.remove.ask"] = "Which section goes?",
            ["section.remove.all"] = "All sections",
            ["pdf.page.ask"] = "{file} has {n} pages. Which is the plan?",
            ["pdf.page.pill"] = "Page {n}",
            ["pdf.page.more"] = "Showing the first {n} pages.",
            ["word.done"] = "Done",
            ["word.save"] = "Save",

            // Words in a prefilled sentence.
            ["word.door"] = "door",
            ["word.window"] = "window",
            ["word.door.nb"] = "døra",
            ["word.window.nb"] = "vinduet",
            ["word.north"] = "North",
            ["word.south"] = "South",
            ["word.east"] = "East",
            ["word.west"] = "West",
            ["word.cancel"] = "Cancel",

            // A viewport pick, mirrored as one line in the thread. Rhino takes the keyboard while it runs.
            ["prompt.scale"] = "Pick two points on a length you know, in the viewport. Esc cancels.",
            ["prompt.scale.length"] = "Real length between the two points",
            ["prompt.scale.set"] = "Set scale",
            ["prompt.section"] = "Pick the start of the section in the viewport, then its end. Esc cancels.",
            ["prompt.draw"] = "Draw the wall in the viewport, on the wall layer. Enter finishes, Esc cancels.",

            // Lines in the thread.
            ["window.nofile"] = "No file open",
            ["line.reopened"] = "Reopened. The bar shows what is true for this file now.",
            ["line.busy"] = "Forsk is still working. Try again when the step line is gone.",
            ["line.thinking"] = "Thinking",
            ["line.generating"] = "Generating… step {i} of {n}",
            ["line.printing"] = "Printing… step {i} of {n}: {what}",
            ["line.running"] = "Running {what}…",
            ["line.bridge"] = "Bridge started.",
            ["line.printing.layout"] = "laying out the sheets",
            ["line.scale.cancelled"] = "Set scale cancelled.",
            ["line.scale.number"] = "Type the length in millimetres, such as 4000.",
            ["file.use_curves.done"] = "{n} curve(s) are on the wall layer now.",
            ["edit.undo.none"] = "Rhino had nothing to undo.",
            ["bridge.start.failed"] = "The bridge did not start: {why}",

            // Roles. One thread and one history; a role sharpens suggestions and tool choice.
            ["role.control"] = "Answer as",
            ["role.auto"] = "Auto",
            ["role.planner"] = "Planner",
            ["role.modeller"] = "Modeller",
            ["role.plotter"] = "Plotter",
            ["role.analyser"] = "Analyser",
            ["role.render"] = "Render",
            ["role.title"] = "Auto lets the router name the role for each answer. A pick stays until you choose Auto again.",

            // The bar.
            ["bar.because"] = "Suggested because",
            ["bar.help"] = "?",
            ["bar.refused"] = "{label} is no longer available here: the file changed.",

            // "What can I do here?" groups, in card order.
            ["group.start"] = "Start a project",
            ["group.import"] = "Import a plan",
            ["group.model"] = "Model and edit",
            ["group.openings"] = "Openings",
            ["group.rooms"] = "Rooms",
            ["group.print"] = "Print and sheets",
            ["group.daylight"] = "Daylight",
            ["group.sections"] = "Sections",
            ["group.profiles"] = "Ink",

            // One sentence where a neighbour would expect a dead control.
            ["hint.key"] = "Chat actions are hidden: Forsk has no API key. It reads FORSK_GROK_API_KEY, ~/.forsk/grok.env, or the repo .env.",
            ["hint.windows"] = "Daylight comes in through windows. Add a window, then run it.",
            ["hint.scale"] = "Generate waits for the scale: pick two points in the view, then type the real length.",
            ["hint.heights"] = "Generate uses the template heights. For other heights, say them: “generate with walls 2700”.",
            ["hint.units"] = "This file is not in millimetres. Forsk works in millimetres only.",

            // The one local sentence on an empty thread: it names the file state. No key needed.
            ["state.empty"] = "This file is empty.",
            ["state.foreign"] = "This file has geometry that Forsk did not make.",
            ["state.unscaled"] = "This file has an imported plan. Its scale is not confirmed.",
            ["state.plan"] = "This file has a 2D plan and no 3D model yet.",
            ["state.noplan"] = "This file has no plan yet.",
            ["state.partial"] = "This file has part of a Forsk model and no walls.",
            ["state.model"] = "This file has a Forsk 3D model.",
            ["state.units"] = "This file is not in millimetres. Switch the .3dm to millimetres, then Forsk can work on it.",

            // The status line.
            ["status.bridge"] = "Bridge off",
            ["status.ink"] = "ink: {ink}",

            // A step whose object is not named in its message.
            ["tool.clear_generated"] = "Model",
            ["tool.floor_from_layer"] = "Floor",
            ["tool.walls_from_layer"] = "Walls",
            ["tool.roof_flat_from_walls"] = "Roof",
            ["tool.openings_from_layer"] = "Openings",
            ["tool.rooms_from_layer"] = "Rooms",
            ["tool.rooms_detect"] = "Rooms",
            ["tool.mark_as_existing"] = "Existing house",
            ["tool.plan_import"] = "Plan",
            ["tool.plan_scale"] = "Scale",
            ["tool.dxf_import"] = "DXF",
            ["tool.layout_pack"] = "Sheets",
            ["tool.export_pdf"] = "PDF",
            ["tool.clear_layouts"] = "Layouts",
            ["tool.clear_drawings"] = "Sheet cache",
            ["tool.sheet_pack"] = "Sheet cache",
            ["tool.make2d_view"] = "Sheet cache",
            ["tool.set_project_meta"] = "Title block",
            ["tool.print_profile"] = "Ink",
            ["tool.section_add"] = "Section",
            ["tool.section_clear"] = "Section",
            ["tool.section_pick"] = "Section",
            ["tool.daylight_from_model"] = "Daylight",
            ["tool.add_opening"] = "Opening",
            ["tool.move_opening"] = "Opening",
            ["tool.set_opening"] = "Opening",
            ["tool.set_opening_type"] = "Opening",
            ["tool.delete_opening"] = "Opening",
            ["tool.move_wall"] = "Wall",
            ["tool.delete_wall"] = "Wall",
            ["tool.add_wall"] = "Wall",
            ["tool.room_push_pull"] = "Room",
            ["receipt.ok"] = "done",
        };

        /// <summary>The string under a key. A missing key is a bug the registry tests catch; the key shows instead of a blank.</summary>
        public static string Get(string key)
        {
            return key != null && Strings.TryGetValue(key, out var text) ? text : key ?? "";
        }

        public static bool Has(string key)
        {
            return key != null && Strings.ContainsKey(key);
        }

        public static string Label(string actionId)
        {
            return Get(actionId);
        }

        /// <summary>A string with {name} slots filled.</summary>
        public static string Format(string key, params string[] pairs)
        {
            var text = Get(key);
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                text = text.Replace("{" + pairs[i] + "}", pairs[i + 1] ?? "");
            return text;
        }

        public static IEnumerable<string> Keys => Strings.Keys;
    }
}
