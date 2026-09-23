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
    public enum ForskMode
    {
        Build,
        Edit,
        Sheets
    }

    public sealed class BakeChip
    {
        public bool HasPlan;
        public bool HasWalls;
        public bool Visible => HasPlan;
        public string Label => HasWalls ? "Rebuild 3D" : "Generate 3D model";
    }

    /// <summary>
    /// In-process dispatch onto the existing [McpCommand] handlers.
    /// Geometry stays in those handlers. This class only filters and wraps undo.
    /// Call Execute on the UI thread.
    /// </summary>
    public static class ForskTools
    {
        static readonly RhinoMCPFunctions Handler = new RhinoMCPFunctions();
        static readonly Dictionary<string, JObject> Catalog = BuildCatalog();

        static readonly string[] Shared =
        {
            "get_document_summary",
            "get_selected_objects_info",
            "get_object_info",
            "select_objects",
            "capture_viewport"
        };

        static readonly string[] BuildOnly =
        {
            "get_objects",
            "clear_generated",
            "floor_from_layer",
            "walls_from_layer",
            "roof_flat_from_walls",
            "openings_from_layer",
            "rooms_from_layer",
            "mark_as_existing"
        };

        static readonly string[] EditOnly =
        {
            "add_opening",
            "move_opening",
            "delete_opening"
        };

        static readonly string[] SheetsOnly =
        {
            "get_objects",
            "sheet_pack",
            "make2d_view",
            "clear_drawings",
            "set_project_meta",
            "layout_pack",
            "export_pdf",
            "clear_layouts"
        };

        public static JArray ToolsFor(ForskMode mode)
        {
            var names = new List<string>(Shared);
            if (mode == ForskMode.Build) names.AddRange(BuildOnly);
            else if (mode == ForskMode.Edit) names.AddRange(EditOnly);
            else names.AddRange(SheetsOnly);

            var tools = new JArray();
            foreach (var name in names)
            {
                if (Catalog.TryGetValue(name, out var tool))
                    tools.Add(tool);
            }
            return tools;
        }

        public static JObject Execute(string name, JObject parameters, ForskMode mode)
        {
            if (!Allowed(name, mode))
            {
                return Fail("Tool " + name + " is not available in " + ModeName(mode) + " mode.");
            }
            return ExecuteAllowed(name, parameters);
        }

        public static JObject ExecuteAllowed(string name, JObject parameters)
        {
            if (!Catalog.ContainsKey(name))
                return Fail("Tool " + name + " is not available in the Forsk panel.");
            return Dispatch(name, parameters);
        }

        public static string Receipt(string name, JObject envelope)
        {
            if (envelope == null) return name + " · error";
            var status = envelope["status"]?.ToString();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var err = envelope["message"]?.ToString();
                return name + " · error · " + Clip(string.IsNullOrEmpty(err) ? "failed" : err);
            }

            var result = envelope["result"] as JObject;
            if (result == null) return name + " · ok";

            var countTok = result["count"] ?? result["opening_count"] ?? result["cut_count"];
            var message = result["message"]?.ToString();
            if (countTok != null)
            {
                var count = countTok.ToString();
                if (count == "0" && !string.IsNullOrEmpty(message))
                    return name + " · ok · 0 · " + Clip(message);
                return name + " · ok · " + count;
            }
            if (!string.IsNullOrEmpty(message))
                return name + " · ok · " + Clip(message);
            return name + " · ok";
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

        public static string ModeName(ForskMode mode)
        {
            if (mode == ForskMode.Edit) return "Edit";
            if (mode == ForskMode.Sheets) return "Sheets";
            return "Build";
        }

        static bool Allowed(string name, ForskMode mode)
        {
            if (string.IsNullOrEmpty(name) || !Catalog.ContainsKey(name)) return false;
            if (Array.IndexOf(Shared, name) >= 0) return true;
            if (mode == ForskMode.Build) return Array.IndexOf(BuildOnly, name) >= 0;
            if (mode == ForskMode.Edit) return Array.IndexOf(EditOnly, name) >= 0;
            return Array.IndexOf(SheetsOnly, name) >= 0;
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

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return Fail("No active document.");

            uint record = 0;
            var undo = !entry.ReadOnly;
            if (undo) record = doc.BeginUndoRecord("Forsk: " + name);
            try
            {
                var result = entry.Handler(parameters);
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
                if (undo) doc.EndUndoRecord(record);
            }
        }

        static JObject Fn(string name, string description, JObject props, params string[] required)
        {
            var parameters = new JObject
            {
                ["type"] = "object",
                ["properties"] = props ?? new JObject(),
                ["additionalProperties"] = false
            };
            if (required != null && required.Length > 0)
                parameters["required"] = new JArray(required);
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters
                }
            };
        }

        static JObject Str(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        static JObject Num(string description)
        {
            return new JObject { ["type"] = "number", ["description"] = description };
        }

        static JObject Bool(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }

        static JObject ViewEnum(string description)
        {
            return new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("plan", "north", "east", "south", "west"),
                ["description"] = description
            };
        }

        static Dictionary<string, JObject> BuildCatalog()
        {
            var tools = new[]
            {
                Fn("get_document_summary",
                    "Document counts, layers, and meta_data.units. Call before creating geometry. Accept only Millimeters or Millimetres.",
                    new JObject()),
                Fn("get_selected_objects_info",
                    "Current viewport selection. Always the target for this, it, selected. attributes holds forsk:kind, forsk:id, forsk:host.",
                    new JObject { ["include_attributes"] = Bool("Include user strings. The panel forces true.") }),
                Fn("get_object_info",
                    "One object by id or name, including forsk user strings.",
                    new JObject
                    {
                        ["id"] = Str("Object GUID."),
                        ["name"] = Str("Object name, if id is omitted.")
                    }),
                Fn("get_objects",
                    "List objects. Pass include_geometry false. Use layer_filter A-WALL, A-ROOM, A-OPEN, or S-PLAN. Does not replace selection.",
                    new JObject
                    {
                        ["layer_filter"] = Str("Layer name."),
                        ["limit"] = new JObject { ["type"] = "integer", ["description"] = "Max rows. Default 30 in the panel." },
                        ["include_geometry"] = Bool("The panel forces false.")
                    }),
                Fn("select_objects",
                    "Select by name. filters.name is an array of names. Then call get_selected_objects_info again. Empty filters are forbidden.",
                    new JObject
                    {
                        ["filters"] = new JObject
                        {
                            ["type"] = "object",
                            ["description"] = "name: string array.",
                            ["properties"] = new JObject
                            {
                                ["name"] = new JObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JObject { ["type"] = "string" }
                                }
                            }
                        }
                    },
                    "filters"),
                Fn("capture_viewport",
                    "Screenshot of a viewport. Call only when the user asks to see the view.",
                    new JObject
                    {
                        ["viewport"] = Str("active, perspective, top, front, or a named view. Default active.")
                    }),
                Fn("clear_generated",
                    "Delete generated walls, floor, roof, openings, and rooms. Leaves X-EXIST, forsk:kind=existing, and drawings.",
                    new JObject
                    {
                        ["include_untagged_prefixes"] = Bool("True only for pre-tag leftover solids. Default false.")
                    }),
                Fn("floor_from_layer",
                    "Floor slab from the outermost closed curve on the plan layer. Extrudes down. Default layer wall, thickness 400, target A-FLOR. X-EXIST is not a bake source.",
                    new JObject
                    {
                        ["layer"] = Str("Source layer. Default wall."),
                        ["thickness"] = Num("Slab thickness in mm. Default 400.")
                    }),
                Fn("walls_from_layer",
                    "Extrude closed plan curves to wall solids. Default layer wall, height 3000, target A-WALL. Stamps forsk:id w01… and forsk:height.",
                    new JObject
                    {
                        ["layer"] = Str("Source layer. Default wall."),
                        ["height"] = Num("Wall height in mm. Default 3000.")
                    }),
                Fn("roof_flat_from_walls",
                    "Flat roof from the wall outline. Requires Forsk walls. Default thickness 200 on A-ROOF.",
                    new JObject
                    {
                        ["thickness"] = Num("Slab thickness in mm. Default 200.")
                    }),
                Fn("openings_from_layer",
                    "Cut doors or windows from wall solids and add A-OPEN markers. layer is door or window. Requires walls first.",
                    new JObject
                    {
                        ["layer"] = Str("door or window."),
                        ["sill"] = Num("Bottom Z. Default 0 door, 900 window."),
                        ["head"] = Num("Top Z. Default 2100.")
                    },
                    "layer"),
                Fn("rooms_from_layer",
                    "Planar room markers from closed curves on A-ROOM (alias room). Count 0 if the layer is missing. Does not invent rooms from walls.",
                    new JObject
                    {
                        ["layer"] = Str("Source layer. Default A-ROOM.")
                    }),
                Fn("mark_as_existing",
                    "Move the selection onto X-EXIST and stamp forsk:kind=existing. Empty selection is refused.",
                    new JObject()),
                Fn("add_opening",
                    "Cut a door or window into one Forsk wall and add a marker. host_id omitted uses the selection. Refuses X-EXIST hosts.",
                    new JObject
                    {
                        ["opening_kind"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("door", "window"),
                            ["description"] = "door or window."
                        },
                        ["host_id"] = Str("Host wall GUID. Omit to use the selection."),
                        ["width"] = Num("Width in mm. Default 900 door, 1200 window."),
                        ["sill"] = Num("Bottom Z."),
                        ["head"] = Num("Top Z."),
                        ["t"] = Num("0–1 along the facade segment. Exclusive with distance_mm."),
                        ["distance_mm"] = Num("Distance from the segment start. Exclusive with t.")
                    },
                    "opening_kind"),
                Fn("move_opening",
                    "Slide one opening along its host. delta_mm is signed millimetres. Id omitted uses the selected marker. Refuses X-EXIST.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker GUID. Omit to use the selection."),
                        ["delta_mm"] = Num("Signed distance along the wall, mm."),
                        ["t"] = Num("Absolute 0–1 position. Exclusive with delta_mm.")
                    }),
                Fn("delete_opening",
                    "Close one opening and delete its marker. Id omitted uses the selected marker. Refuses X-EXIST.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker GUID. Omit to use the selection.")
                    }),
                Fn("sheet_pack",
                    "Make2D plan plus north, east, south, and west on S-* layers. Model space, not a Layout page.",
                    new JObject
                    {
                        ["include_existing"] = Bool("Include X-EXIST in the silhouette. Default true.")
                    }),
                Fn("make2d_view",
                    "One Make2D view: plan, north, east, south, or west. Empty clay returns count 0.",
                    new JObject
                    {
                        ["view"] = ViewEnum("plan, north, east, south, or west.")
                    },
                    "view"),
                Fn("clear_drawings",
                    "Delete forsk:kind=drawing curves. Optional views filter. Does not delete walls or X-EXIST.",
                    new JObject
                    {
                        ["views"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = ViewEnum("A view to clear."),
                            ["description"] = "Omit to clear every drawing."
                        }
                    }),
                Fn("set_project_meta",
                    "Store project, client, address, date, and scale label for the Layout title block. Omitted keys stay. An empty string clears that key. Call when the user states project, client, or address.",
                    new JObject
                    {
                        ["project"] = Str("Project name."),
                        ["client"] = Str("Client name."),
                        ["address"] = Str("Site address."),
                        ["date"] = Str("Date yyyy-MM-dd. Unset reads as today."),
                        ["scale_label"] = Str("Title scale text. Unset reads as 1:100.")
                    }),
                Fn("layout_pack",
                    "A3 Layout pages of a greyscale drawing. One Detail per view shows black S-DRAW curves, plus a title block bottom-right. Not a PDF. Requires walls. Default views are plan, north, east, south, and west.",
                    new JObject
                    {
                        ["paper"] = Str("A3 only. Default A3."),
                        ["views"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = ViewEnum("A view to lay out."),
                            ["description"] = "Omit for plan and four elevations."
                        },
                        ["scale"] = Num("Requested scale denominator. 100 means 1:100."),
                        ["replace"] = Bool("Replace Forsk pages for these views. Default true."),
                        ["include_existing"] = Bool("Include X-EXIST in the greyscale drawing. Default true.")
                    }),
                Fn("export_pdf",
                    "Write Forsk Layout pages to a PDF. In this panel, omit path. A save dialog supplies the absolute .pdf path. Do not invent a path and do not ask the user to type one. Call layout_pack first.",
                    new JObject
                    {
                        ["path"] = Str("Omit in the panel. The save dialog sets an absolute .pdf path."),
                        ["layout"] = Str("Optional page name or view: plan, north, east, south, west. Omit for every Forsk page.")
                    }),
                Fn("clear_layouts",
                    "Delete Forsk Layout pages, their title blocks, and the greyscale S-DRAW curves. Does not delete clay, X-EXIST, or the S-PLAN / S-ELEV sheet cache. clear_generated also leaves layouts.",
                    new JObject
                    {
                        ["views"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = ViewEnum("A view to clear."),
                            ["description"] = "Omit to clear every Forsk layout."
                        },
                        ["dry_run"] = Bool("List matches without deleting. Default false.")
                    })
            };

            var map = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                var name = tool["function"]?["name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                    map[name] = tool;
            }
            return map;
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

