using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Eto.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;
using RhinoMCPPlugin.Functions;
using rhinomcp.Serializers;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// In-process dispatch onto the existing [McpCommand] handlers.
    /// Geometry stays in those handlers. This class only filters and wraps undo.
    /// Call Execute on the UI thread.
    /// </summary>
    public static class ForskTools
    {
        static readonly RhinoMCPFunctions Handler = new RhinoMCPFunctions();
        static IReadOnlyDictionary<string, JObject> Catalog => ForskToolPacks.Catalog;

        /// <summary>The turn's tools: its intent's pack, or the union for General; a role only reorders them.</summary>
        public static JArray ToolsFor(ForskIntent intent, ForskRole role)
        {
            return ForskToolPacks.Schemas(ForskToolPacks.For(intent, role));
        }

        public static JObject Execute(string name, JObject parameters)
        {
            if (!Allowed(name))
                return Fail("Tool " + name + " is not available in the Forsk panel.");
            return ExecuteAllowed(name, parameters);
        }

        static bool Allowed(string name)
        {
            return !string.IsNullOrEmpty(name)
                && (ForskToolPacks.Union.Contains(name) || name == ForskToolPacks.DebugTool);
        }

        public static JObject ExecuteAllowed(string name, JObject parameters)
        {
            if (!Catalog.ContainsKey(name))
                return Fail("Tool " + name + " is not available in the Forsk panel.");
            if (name == ForskToolPacks.DebugTool)
                return ReadDebugReport();
            if (name == ForskDaylight.ToolName)
                return ForskDaylight.Run(parameters?["target"]?.ToString(), Dispatch);
            if (name == ForskToolPacks.PrintPagesTool)
                return Local(Handler.PrintPages, parameters);
            if (name == ForskToolPacks.TakeoffTool)
                return Local(Handler.TakeoffTool, parameters);
            return Dispatch(name, parameters);
        }

        /// <summary>A window tool that is no bridge command, as a tool envelope. UI thread.</summary>
        static JObject Local(Func<JObject, JObject> run, JObject parameters)
        {
            try
            {
                return new JObject { ["status"] = "success", ["result"] = run(parameters ?? new JObject()) ?? new JObject() };
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        /// <summary>A bridge command outside the chat catalog, such as daylight_scene. UI thread.</summary>
        public static JObject Command(string name, JObject parameters)
        {
            return Dispatch(name, parameters);
        }

        /// <summary>Command from a background thread, run on the UI thread.</summary>
        public static JObject CommandOnUi(string name, JObject parameters)
        {
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                envelope = Dispatch(name, parameters);
            }));
            return envelope ?? Fail("No result");
        }

        public static string Receipt(string name, JObject envelope)
        {
            if (envelope == null) return name + " · error";
            var status = envelope["status"]?.ToString();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var err = envelope["message"]?.ToString();
                return name + " · error · " + Short(string.IsNullOrEmpty(err) ? "failed" : err);
            }

            var result = envelope["result"] as JObject;
            if (result == null) return name + " · ok";

            var countTok = result["count"] ?? result["opening_count"] ?? result["cut_count"];
            var message = result["message"]?.ToString();
            if (countTok != null)
            {
                var count = countTok.ToString();
                if (count == "0" && !string.IsNullOrEmpty(message))
                    return name + " · ok · 0 · " + Short(message);
                var note = ThicknessNote(message);
                if (!string.IsNullOrEmpty(note))
                    return name + " · ok · " + count + " · " + Short(note);
                return name + " · ok · " + count;
            }
            if (!string.IsNullOrEmpty(message))
                return name + " · ok · " + Short(message);
            return name + " · ok";
        }

        static string ThicknessNote(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            const string key = "Could not measure wall thickness.";
            var at = message.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";
            return message.Substring(at).Trim();
        }

        static string Short(string text)
        {
            var one = Clip(text);
            if (one.Length <= 96) return one;
            return one.Substring(0, 93) + "...";
        }

        public static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var one = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (one.Length <= 180) return one;
            return one.Substring(0, 177) + "...";
        }

        public static JObject Fail(string message)
        {
            return new JObject
            {
                ["status"] = "error",
                ["message"] = message ?? "error"
            };
        }

        /// <summary>The debug report as a tool result. The receipt stays one line; the model gets the text.</summary>
        static JObject ReadDebugReport()
        {
            try
            {
                var text = ForskWindow.DebugReportText(RhinoDoc.ActiveDoc);
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = new JObject
                    {
                        ["message"] = "Debug report.",
                        ["report"] = text ?? ""
                    }
                };
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        static JObject Dispatch(string name, JObject parameters)
        {
            var dispatch = Handler.GetDispatchTable();
            if (!dispatch.TryGetValue(name, out var entry))
                return Fail("Unknown command type: " + name);

            parameters = parameters ?? new JObject();
            if (name == "get_selected_objects_info")
                parameters["include_attributes"] = true;
            if (name == "get_objects")
            {
                parameters["include_geometry"] = false;
                if (parameters["limit"] == null)
                    parameters["limit"] = 30;
            }

            return Invoke(name, entry.ReadOnly, () => entry.Handler(parameters));
        }

        /// <summary>
        /// A write that is no bridge command, as a tool envelope inside the call's undo
        /// record (or the window's, when one is open). UI thread.
        /// </summary>
        public static JObject Write(string name, Func<JObject> run)
        {
            return Invoke(name, false, run);
        }

        static JObject Invoke(string name, bool readOnly, Func<JObject> run)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return Fail("No active document.");

            uint record = 0;
            // One record per pill or answer: while the window's record is open, this call writes into it.
            var undo = !readOnly && !doc.UndoRecordingIsActive;
            if (undo) record = doc.BeginUndoRecord("Forsk: " + name);
            ForskCalls.Enter();
            try
            {
                var result = run();
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = result ?? new JObject()
                };
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
            finally
            {
                ForskCalls.Exit();
                if (undo) doc.EndUndoRecord(record);
            }
        }
    }

    public static class ForskPrompts
    {
        const string Builtin = @"You are Forsk. You drive Rhino 7 through the typed tools in this panel. Rhino is the clay viewport.

Document units must be millimetres. Call get_document_summary first. Read meta_data.units. Accept Millimeters or Millimetres. If units are anything else, stop. Tell the user to switch the .3dm to millimetres. Do not scale.

Project origin (0,0,0) is the existing-building reference corner. State that when you place the first object.

Viewport selection is the target. On any this / it / selected turn, call get_selected_objects_info before you edit. Do not use the last created object.

Empty selection refuse, exact copy:
Nothing is selected. Click the object in Rhino, then say it again. Or name it and I will select it.

Plan to 3D order is floor_from_layer, walls_from_layer, roof_flat_from_walls, openings_from_layer door, openings_from_layer window, rooms_from_layer. Skip rooms when there are no room curves.

Import a DXF with dxf_import, not Rhino's own Import: it keeps DXF text escapes, so a label like Bøttekott names its room, and it reads the DXF's units, so the plan lands at true size in mm. Pass the absolute .dxf path the user gave. With none, call it with no path and the user picks the file.

Import a floor plan with plan_import: a PDF as pdf_path (page when the user names one), or a scan or photo of the plan (PNG or JPEG) as image_path alone. With no path the user picks the file. It lands as a faded underlay plus 2D walls, doors, windows and rooms to review. Pass on the receipt: counts, how the scale stands, what needs review, and the licence line when the raster source read the plan. When nothing was imported, pass on the reason and the next step it gives. Set scale is plan_scale; with no points the user picks two and types the length. Nothing goes 3D until the user asks to generate.

Defaults: walls 3000, floor thickness 400, roof 200, doors sill 0 head 2100 width 900, windows sill 900 head 2100 width 1200. Pass stated heights as tool params. If the user states none, use the defaults and say so once.

Never bake from layer X-EXIST. Refuse: X-EXIST is existing underlay, not a bake source.
Never edit an opening on X-EXIST or forsk:kind=existing. Refuse: Existing underlay is not a Forsk host wall.
Roof or openings before walls: Walls first. Call walls_from_layer before roof_flat_from_walls. Or the openings / add_opening line with the same shape.

Delete or remove a door or window, including these windows when two or more are selected, is one delete_opening with no id. Add is add_opening. Both rebuild the host from its path. No filler plate. Move millimetres along the wall is move_opening delta_mm. Set width, sill, or head on the selected opening is set_opening. make this a sliding door, top-hung window, flip swing, and change hand are one set_opening_type. Two or more selected openings are one call and no id. make all windows fixed is one set_opening_type with that type and all true. Do not call clear_generated. Do not call delete_object for an opening. Do not use the last opening created. The delete status line is the tool message, such as Removed Door D02. or Removed 2 windows. Do not repeat a marker id.

Sheets prefers Layout pages and a PDF. Print, make PDF, or skriv ut opens a save dialog. Do not invent a file path. set_project_meta stores the project info (project, project number, client, address, architect, date, revision). layout_pack bakes black S-DRAW curves and makes the pages. clear_layouts removes those pages and the S-DRAW curves. Sheet cache on S-PLAN and S-ELEV stays: sheet_pack, make2d_view, clear_drawings. Never clear_generated for drawings or layouts.

Do not call Grasshopper tools or execute code. Reply in at most two sentences: one past-tense status line, then at most three short facts. No Target block on success. The panel prints one row per tool.";

        public static string Warning { get; private set; }

        public static string Load(ForskIntent intent)
        {
            var root = FindRoot();
            string core = null;
            if (root != null)
            {
                var path = Path.Combine(root, "prompts", "system.md");
                if (File.Exists(path))
                {
                    core = File.ReadAllText(path);
                    Warning = null;
                }
            }
            if (string.IsNullOrWhiteSpace(core))
            {
                core = Builtin;
                Warning = "Using the built-in prompt. prompts/system.md was not found.";
            }

            var pack = LoadPack(root, intent);
            if (string.IsNullOrWhiteSpace(pack)) return core.Trim();
            return core.Trim() + "\n\n" + pack;
        }

        public static string FindRoot()
        {
            var env = Environment.GetEnvironmentVariable("FORSK_HOME");
            if (IsForskRoot(env)) return env;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var guess = Path.Combine(home, "Documents", "hobby", "forsk");
            if (IsForskRoot(guess)) return guess;
            return null;
        }

        static bool IsForskRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return File.Exists(Path.Combine(path, "prompts", "system.md"));
        }

        static string LoadPack(string root, ForskIntent intent)
        {
            var file = PackFile(intent);
            if (file != null && root != null)
            {
                var path = Path.Combine(root, "prompts", "packs", file);
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            return BuiltinPack(intent);
        }

        static string PackFile(ForskIntent intent)
        {
            if (intent == ForskIntent.Edit) return "ui_edit.md";
            if (intent == ForskIntent.Sheets) return "ui_sheets.md";
            if (intent == ForskIntent.Print) return "ui_print.md";
            if (intent == ForskIntent.Build) return "ui_build.md";
            if (intent == ForskIntent.Daylight) return "daylight.md";
            if (intent == ForskIntent.Import) return "plan_import.md";
            return null;
        }

        static string BuiltinPack(ForskIntent intent)
        {
            if (intent == ForskIntent.Edit)
            {
                return "Turn bias: Edit. At most two sentences. No Target block on success. "
                    + "Prefer add_opening, move_opening, set_opening, set_opening_type, delete_opening. "
                    + "Delete, add, move, set, and set type rebuild that host only. "
                    + "make this a sliding door, top-hung window, flip swing, and change hand are one set_opening_type. "
                    + "Two or more selected openings are one set_opening_type with no id. "
                    + "make all windows fixed is one set_opening_type with that type and all true. "
                    + "Two or more selected windows are one delete_opening with no id. "
                    + "Status line for that call: Removed Door D02., or Removed 2 windows. Do not repeat a marker id. "
                    + "Move a wall is move_wall: side for an outer wall or at [x, y], toward, distance_mm. With neither, ask which wall. "
                    + "Delete a wall is delete_wall, named the same way; its openings go with it. "
                    + "Add a wall is add_wall: from and to [x, y], or line_id for a drawn line. "
                    + "Push or pull a side of the selected room is room_push_pull: side, distance_mm, way out or in. "
                    + "A stair (trapp) is add_stair: from and to, along_wall for the picked wall, or nothing. Change it with edit_stair: make the stair 1000 wide is width, steps 170 high is riser_max, going 280 is going, flip the stair is flip. Delete it with delete_stair. Never draw a stair with other tools. "
                    + "A successful move, delete or add updates the floor, the flat roof and the rooms, and the status line is the tool message. "
                    + "A shown daylight map is hidden as out of date. "
                    + "Do not call clear_generated. Do not call delete_object for an opening. "
                    + "Refuse X-EXIST hosts with: Existing underlay is not a Forsk host wall. "
                    + "Bake, sheets, and print stay available when the user asks.";
            }
            if (intent == ForskIntent.Sheets)
            {
                return "Turn bias: Sheets. At most two sentences. No Target block on success. "
                    + "Prefer sheet_pack, make2d_view, clear_drawings. "
                    + "Layout pages and a PDF stay available: set_project_meta, layout_pack, export_pdf, clear_layouts. "
                    + "Door, window or room schedules are the schedules page: layout_pack views schedules; schedule_kinds picks the lists. "
                    + "Dimensions are on the plan sheet: layout_pack views plan draws them from the model. Never draw or type dimensions. "
                    + "A section (snitt) A–A is section_add (a room by name, axis cross or long, or from and to, or line_id), then layout_pack views plan and section_<letter>. Never draw a section yourself. "
                    + "add cross section is the viewport command ForskSection. Do not call section_add for that phrase and do not draw the line. "
                    + "A profile (use the grey profile, hatched poché, svart poché) is print_profile: default, grey or hatch; then layout_pack draws with it. "
                    + "The set is on the Choose sheets card; change it with print_pages; Print prints the set. "
                    + "Print PDF opens a save dialog. Do not invent a file path. Never clear_generated for drawings.";
            }
            if (intent == ForskIntent.Print)
            {
                return "Turn bias: Print. At most two sentences. No Target block on success. "
                    + "layout_pack, export_pdf, clear_layouts. Print writes the set: the front sheet (Drawing list and Areas), the plan with its dimensions, the four facades, every stored section and the lists, numbered A-00-001, A-20-001, A-40-001 and on. "
                    + "Print in a profile (grey, hatch) is print_profile first, then Print. "
                    + "The set is on the Choose sheets card; change it with print_pages; Print prints the set. "
                    + "Print PDF opens a save dialog. Do not invent a file path. "
                    + "clear_layouts removes the pages and the S-DRAW curves. "
                    + "sheet_pack stays available when the user asks for drawings.";
            }
            if (intent == ForskIntent.Daylight)
            {
                return "Turn bias: Daylight. daylight_from_model runs it. "
                    + "Hide daylight map and Show daylight map keep the mesh; do not delete it and do not call daylight_clear. "
                    + "Reply with spaces, windows, and the time, then the tool's disclaimer line word for word. "
                    + "No rooms: offer rooms_detect, which finds them from the walls. "
                    + "No windows: pass the refusal on and offer add_opening. Never lux or a code verdict.";
            }
            if (intent == ForskIntent.Area) return ForskArea.Bias;
            if (intent == ForskIntent.Takeoff)
                return "Turn bias: Takeoff. Call takeoff. Answer in one line with the figure the user asked for, from its rows, and say approx. "
                    + "The window shows every line on a card under the receipt: do not list them. No coordinates, no ids. Do not invent a figure.";
            if (intent == ForskIntent.Dxf) return ForskDxf.Bias;
            if (intent == ForskIntent.Import)
            {
                return "Turn bias: Import. plan_import brings in a PDF (pdf_path) or a scan or photo of the plan (image_path alone); with no path the user picks. plan_scale sets the scale. "
                    + "Reply with the counts, how the scale stands, the licence line when there is one, and what needs review, from the tool message. "
                    + "When nothing was imported, give the reason and the next step from the message, such as the command that fetches the model weights. "
                    + "Set scale with no points given is plan_scale with no arguments: the user picks two points and types the length. "
                    + "Do not generate 3D here. The user reviews and traces missing walls on the wall layer first, then asks to generate.";
            }
            if (intent == ForskIntent.Support)
            {
                return "Turn bias: Support. Answer in at most two sentences. "
                    + "The tools only read the document, the selection, a layer's objects, and the debug report. "
                    + "Do not edit the model. Do not select, capture, or call a tool that is not in the list. "
                    + "Pass on what the tools say. Do not invent a count. "
                    + "Nothing selected is not a refusal. This overrides the empty-selection rule above, including no document scan. "
                    + "Call get_document_summary, then get_objects on the layer the report is about, and get_object_info when a name or id is known. "
                    + "Do not say that nothing is selected. "
                    + "A question: answer it. A bug or a feature request: say what you found in the model. The window adds the report.";
            }
            if (intent == ForskIntent.Build)
            {
                return "Turn bias: Build. At most two sentences. A full bake is one line. "
                    + "Bake, heights, tilbygg, clear_generated. "
                    + "Order: floor, walls, roof, openings, rooms_from_layer. "
                    + "When that count is 0, call rooms_detect. Drawn A-ROOM markers win; do not detect over them. "
                    + "Make rooms or find rooms is rooms_detect again: reply with rooms, total area, and any open region's reason. "
                    + "Rebuild is clear_generated, then that order. There is no Rebuild button. "
                    + "Refuse X-EXIST as a bake source.";
            }
            return "";
        }
    }

    public static class ForskKeys
    {
        public const string Missing =
            "Set FORSK_GROK_API_KEY to chat. Export it before launching Rhino, or put FORSK_GROK_API_KEY=… in ~/.forsk/grok.env. Generate 3D, Print PDF, and Daylight still run without a key.";

        public static string Load()
        {
            var env = Environment.GetEnvironmentVariable("FORSK_GROK_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new List<string>
            {
                Path.Combine(home, ".forsk", "grok.env")
            };
            var root = ForskPrompts.FindRoot();
            if (root != null) paths.Add(Path.Combine(root, ".env"));

            foreach (var path in paths)
            {
                var key = ReadFile(path);
                if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
            }
            return null;
        }

        static string ReadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string bare = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line.Substring(7).Trim();
                const string prefix = "FORSK_GROK_API_KEY=";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = line.Substring(prefix.Length).Trim().Trim('"').Trim('\'');
                    if (value.Length > 0) return value;
                }
                if (bare == null && line.IndexOf('=') < 0)
                    bare = line;
            }
            return bare;
        }
    }

    /// <summary>
    /// Dock bubble is the closing text. Target essays stay off the happy path.
    /// </summary>
    public static class ForskReply
    {
        static readonly string[] KeepWhole =
        {
            "Nothing is selected. Click the object in Rhino, then say it again. Or name it and I will select it.",
            "Nothing is selected. Select the existing building, then mark it again.",
            "Walls first. Call walls_from_layer before roof_flat_from_walls.",
            "Walls first. Call walls_from_layer before openings_from_layer.",
            "Walls first. Call walls_from_layer before add_opening.",
            "X-EXIST is existing underlay, not a bake source.",
            "Existing underlay is not a Forsk host wall.",
            "Not an opening marker.",
            "Not a Forsk wall.",
            "Unknown view. Use plan, north, east, south, or west.",
            "Unknown view. Use plan, north, east, south, west, schedules, or a stored section (section_a).",
            "Document units must be millimetres. Switch the .3dm to millimetres."
        };

        public static string Shape(string userText, string assistant)
        {
            if (string.IsNullOrWhiteSpace(assistant)) return "";
            var text = assistant.Trim();
            foreach (var keep in KeepWhole)
            {
                if (text.StartsWith(keep, StringComparison.Ordinal))
                    return keep;
            }

            var target = new List<string>();
            var prose = new StringBuilder();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (IsTargetLine(line))
                {
                    if (target.Count < 2) target.Add(line);
                    continue;
                }
                if (prose.Length > 0) prose.Append(' ');
                prose.Append(line);
            }

            var body = TakeSentences(prose.ToString(), 2);
            var keepTarget = AsksSelection(userText) || (target.Count > 0 && body.IndexOf('?') >= 0);
            if (!keepTarget || target.Count == 0) return body;
            if (body.Length == 0) return string.Join("\n", target.ToArray());
            return string.Join("\n", target.ToArray()) + "\n" + body;
        }

        static bool AsksSelection(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return false;
            var t = user.ToLowerInvariant();
            return t.Contains("what is selected")
                || t.Contains("what's selected")
                || t.Contains("what’s selected")
                || t.Contains("whats selected")
                || t.Contains("what is the selection");
        }

        static bool IsTargetLine(string line)
        {
            if (line.StartsWith("Target (", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)) return true;
            var hasName = line.IndexOf("name=", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasLayer = line.IndexOf("layer=", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasName && hasLayer) return true;
            return line.IndexOf("bounding_box", StringComparison.OrdinalIgnoreCase) >= 0
                && line.IndexOf("id=", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string TakeSentences(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text) || max < 1) return "";
            var count = 0;
            var end = text.Length;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '.' && c != '!' && c != '?') continue;
                if (c == '.' && i > 0 && i + 1 < text.Length
                    && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                    continue;
                count++;
                if (count < max) continue;
                end = i + 1;
                break;
            }
            var cut = text.Substring(0, end).Trim();
            if (count == 0 && cut.Length > 240)
                return cut.Substring(0, 237).Trim() + "...";
            return cut;
        }
    }

    /// <summary>What the window follows during a chat turn. The old panel passes none.</summary>
    public sealed class TurnHooks
    {
        /// <summary>True while the model call is in flight, false once it answered.</summary>
        public Action<bool> Thinking;
        /// <summary>Each tool call and its envelope, after it ran: the structured receipt.</summary>
        public Action<string, JObject> Tool;
        /// <summary>The intent the router chose for the turn.</summary>
        public Action<ForskIntent> Routed;
        /// <summary>Owns the dialogs the turn opens. Null is Rhino's main window.</summary>
        public Window DialogParent;
        /// <summary>The user's role override: its tools are tried first. None keeps the pack's order.</summary>
        public ForskRole Role;
    }

    public static class ForskGrok
    {
        const string Endpoint = "https://api.x.ai/v1/chat/completions";
        const int MaxRounds = 8;
        const int MaxToolChars = 8000;

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
            }
            catch
            {
                // Rhino's Mono already speaks TLS 1.2. Keep going.
            }
            return new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public static void RunTurn(string userText, Picked picked, List<JObject> history, Action<string, string> show, TurnHooks hooks = null)
        {
            ForskSpeech.Use(userText);
            try
            {
                RunTurnBody(userText, picked, history, show, hooks);
            }
            finally
            {
                ForskSpeech.Clear();
            }
        }

        static void RunTurnBody(string userText, Picked picked, List<JObject> history, Action<string, string> show, TurnHooks hooks)
        {
            history.Add(new JObject { ["role"] = "user", ["content"] = userText });
            var intent = ForskIntentRouter.Classify(userText, picked);
            hooks?.Routed?.Invoke(intent);
            var key = ForskKeys.Load();
            if (string.IsNullOrEmpty(key))
            {
                history.Add(new JObject { ["role"] = "assistant", ["content"] = ForskKeys.Missing });
                show("assistant", ForskKeys.Missing);
                return;
            }

            var system = PanelHeader(intent) + "\n\n" + ForskPrompts.Load(intent);
            for (var round = 0; round < MaxRounds; round++)
            {
                JObject message;
                hooks?.Thinking?.Invoke(true);
                try
                {
                    message = Complete(key, system, history, ForskTools.ToolsFor(intent, hooks?.Role ?? ForskRole.None));
                }
                catch (Exception e)
                {
                    var text = ForskTools.Clip(e.Message);
                    history.Add(new JObject { ["role"] = "assistant", ["content"] = text });
                    show("assistant", text);
                    return;
                }
                finally
                {
                    hooks?.Thinking?.Invoke(false);
                }

                var content = message["content"]?.Type == JTokenType.Null
                    ? ""
                    : message["content"]?.ToString() ?? "";
                var calls = message["tool_calls"] as JArray;
                if (calls != null && calls.Count > 0)
                {
                    // The row under the bubble is the tool. Drop the preamble.
                    message["content"] = JValue.CreateNull();
                }
                else
                {
                    var shaped = ForskReply.Shape(userText, content);
                    if (string.IsNullOrWhiteSpace(shaped))
                        message["content"] = JValue.CreateNull();
                    else
                        message["content"] = shaped;
                    if (!string.IsNullOrWhiteSpace(shaped))
                        show("assistant", shaped);
                }
                history.Add(message);

                if (calls == null || calls.Count == 0)
                {
                    var shown = message["content"]?.Type == JTokenType.Null
                        ? ""
                        : message["content"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(shown))
                        show("assistant", "Done.");
                    Trim(history);
                    return;
                }

                var areaAnswer = false;
                foreach (var token in calls)
                {
                    var call = token as JObject;
                    var id = call?["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) id = "call_" + round;
                    var fn = call?["function"] as JObject;
                    var name = fn?["name"]?.ToString() ?? "";
                    var args = ParseArgs(fn?["arguments"]?.ToString());
                    // Support's pack is the only list it may call. A named edit tool does not run.
                    var envelope = ForskToolPacks.Allows(intent, name)
                        ? CallOnUi(name, args, hooks?.DialogParent)
                        : ForskTools.Fail("Support does not change the model.");
                    hooks?.Tool?.Invoke(name, envelope);
                    if (ForskArea.Answered(name, envelope?["status"]?.ToString())) areaAnswer = true;
                    if (name == ForskDxf.Tool)
                    {
                        // The tool's whole receipt, as MCP clients get it.
                        show("receipt", ForskPlanImport.DxfLine(envelope));
                        show("assistant", ForskPlanImport.DxfNote(envelope));
                    }
                    else if (name == ForskPlanImport.ImportTool)
                    {
                        // The chip's receipt: counts, then how the scale stands and what needs review.
                        show("receipt", ForskPlanImport.ImportLine(envelope));
                        show("assistant", ForskPlanImport.ReviewNote(envelope));
                    }
                    else
                        show("receipt", ForskTools.Receipt(string.IsNullOrEmpty(name) ? "tool" : name, envelope));
                    history.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["name"] = name,
                        ["content"] = Slim(envelope)
                    });
                }
                // The receipt is the answer. Another sentence repeats the net total.
                if (areaAnswer)
                {
                    Trim(history);
                    return;
                }
            }

            show("assistant", "Stopped after " + MaxRounds + " tool rounds.");
            Trim(history);
        }

        static string PanelHeader(ForskIntent intent)
        {
            var bias = intent == ForskIntent.General
                ? "No tool bias this turn: every Forsk tool is here. Follow the message."
                : "This turn is about " + IntentName(intent) + " and carries those tools. Anything else runs when the user asks for it in its own message.";
            var empty = intent == ForskIntent.Support
                ? "Nothing selected is not a refusal. Call get_document_summary, then get_objects on the layer the report is about. Do not say that nothing is selected. "
                : "Empty selection uses the refuse copy. ";
            var text = "You are answering inside the Forsk window. " + bias + " "
                + "Call the tool the user asked for. "
                + "Reply in at most two sentences: one past-tense status line, then at most three short facts. "
                + "Do not print a Target block on success. " + empty
                + "Ambiguous selection prints one Target line and asks. "
                + "Do not restate the user. Do not say you are happy to help. Do not teach unless they asked how or why. "
                + "Do not narrate tools. The window prints one receipt per tool. "
                + "A multi-step bake is one line: Floor, walls, roof, openings, rooms baked. Say defaults once. "
                + "Do not call capture_viewport unless the user asks to see the view.";
            if (intent == ForskIntent.Print || intent == ForskIntent.Sheets)
            {
                text += " When the user states project info (name, number, client, address, architect, date, revision), call set_project_meta. "
                    + "The set prints at one scale, the largest of " + SheetScale.LadderText + " at which every sheet fits A3. Naming one of those (layout_pack scale 200 means 1:200) keeps it for the next Print, and the whole set steps up the list if a sheet does not fit. Fit the scale again is layout_pack scale 0. "
                    + "For a PDF, call layout_pack if the pages are not already there, then export_pdf with path omitted. "
                    + "The panel opens a save dialog. Do not invent a path and do not ask the user to type one. "
                    + "Clear layouts is clear_layouts. Clear drawings is clear_drawings.";
            }
            if (intent == ForskIntent.Build)
            {
                text += " Rebuild, bake again, or clear and regenerate is clear_generated, then the bake order.";
            }
            if (intent == ForskIntent.Support)
            {
                text += " Answer in at most two sentences from what the tools read. Do not edit the model, the selection, or the file. A bug or a feature request ends in the window's report card; do not ask the user to file one.";
            }
            return text;
        }

        static string IntentName(ForskIntent intent)
        {
            if (intent == ForskIntent.Edit) return "Edit";
            if (intent == ForskIntent.Sheets) return "Sheets";
            if (intent == ForskIntent.Print) return "Print";
            if (intent == ForskIntent.Build) return "Build";
            if (intent == ForskIntent.Daylight) return "Daylight";
            if (intent == ForskIntent.Area) return "Area";
            if (intent == ForskIntent.Takeoff) return "Takeoff";
            if (intent == ForskIntent.Support) return "Support";
            if (intent == ForskIntent.Dxf) return "Import DXF";
            if (intent == ForskIntent.Import) return "Import";
            return "General";
        }

        static JObject CallOnUi(string name, JObject args, Window parent)
        {
            var callArgs = args;
            if (name == "export_pdf")
            {
                var path = ForskPrint.PickPathFromBackground(parent);
                if (string.IsNullOrEmpty(path))
                    return ForskTools.Fail("Print cancelled.");
                ForskPrint.SettleAfterDialog();
                callArgs = args == null ? new JObject() : (JObject)args.DeepClone();
                callArgs["path"] = path;
            }
            if (name == "export_ifc" && string.IsNullOrWhiteSpace(args?["path"]?.ToString()))
            {
                var line = ForskPrint.ExportIfc(null, parent);
                return line.StartsWith(ForskReceipt.Done, StringComparison.Ordinal)
                    ? new JObject { ["status"] = "success", ["result"] = new JObject { ["message"] = line } }
                    : ForskTools.Fail(line);
            }
            if (name == "export_csv" && string.IsNullOrWhiteSpace(args?["path"]?.ToString()))
            {
                var line = ForskPrint.ExportCsv(null, parent);
                return line.StartsWith(ForskReceipt.Done, StringComparison.Ordinal)
                    ? new JObject { ["status"] = "success", ["result"] = new JObject { ["message"] = line } }
                    : ForskTools.Fail(line);
            }
            if (name == "export_sheets")
            {
                // The model names no folder: the user picks one, and the files go in "<project> DWG" inside it.
                var format = args?["format"]?.ToString() == "dxf" ? "dxf" : "dwg";
                var line = ForskPrint.Export(null, parent, format);
                return line.StartsWith(ForskReceipt.Done, StringComparison.Ordinal)
                    ? new JObject { ["status"] = "success", ["result"] = new JObject { ["message"] = line } }
                    : ForskTools.Fail(line);
            }
            // No DXF path to open as it stands: the user picks the file.
            if (name == ForskDxf.Tool && ForskDxf.NeedsPick(args?["path"]?.ToString()))
            {
                var path = ForskPlanImport.PickDxfFromBackground(parent);
                if (string.IsNullOrEmpty(path))
                    return ForskTools.Fail("Import DXF cancelled.");
                ForskPrint.SettleAfterDialog();
                callArgs = new JObject { ["path"] = path };
            }
            // No plan to open as it stands: the user picks a PDF, an image or a DXF. A PDF of several pages asks which.
            if (name == ForskPlanImport.ImportTool && ForskPlanImport.NeedsSource(args) != null)
            {
                callArgs = ForskPlanImport.SourceFromBackground(args, parent);
                if (callArgs == null)
                    return ForskTools.Fail("Import plan cancelled.");
                ForskPrint.SettleAfterDialog();
                // A DXF picked here is dxf_import's.
                if (callArgs["dxf"] != null)
                {
                    name = ForskDxf.Tool;
                    callArgs = new JObject { ["path"] = callArgs["dxf"] };
                }
            }
            // No points given: the user picks them in the viewport and types the length.
            if (name == ForskPlanImport.ScaleTool && ForskPlanImport.NeedsPick(args))
                return ForskPlanImport.PickScaleFromBackground();
            // The tracer runs here, off the UI thread. Scene and paint hop onto it.
            if (name == ForskDaylight.ToolName)
                return ForskDaylight.Run(args?["target"]?.ToString(), ForskTools.CommandOnUi);
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                envelope = ForskTools.Execute(name, callArgs);
            }));
            return envelope ?? ForskTools.Fail("No result");
        }

        static JObject ParseArgs(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var token = JToken.Parse(json);
                return token as JObject ?? new JObject();
            }
            catch
            {
                return new JObject();
            }
        }

        static JObject Complete(string key, string system, List<JObject> history, JArray tools)
        {
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = system }
            };
            foreach (var prior in history)
                messages.Add(prior);

            var model = Environment.GetEnvironmentVariable("FORSK_GROK_MODEL");
            if (string.IsNullOrWhiteSpace(model)) model = "grok-4.7";

            var body = new JObject
            {
                ["model"] = model.Trim(),
                ["reasoning_effort"] = "low",
                ["messages"] = messages,
                ["tools"] = tools,
                ["tool_choice"] = "auto"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = Http.SendAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Grok did not answer. " + ForskTools.Clip(e.Message));
            }

            var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JObject json = null;
            try { json = JObject.Parse(payload); }
            catch { json = null; }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Grok refused the key. Check FORSK_GROK_API_KEY.");
                var err = json?["error"]?["message"]?.ToString();
                if (string.IsNullOrWhiteSpace(err))
                    err = "Grok HTTP " + (int)response.StatusCode;
                throw new InvalidOperationException(ForskTools.Clip(err));
            }

            var message = json?["choices"]?[0]?["message"] as JObject;
            if (message == null)
                throw new InvalidOperationException("Grok returned no message.");

            var stored = new JObject { ["role"] = "assistant" };
            if (message["content"] == null || message["content"].Type == JTokenType.Null || string.IsNullOrWhiteSpace(message["content"]?.ToString()))
                stored["content"] = JValue.CreateNull();
            else
                stored["content"] = message["content"].ToString();
            if (message["tool_calls"] is JArray calls && calls.Count > 0)
                stored["tool_calls"] = calls;
            return stored;
        }

        static string Slim(JObject envelope)
        {
            var copy = (JObject)envelope.DeepClone();
            SlimToken(copy);
            var text = copy.ToString(Formatting.None);
            if (text.Length <= MaxToolChars) return text;
            return text.Substring(0, MaxToolChars) + "...";
        }

        static void SlimToken(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var key in new[] { "geometry", "image_data", "image", "png_base64" })
                    obj.Remove(key);
                foreach (var prop in obj.Properties().ToList())
                {
                    if (prop.Value is JArray arr && arr.Count > 12 &&
                        (prop.Name.EndsWith("ids") || prop.Name == "deleted" || prop.Name == "points"))
                    {
                        prop.Value = new JObject { ["truncated"] = true, ["count"] = arr.Count };
                    }
                    else
                    {
                        SlimToken(prop.Value);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                    SlimToken(item);
            }
        }

        static void Trim(List<JObject> history)
        {
            while (history.Count > 24)
                history.RemoveAt(0);
            while (history.Count > 0 && history[0]["role"]?.ToString() == "tool")
                history.RemoveAt(0);
        }
    }

    public static class ForskBake
    {
        public static BakeChip Detect()
        {
            var chip = new BakeChip();
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return chip;
            foreach (var obj in doc.Objects)
            {
                if (obj?.Geometry == null || obj.Attributes == null) continue;
                var kind = obj.Attributes.GetUserString("forsk:kind");
                var generated = obj.Attributes.GetUserString("forsk:generated");
                if (generated == "1" && string.Equals(kind, "wall", StringComparison.OrdinalIgnoreCase))
                    chip.HasWalls = true;
                if (!(obj.Geometry is Curve)) continue;
                if (FileClassifier.IsPlanLayer(LayerName(doc, obj)))
                    chip.HasPlan = true;
            }
            var rows = RhinoMCPFunctions.ChipRows(doc);
            chip.ReadDaylight(rows);
            chip.ReadImport(rows);
            return chip;
        }

        public static List<string> Run(bool rebuild)
        {
            return Run(rebuild, null);
        }

        /// <summary>step(i, n) before each of the n steps, for the window's static step line.</summary>
        public static List<string> Run(bool rebuild, Action<int, int> step)
        {
            var total = rebuild ? 7 : 6;
            var at = 0;
            Action next = () => step?.Invoke(++at, total);
            var lines = new List<string>();
            var settings = LoadSettings();
            string block = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                {
                    block = "No active document.";
                    return;
                }
                var units = doc.ModelUnitSystem.ToString();
                if (!IsMillimetres(units))
                    block = "Document units must be millimetres. Switch the .3dm to millimetres.";
            }));
            if (block != null)
            {
                lines.Add(block);
                return lines;
            }

            lines.Add("Defaults: walls " + FormatMm(settings.WallHeight)
                + ", floor " + FormatMm(settings.FloorThickness)
                + ", roof " + FormatMm(settings.RoofThickness)
                + ". Origin (0,0,0) is the existing-building corner.");

            if (rebuild) next();
            if (rebuild && !Step("clear_generated", new JObject(), lines, true))
                return Finish(lines);
            next();
            if (!Step("floor_from_layer", new JObject
            {
                ["layer"] = "wall",
                ["thickness"] = settings.FloorThickness
            }, lines, true))
                return Finish(lines);
            next();
            if (!Step("walls_from_layer", new JObject
            {
                ["layer"] = "wall",
                ["height"] = settings.WallHeight
            }, lines, true))
                return Finish(lines);
            next();
            if (!Step("roof_flat_from_walls", new JObject
            {
                ["thickness"] = settings.RoofThickness
            }, lines, true))
                return Finish(lines);

            next();
            Opening("door", lines);
            next();
            Opening("window", lines);
            next();
            var rooms = Call("rooms_from_layer", new JObject());
            lines.Add(ForskTools.Receipt("rooms_from_layer", rooms));
            // Drawn outlines already became markers when the count is above zero.
            if (BakeChip.ShouldDetectRooms(ResultCount(rooms)))
                Step("rooms_detect", new JObject(), lines, false);
            // An existing file's private window-06-block definitions fold into the shared ones.
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                BakePace.Hold(doc);
                using (BakePace.Time(BakePhases.Openings))
                    new RhinoMCPFunctions().CollapseOpeningBlocks(doc);
            }));
            return Finish(lines);
        }

        static int? ResultCount(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return null;
            return (envelope["result"] as JObject)?["count"]?.ToObject<int?>();
        }

        static List<string> Finish(List<string> lines)
        {
            // The pass redraws once, when it restores RedrawEnabled. A bake with no pass still redraws.
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                BakePace.Redraw(RhinoDoc.ActiveDoc);
            }));
            return lines;
        }

        static string PhaseOf(string name)
        {
            if (name == "walls_from_layer" || name == "floor_from_layer" || name == "roof_flat_from_walls")
                return BakePhases.Walls;
            if (name == "openings_from_layer")
                return BakePhases.Openings;
            if (name == "rooms_from_layer" || name == "rooms_detect")
                return BakePhases.Rooms;
            return null;
        }

        static bool Step(string name, JObject args, List<string> lines, bool stopOnZero)
        {
            var envelope = Call(name, args);
            lines.Add(ForskTools.Receipt(name, envelope));
            if (!string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!stopOnZero) return true;
            var result = envelope["result"] as JObject;
            var count = result?["count"]?.ToObject<int?>();
            if (count.HasValue && count.Value == 0) return false;
            var message = result?["message"]?.ToString() ?? "";
            if (message.IndexOf("not a bake source", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("Walls first", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        static void Opening(string layer, List<string> lines)
        {
            var envelope = Call("openings_from_layer", new JObject { ["layer"] = layer });
            var message = envelope["message"]?.ToString() ?? "";
            if (!string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)
                && message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lines.Add("openings_from_layer · skipped · no " + layer + " layer");
                return;
            }
            var receipt = ForskTools.Receipt("openings_from_layer", envelope);
            lines.Add(receipt.Replace("openings_from_layer", "openings_from_layer " + layer));
        }

        static JObject Call(string name, JObject args)
        {
            JObject envelope = null;
            var phase = PhaseOf(name);
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                BakePace.Hold(RhinoDoc.ActiveDoc);
                using (BakePace.Time(phase))
                    envelope = ForskTools.ExecuteAllowed(name, args);
            }));
            return envelope ?? ForskTools.Fail("No result");
        }

        static bool IsMillimetres(string units)
        {
            if (string.IsNullOrEmpty(units)) return false;
            return units.Equals("Millimeters", StringComparison.OrdinalIgnoreCase)
                || units.Equals("Millimetres", StringComparison.OrdinalIgnoreCase);
        }

        static string FormatMm(double value)
        {
            return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        static BakeNumbers LoadSettings()
        {
            var numbers = new BakeNumbers();
            var root = ForskPrompts.FindRoot();
            if (root == null) return numbers;
            var path = Path.Combine(root, "templates", "model_settings.json");
            if (!File.Exists(path)) return numbers;
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                numbers.WallHeight = json["wall_height"]?.ToObject<double?>() ?? numbers.WallHeight;
                numbers.FloorThickness = json["floor_thickness"]?.ToObject<double?>() ?? numbers.FloorThickness;
                numbers.RoofThickness = json["roof_flat_thickness"]?.ToObject<double?>() ?? numbers.RoofThickness;
            }
            catch
            {
                // Unreadable settings file: the same defaults the bake tools use.
            }
            return numbers;
        }

        static string LayerName(RhinoDoc doc, RhinoObject obj)
        {
            var index = obj.Attributes.LayerIndex;
            if (index < 0 || index >= doc.Layers.Count) return "";
            return doc.Layers[index].Name ?? "";
        }

        sealed class BakeNumbers
        {
            public double WallHeight = 3000;
            public double FloorThickness = 400;
            public double RoofThickness = 200;
        }
    }

    /// <summary>
    /// Same path for the Print PDF chip and for chat print / make PDF / skriv ut.
    /// The save dialog picks the path.
    /// </summary>
    public static class ForskPrint
    {
        const string MetaSection = ProjectInfo.Section;

        public static bool IsRequest(string text)
        {
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 160) return false;
            if (t.Contains("clear")) return false;
            if (t.Contains("project is") || t.Contains("client") || t.Contains("address")) return false;
            if (t.Contains("make sheet") || t.Contains("tegning") || t.Contains("drawing")) return false;
            if (t.Contains("make2d") || t.Contains("sheet_pack")) return false;
            // Print in a profile is the chat's: print_profile, then Print.
            if (t.Contains("profile") || t.Contains("profil") || t.Contains("poch")) return false;

            if (t == "pdf" || t == "print" || t == "print pdf" || t == "skriv ut") return true;
            if (HasWord(t, "print")) return true;
            if (t.Contains("skriv ut")) return true;
            if (t.Contains("make pdf") || t.Contains("make a pdf") || t.Contains("make the pdf")) return true;
            return false;
        }

        public static string Run()
        {
            return Run(null);
        }

        public static string Run(Action<string> progress)
        {
            return Run(progress, null);
        }

        /// <summary>parent owns the save dialog, so it opens where the user is looking. Null is Rhino's main window.</summary>
        public static string Run(Action<string> progress, Window parent)
        {
            return Run(progress, parent, null);
        }

        /// <summary>view: one sheet (plan, north, …, schedules, section_a), or null for the whole set.</summary>
        public static string Run(Action<string> progress, Window parent, string view)
        {
            var units = UnitsProblem();
            if (units != null)
                return "Print PDF · error · " + units;

            var pack = Call("layout_pack", view == null ? new JObject() : new JObject { ["views"] = new JArray(view) });
            if (!Ok(pack)) return FailLine(pack);
            var packed = pack["result"] as JObject;

            Report(progress, "Layouts ready — choose where to save.");
            var path = PickPathFromBackground(parent);
            if (string.IsNullOrEmpty(path))
                return "Print PDF · cancelled";

            // The layout capture runs after this returns, with Rhino focused.
            SettleAfterDialog();
            var exportArgs = new JObject { ["path"] = path };
            if (view != null) exportArgs["layout"] = view;
            var exported = Call("export_pdf", exportArgs);
            if (!Ok(exported)) return FailLine(exported);

            var result = exported["result"] as JObject;
            var written = result?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(written)) written = path;
            var message = result?["message"]?.ToString() ?? "";
            var blankAt = message.IndexOf("Blank preview:", StringComparison.Ordinal);
            var blank = blankAt >= 0 ? " · " + ForskTools.Clip(message.Substring(blankAt)) : "";
            // N3: the vector write failed and the page previews stood in.
            if (result?["vector"]?.Type == JTokenType.Boolean && !result["vector"].Value<bool>()) blank = " (raster fallback)" + blank;
            var sheets = packed?["sheets"]?.Value<int>() ?? result?["count"]?.Value<int>() ?? 0;
            var scale = (packed?["drawings"]?.Value<int>() ?? 0) > 0 ? packed["scale"]?.Value<int>() ?? 0 : 0;
            var detailScales = (packed?["detail_scales"] as JArray)?.Select(s => s.Value<int>()).ToList();
            // N2: the whole set's Print writes the takeoff beside the PDF. One sheet does not.
            string csv = null;
            if (view == null)
                csv = WriteCsv(Path.Combine(Path.GetDirectoryName(written) ?? "", TakeoffCsv.FileName(Path.GetFileNameWithoutExtension(written))));
            return ForskReceipt.PrintLine(sheets, scale, written, StoredMeta("revision"), packed?["bumped"]?.ToString(), blank,
                detailScales, packed?["details_dropped"]?.Value<int>() ?? 0, csv);
        }

        /// <summary>export_csv at path: "" when written, else why not. A failed CSV never fails the Print or the export.</summary>
        static string WriteCsv(string path)
        {
            try
            {
                var envelope = Call("export_csv", new JObject { ["path"] = path });
                if (Ok(envelope) && !string.IsNullOrWhiteSpace((envelope["result"] as JObject)?["path"]?.ToString())) return "";
                var message = (envelope?["result"] as JObject)?["message"]?.ToString() ?? envelope?["message"]?.ToString();
                return string.IsNullOrWhiteSpace(message) ? "export failed" : message;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>N2: the takeoff as a CSV. A save dialog named "&lt;project&gt; Takeoff.csv", then export_csv. One line back.</summary>
        public static string ExportCsv(Action<string> progress, Window parent)
        {
            Report(progress, "Choose where to save the CSV.");
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        var dialog = new Eto.Forms.SaveFileDialog
                        {
                            Title = "Export CSV",
                            FileName = TakeoffCsv.FileName(Sanitize(StoredMeta("project"))),
                            CheckFileExists = false
                        };
                        dialog.Filters.Add(new FileFilter("CSV", ".csv"));
                        TrySetDesktop(dialog);
                        if (dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) == DialogResult.Ok)
                            path = AbsoluteWith(dialog.FileName, dialog.Directory, ".csv");
                    }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            if (string.IsNullOrEmpty(path))
                return "Export CSV · cancelled";
            Report(progress, "Writing the CSV.");
            var envelope = Call("export_csv", new JObject { ["path"] = path });
            var result = envelope?["result"] as JObject;
            var message = result?["message"]?.ToString() ?? envelope?["message"]?.ToString() ?? "";
            if (!Ok(envelope) || string.IsNullOrWhiteSpace(result?["path"]?.ToString()))
                return "Export CSV · error · " + ForskTools.Clip(string.IsNullOrWhiteSpace(message) ? "Export failed." : message);
            return message;
        }

        /// <summary>
        /// R3: the set as DWG or DXF. A folder dialog parented to the window, then
        /// export_sheets into "<project> DWG" inside it: it lays the set out as
        /// Print does and writes one file per sheet. One line back.
        /// </summary>
        public static string Export(Action<string> progress, Window parent, string format)
        {
            var label = format == "dxf" ? "Export DXF" : "Export DWG";
            var units = UnitsProblem();
            if (units != null)
                return label + " · error · " + units;

            Report(progress, "Choose a folder for the files.");
            var picked = PickFolderFromBackground(parent, label);
            if (string.IsNullOrEmpty(picked))
                return label + " · cancelled";

            var folder = Path.Combine(picked, SheetFlat.FolderName(StoredMeta("project"), format));
            Report(progress, "Laying out and writing the sheets.");
            var envelope = Call("export_sheets", new JObject { ["folder"] = folder, ["format"] = format });
            var result = envelope?["result"] as JObject;
            var count = result?["count"]?.Value<int>() ?? 0;
            var message = result?["message"]?.ToString() ?? envelope?["message"]?.ToString() ?? "";
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)
                || count == 0 || !message.StartsWith("Exported", StringComparison.Ordinal))
                return label + " · error · " + ForskTools.Clip(string.IsNullOrWhiteSpace(message) ? "Export failed." : message);
            var written = result?["folder"]?.ToString() ?? folder;
            // N2: the takeoff goes into the same folder.
            var csv = WriteCsv(Path.Combine(written, TakeoffCsv.FileName(Sanitize(StoredMeta("project")))));
            return ForskReceipt.ExportLine(count, format, written, csv);
        }

        /// <summary>R4: the model as IFC. A save dialog parented to the window, then export_ifc. One line back.</summary>
        public static string ExportIfc(Action<string> progress, Window parent)
        {
            Report(progress, "Choose where to save the IFC.");
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        var dialog = new Eto.Forms.SaveFileDialog { Title = "Export IFC", FileName = IfcFileName(), CheckFileExists = false };
                        dialog.Filters.Add(new FileFilter("IFC", ".ifc"));
                        TrySetDesktop(dialog);
                        if (dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) == DialogResult.Ok)
                            path = AbsoluteWith(dialog.FileName, dialog.Directory, ".ifc");
                    }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            if (string.IsNullOrEmpty(path))
                return "Export IFC · cancelled";
            Report(progress, "Writing the IFC.");
            var envelope = Call("export_ifc", new JObject { ["path"] = path });
            var result = envelope?["result"] as JObject;
            var message = result?["message"]?.ToString() ?? envelope?["message"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(result?["path"]?.ToString()))
                return "Export IFC · error · " + ForskTools.Clip(string.IsNullOrWhiteSpace(message) ? "Export failed." : message);
            return message;
        }

        static string IfcFileName()
        {
            var stem = Sanitize(StoredMeta("project"));
            return (stem.Length == 0 ? "forsk" : stem) + ".ifc";
        }

        /// <summary>The dialog's name made absolute in its folder, with the extension.</summary>
        static string AbsoluteWith(string fileName, Uri directory, string extension)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var path = fileName.Trim();
            if (!Path.IsPathRooted(path))
            {
                string dir = null;
                try { if (directory != null && directory.IsAbsoluteUri) dir = directory.LocalPath; }
                catch { dir = null; }
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                path = Path.Combine(dir ?? "", Path.GetFileName(path));
            }
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }

        static string PickFolderFromBackground(Window parent, string title)
        {
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        var dialog = new SelectFolderDialog { Title = title };
                        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) dialog.Directory = desktop;
                        if (dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) == DialogResult.Ok)
                            path = dialog.Directory;
                    }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        static void Report(Action<string> progress, string status)
        {
            if (progress == null || string.IsNullOrWhiteSpace(status)) return;
            try
            {
                progress(status);
            }
            catch (Exception)
            {
                // A status line must not replace the receipt.
            }
        }

        public static string PickPathFromBackground()
        {
            return PickPathFromBackground(null);
        }

        public static string PickPathFromBackground(Window parent)
        {
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try { path = PickPath(parent); }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            return path;
        }

        public static void SettleAfterDialog()
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try
                {
                    var window = RhinoEtoApp.MainWindow;
                    if (window != null)
                        window.Focus();
                }
                catch (Exception)
                {
                }
                RhinoApp.Wait();
            }));
        }

        public static string PickPath(Window parent)
        {
            var dialog = new Eto.Forms.SaveFileDialog
            {
                Title = "Print PDF",
                FileName = DefaultFileName(),
                CheckFileExists = false
            };
            dialog.Filters.Add(new FileFilter("PDF", ".pdf"));
            TrySetDesktop(dialog);
            if (dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) != DialogResult.Ok)
                return null;
            return AbsolutePdf(dialog.FileName, dialog.Directory);
        }

        /// <summary>The architect saved on this Mac, which prefills an empty Architect on the Project info card.</summary>
        public static string FirmArchitect()
        {
            try { return global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.GetString(ProjectInfo.ArchitectSetting, "") ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Each Save of the Project info card keeps its architect for the next file. Empty keeps the last one.</summary>
        public static void SaveFirmArchitect(string architect)
        {
            if (string.IsNullOrWhiteSpace(architect)) return;
            try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(ProjectInfo.ArchitectSetting, architect.Trim()); }
            catch (Exception) { }
        }

        /// <summary>A title block value stored on the document, or null.</summary>
        static string StoredMeta(string key)
        {
            string value = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null) value = doc.Strings.GetValue(MetaSection, key);
            }));
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static string DefaultFileName()
        {
            var doc = RhinoDoc.ActiveDoc;
            var project = doc == null ? "" : doc.Strings.GetValue(MetaSection, "project");
            var stem = Sanitize(project);
            if (stem.Length == 0) return "forsk-print.pdf";
            return stem + ".pdf";
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name.Trim())
            {
                if (ch == '/' || ch == '\\' || Array.IndexOf(invalid, ch) >= 0)
                    sb.Append('-');
                else
                    sb.Append(ch);
            }
            var text = sb.ToString().Trim().Trim('.');
            if (text.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 4).Trim().Trim('.');
            if (text.Length > 80) text = text.Substring(0, 80).Trim();
            return text;
        }

        static void TrySetDesktop(Eto.Forms.SaveFileDialog dialog)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return;
                var full = Path.GetFullPath(desktop);
                if (!full.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    full += Path.DirectorySeparatorChar;
                dialog.Directory = new Uri("file://" + full);
            }
            catch
            {
                // The dialog still opens. The user picks the folder.
            }
        }

        static string AbsolutePdf(string fileName, Uri directory)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var path = fileName.Trim();
            if (!Path.IsPathRooted(path))
            {
                string dir = null;
                try
                {
                    if (directory != null && directory.IsAbsoluteUri)
                        dir = directory.LocalPath;
                }
                catch
                {
                    dir = null;
                }
                if (string.IsNullOrEmpty(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                path = Path.Combine(dir ?? "", Path.GetFileName(path));
            }
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                path += ".pdf";
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }

        static string UnitsProblem()
        {
            string problem = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                {
                    problem = "No active document.";
                    return;
                }
                var units = doc.ModelUnitSystem.ToString();
                if (units.Equals("Millimeters", StringComparison.OrdinalIgnoreCase)) return;
                if (units.Equals("Millimetres", StringComparison.OrdinalIgnoreCase)) return;
                problem = "Document units must be millimetres. Switch the .3dm to millimetres.";
            }));
            return problem;
        }

        static JObject Call(string name, JObject args)
        {
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                envelope = ForskTools.ExecuteAllowed(name, args);
            }));
            return envelope ?? ForskTools.Fail("No result");
        }

        static bool Ok(JObject envelope)
        {
            if (envelope == null) return false;
            if (!string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return false;
            var result = envelope["result"] as JObject;
            if (result == null) return true;
            var message = result["message"]?.ToString() ?? "";
            if (message.IndexOf("Nothing to lay out", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("PDF write failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("No layouts to print", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("does not show the clay", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("does not show the drawing", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("Hidden line drawing failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("No visible curves", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("capture failed after activate/Wait", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("requires a file path", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("must be an absolute", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("Unknown paper", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (message.IndexOf("Unknown view", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            var count = result["count"];
            int n;
            if (count != null && count.Type != JTokenType.Null
                && int.TryParse(count.ToString(), out n) && n == 0 && message.Length > 0)
                return false;
            return true;
        }

        static string FailLine(JObject envelope)
        {
            var message = envelope?["message"]?.ToString();
            var result = envelope?["result"] as JObject;
            if (string.IsNullOrWhiteSpace(message))
                message = result?["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message))
                message = "Print failed.";
            return "Print PDF · error · " + ForskTools.Clip(message);
        }

        static string Normalize(string text)
        {
            var raw = (text ?? "").Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            var space = false;
            foreach (var ch in raw)
            {
                if (char.IsWhiteSpace(ch))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (ch == '.' || ch == '!' || ch == '?') continue;
                if (space)
                {
                    sb.Append(' ');
                    space = false;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        static bool HasWord(string text, string word)
        {
            var i = 0;
            while (i < text.Length)
            {
                var at = text.IndexOf(word, i, StringComparison.Ordinal);
                if (at < 0) return false;
                var left = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                var end = at + word.Length;
                var right = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (left && right) return true;
                i = at + word.Length;
            }
            return false;
        }
    }
}