Defaults: walls 3000, floor thickness 400, roof 200, doors sill 0 head 2100 width 900, windows sill 900 head 2100 width 1200. Pass stated heights as tool params. If the user states none, use the defaults and say so once.

Never bake from layer X-EXIST. Refuse: X-EXIST is existing underlay, not a bake source.
Never edit an opening on X-EXIST or forsk:kind=existing. Refuse: Existing underlay is not a Forsk host wall.
Roof or openings before walls: Walls first. Call walls_from_layer before roof_flat_from_walls. Or the openings / add_opening line with the same shape.

Delete this door or window is delete_opening. Add is add_opening. Move millimetres along the wall is move_opening delta_mm.

Sheets prefers Layout pages and a PDF. Print, make PDF, or skriv ut opens a save dialog. Do not invent a file path. set_project_meta stores project, client, and address. layout_pack bakes black S-DRAW curves and makes the pages. clear_layouts removes those pages and the S-DRAW curves. Sheet cache on S-PLAN and S-ELEV stays: sheet_pack, make2d_view, clear_drawings. Never clear_generated for drawings or layouts.

Do not call Grasshopper tools or execute code. Reply in one or two sentences. The panel prints tool receipts.";

        public static string Warning { get; private set; }

        public static string Load(ForskMode mode)
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

            var pack = LoadPack(root, mode);
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

        static string LoadPack(string root, ForskMode mode)
        {
            var file = mode == ForskMode.Edit ? "ui_edit.md"
                : mode == ForskMode.Sheets ? "ui_sheets.md"
                : "ui_build.md";
            if (root != null)
            {
                var path = Path.Combine(root, "prompts", "packs", file);
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            if (mode == ForskMode.Edit)
            {
                return "Mode: Edit. Selection first. add_opening, move_opening, delete_opening only. "
                    + "Refuse X-EXIST hosts with: Existing underlay is not a Forsk host wall. "
                    + "Bake and sheets belong on the other chips.";
            }
            if (mode == ForskMode.Sheets)
            {
                return "Mode: Sheets. Delivery is Layout pages plus a PDF. "
                    + "Print PDF opens a save dialog. Do not invent a file path. "
                    + "set_project_meta, layout_pack, export_pdf, clear_layouts. "
                    + "Make2D stays available: sheet_pack, make2d_view, clear_drawings. "
                    + "Clear layouts with clear_layouts. Clear drawings with clear_drawings. Never clear_generated.";
            }
            return "Mode: Build. Bake, heights, tilbygg, clear_generated. "
                + "Order: floor, walls, roof, openings, rooms. "
                + "Refuse X-EXIST as a bake source.";
        }
    }

    public static class ForskKeys
    {
        public const string Missing =
            "Set FORSK_GROK_API_KEY to chat. Export it before launching Rhino, or put FORSK_GROK_API_KEY=… in ~/.forsk/grok.env. Generate 3D and Print PDF still run without a key.";

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

        public static void RunTurn(string userText, ForskMode mode, List<JObject> history, Action<string, string> show)
        {
            history.Add(new JObject { ["role"] = "user", ["content"] = userText });
            var key = ForskKeys.Load();
            if (string.IsNullOrEmpty(key))
            {
                history.Add(new JObject { ["role"] = "assistant", ["content"] = ForskKeys.Missing });
                show("assistant", ForskKeys.Missing);
                return;
            }

            var system = PanelHeader(mode) + "\n\n" + ForskPrompts.Load(mode);
            for (var round = 0; round < MaxRounds; round++)
            {
                JObject message;
                try
                {
                    message = Complete(key, system, history, ForskTools.ToolsFor(mode));
                }
                catch (Exception e)
                {
                    var text = ForskTools.Clip(e.Message);
                    history.Add(new JObject { ["role"] = "assistant", ["content"] = text });
                    show("assistant", text);
                    return;
                }

                var content = message["content"]?.Type == JTokenType.Null
                    ? ""
                    : message["content"]?.ToString() ?? "";
                var calls = message["tool_calls"] as JArray;
                history.Add(message);
                if (!string.IsNullOrWhiteSpace(content))
                    show("assistant", content.Trim());

                if (calls == null || calls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(content))
                        show("assistant", "Done.");
                    Trim(history);
                    return;
                }

                foreach (var token in calls)
                {
                    var call = token as JObject;
                    var id = call?["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) id = "call_" + round;
                    var fn = call?["function"] as JObject;
                    var name = fn?["name"]?.ToString() ?? "";
                    var args = ParseArgs(fn?["arguments"]?.ToString());
                    var envelope = CallOnUi(name, args, mode);
                    show("receipt", ForskTools.Receipt(string.IsNullOrEmpty(name) ? "tool" : name, envelope));
                    history.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["name"] = name,
                        ["content"] = Slim(envelope)
                    });
                }
            }

            show("assistant", "Stopped after " + MaxRounds + " tool rounds.");
            Trim(history);
        }

        static string PanelHeader(ForskMode mode)
        {
            var text = "You are answering inside the Forsk panel. Active mode: " + ForskTools.ModeName(mode) + ". "
                + "Call only the tools offered this turn. The user already sees a one-line receipt per tool; reply in one or two sentences, not raw JSON. "
                + "Do not call capture_viewport unless the user asks to see the view. "
                + "If they ask for a job that belongs on another chip, tell them to switch Build, Edit, or Sheets.";
            if (mode == ForskMode.Sheets)
            {
                text += " Sheets prefers Layout pages and a PDF. "
                    + "set_project_meta, layout_pack, export_pdf, and clear_layouts are in this tool set, with sheet_pack, make2d_view, and clear_drawings. "
                    + "When the user states a project, client, or address, call set_project_meta. Clear layouts is clear_layouts. "
                    + "For a PDF, call layout_pack if the pages are not already there, then export_pdf with path omitted. "
                    + "The panel opens a save dialog. Do not invent a path and do not ask the user to type one.";
            }
            return text;
        }

        static JObject CallOnUi(string name, JObject args, ForskMode mode)
        {
            var callArgs = args;
            if (name == "export_pdf")
            {
                var path = ForskPrint.PickPathFromBackground();
                if (string.IsNullOrEmpty(path))
                    return ForskTools.Fail("Print cancelled.");
                ForskPrint.SettleAfterDialog();
                callArgs = args == null ? new JObject() : (JObject)args.DeepClone();
                callArgs["path"] = path;
            }
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                envelope = ForskTools.Execute(name, callArgs, mode);
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
                if (IsPlanLayer(LayerName(doc, obj)))
                    chip.HasPlan = true;
            }
            return chip;
        }

        public static List<string> Run(bool rebuild)
        {
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

            if (rebuild && !Step("clear_generated", new JObject(), lines, true))
                return Finish(lines);
            if (!Step("floor_from_layer", new JObject
            {
                ["layer"] = "wall",
                ["thickness"] = settings.FloorThickness
            }, lines, true))
                return Finish(lines);
            if (!Step("walls_from_layer", new JObject
            {
                ["layer"] = "wall",
                ["height"] = settings.WallHeight
            }, lines, true))
                return Finish(lines);
            if (!Step("roof_flat_from_walls", new JObject
            {
                ["thickness"] = settings.RoofThickness
            }, lines, true))
                return Finish(lines);

            Opening("door", lines);
            Opening("window", lines);
            Step("rooms_from_layer", new JObject(), lines, false);
            return Finish(lines);
        }

        static List<string> Finish(List<string> lines)
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }));
            return lines;
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
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
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

        static bool IsPlanLayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var key = name.Trim().ToLowerInvariant();
            if (key.StartsWith("s-")) return false;
            if (key == "x-exist") return false;
            if (key == "wall" || key == "door" || key == "window" || key == "room" || key == "plan" || key == "a-room")
                return true;
            if (key.StartsWith("a-") && key.Contains("plan"))
                return true;
            return false;
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
    /// Sheets print. Same path for the Print PDF button and for chat
    /// print / make PDF / skriv ut. The save dialog picks the path.
    /// </summary>
    public static class ForskPrint
    {
        const string MetaSection = "forsk";

        static readonly string[] MetaKeys =
        {
            "project", "client", "address", "date", "scale_label"
        };

        public static bool IsRequest(string text)
        {
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 160) return false;
            if (t.Contains("clear")) return false;
            if (t.Contains("project is") || t.Contains("client") || t.Contains("address")) return false;
            if (t.Contains("make sheet") || t.Contains("tegning") || t.Contains("drawing")) return false;
            if (t.Contains("make2d") || t.Contains("sheet_pack")) return false;

            if (t == "pdf" || t == "print" || t == "print pdf" || t == "skriv ut") return true;
            if (HasWord(t, "print")) return true;
            if (t.Contains("skriv ut")) return true;
            if (t.Contains("make pdf") || t.Contains("make a pdf") || t.Contains("make the pdf")) return true;
            return false;
        }

        public static string Run()
        {
            var units = UnitsProblem();
            if (units != null)
                return "Print PDF · error · " + units;

            var meta = KnownMeta();
            if (meta != null)
            {
                var stored = Call("set_project_meta", meta);
                if (!Ok(stored)) return FailLine(stored);
            }

            var pack = Call("layout_pack", new JObject());
            if (!Ok(pack)) return FailLine(pack);

            var path = PickPathFromBackground();
            if (string.IsNullOrEmpty(path))
                return "Print PDF · cancelled";

            // The layout capture runs after this returns, with Rhino focused.
            SettleAfterDialog();
            var exported = Call("export_pdf", new JObject { ["path"] = path });
            if (!Ok(exported)) return FailLine(exported);

            var result = exported["result"] as JObject;
            var count = result?["count"]?.ToString();
            var written = result?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(written)) written = path;
            if (string.IsNullOrWhiteSpace(count))
                return "Print PDF · ok · " + written;
            return "Print PDF · ok · " + count + " · " + written;
        }

        public static string PickPathFromBackground()
        {
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try { path = PickPath(); }
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

        public static string PickPath()
        {
            var dialog = new Eto.Forms.SaveFileDialog
            {
                Title = "Print PDF",
                FileName = DefaultFileName(),
                CheckFileExists = false
            };
            dialog.Filters.Add(new FileFilter("PDF", ".pdf"));
            TrySetDesktop(dialog);
            var parent = RhinoEtoApp.MainWindow;
            if (dialog.ShowDialog(parent) != DialogResult.Ok)
                return null;
            return AbsolutePdf(dialog.FileName, dialog.Directory);
        }

        static JObject KnownMeta()
        {
            JObject meta = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null) return;
                var obj = new JObject();
                var any = false;
                foreach (var key in MetaKeys)
                {
                    var value = doc.Strings.GetValue(MetaSection, key);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    obj[key] = value.Trim();
                    any = true;
                }
                if (any) meta = obj;
            }));
            return meta;
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

    public static class ForskTarget
    {
        public static string Read()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return "Click something in the model.";

            RhinoObject first = null;
            var count = 0;
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
            {
                if (obj == null) continue;
                count++;
                if (first == null) first = obj;
            }
            if (count == 0 || first == null) return "Click something in the model.";

            var summary = FormatOne(doc, first);
            if (count == 1) return "Target: " + summary;
            return "Target: " + count + " selected · " + summary;
        }

        static string FormatOne(RhinoDoc doc, RhinoObject obj)
        {
            var name = string.IsNullOrWhiteSpace(obj.Name) ? "" : obj.Name;
            var layer = "";
            var index = obj.Attributes.LayerIndex;
            if (index >= 0 && index < doc.Layers.Count)
                layer = doc.Layers[index].Name ?? "";

            var attrs = Serializer.RhinoObjectAttributes(obj);
            var id = attrs?["forsk:id"]?.ToString();
            var kind = attrs?["forsk:kind"]?.ToString();

            var head = name;
            if (!string.IsNullOrEmpty(id))
                head = string.IsNullOrEmpty(head) ? id : head + " " + id;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(head)) parts.Add(head);
            if (!string.IsNullOrEmpty(layer)) parts.Add(layer);
            if (!string.IsNullOrEmpty(kind)) parts.Add("forsk:" + kind);
            if (parts.Count == 0) return obj.Id.ToString();
            return string.Join(" · ", parts);
        }
    }
}
