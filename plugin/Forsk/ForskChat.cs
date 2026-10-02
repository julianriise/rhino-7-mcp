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
        static readonly Dictionary<string, JObject> Catalog = BuildCatalog();

        // Intent only picks a prompt pack. This allow-list is the union.
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
            "dxf_import",
            "floor_from_layer",
            "walls_from_layer",
            "roof_flat_from_walls",
            "openings_from_layer",
            "rooms_from_layer",
            "rooms_detect",
            "mark_as_existing"
        };

        static readonly string[] EditOnly =
        {
            "add_opening",
            "move_opening",
            "set_opening",
            "set_opening_type",
            "delete_opening",
            "move_wall",
            "delete_wall",
            "add_wall"
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
            "clear_layouts",
            "section_add",
            "section_clear",
            "print_profile"
        };

        static readonly string[] DaylightOnly =
        {
            ForskDaylight.ToolName
        };

        static readonly string[] ImportOnly =
        {
            ForskPlanImport.ImportTool,
            ForskPlanImport.ScaleTool
        };

        public static JArray ToolsFor()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tools = new JArray();
            AddTools(Shared, seen, tools);
            AddTools(BuildOnly, seen, tools);
            AddTools(EditOnly, seen, tools);
            AddTools(SheetsOnly, seen, tools);
            AddTools(DaylightOnly, seen, tools);
            AddTools(ImportOnly, seen, tools);
            return tools;
        }

        static void AddTools(string[] names, HashSet<string> seen, JArray tools)
        {
            foreach (var name in names)
            {
                if (!seen.Add(name)) continue;
                if (Catalog.TryGetValue(name, out var tool))
                    tools.Add(tool);
            }
        }

        public static JObject Execute(string name, JObject parameters)
        {
            if (!Allowed(name))
                return Fail("Tool " + name + " is not available in the Forsk panel.");
            return ExecuteAllowed(name, parameters);
        }

        public static JObject ExecuteAllowed(string name, JObject parameters)
        {
            if (!Catalog.ContainsKey(name))
                return Fail("Tool " + name + " is not available in the Forsk panel.");
            if (name == ForskDaylight.ToolName)
                return ForskDaylight.Run(parameters?["target"]?.ToString(), Dispatch);
            return Dispatch(name, parameters);
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

        static bool Allowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return Array.IndexOf(Shared, name) >= 0
                || Array.IndexOf(BuildOnly, name) >= 0
                || Array.IndexOf(EditOnly, name) >= 0
                || Array.IndexOf(SheetsOnly, name) >= 0
                || Array.IndexOf(DaylightOnly, name) >= 0
                || Array.IndexOf(ImportOnly, name) >= 0;
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
            // One record per pill or answer: while the window's record is open, this call writes into it.
            var undo = !entry.ReadOnly && !doc.UndoRecordingIsActive;
            if (undo) record = doc.BeginUndoRecord("Forsk: " + name);
            ForskCalls.Enter();
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
                ForskCalls.Exit();
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

        static JObject Pair(string description)
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 2,
                ["description"] = description
            };
        }

        static JObject Compass(string description)
        {
            return new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("north", "south", "east", "west"),
                ["description"] = description
            };
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

        /// <summary>A Layout page: a drawing view, the schedules sheet, or a stored section (section_a to section_z).</summary>
        static JObject PageEnum(string description)
        {
            var pages = new JArray("plan", "north", "east", "south", "west", "schedules");
            for (var c = 'a'; c <= 'z'; c++)
                pages.Add("section_" + c);
            return new JObject
            {
                ["type"] = "string",
                ["enum"] = pages,
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
                    "Cut doors or windows from wall solids, add A-OPEN markers, and a simple frame with a door leaf or window sill. layer is door or window. Requires walls first.",
                    new JObject
                    {
                        ["layer"] = Str("door or window."),
                        ["sill"] = Num("Bottom Z. Default 0 door, 900 window."),
                        ["head"] = Num("Top Z. Default 2100.")
                    },
                    "layer"),
                Fn(ForskDxf.Tool,
                    "Import a DXF plan with its text intact and at true size: Rhino's import, then every TEXT and MTEXT rewritten from the DXF file, matched by layer and position, and the plan scaled to mm from the DXF's units ($INSUNITS). Rhino's own Import turns B\\U+00F8ttekott into B00F8ttekott. Reports texts matched, rewritten, and unmatched, the units read and the scale applied, and warns when the units were a guess.",
                    new JObject
                    {
                        ["path"] = Str("Absolute path of the .dxf file. Omit it and the user picks the file.")
                    }),
                Fn(ForskPlanImport.ImportTool,
                    "Import a floor plan: a PDF (a vector page's walls, doors, windows, rooms and scale read off it; a scanned page read by the raster source), or a scanned or photographed plan as a PNG or JPEG, read by the raster source (the CubiCasa5k model, licensed CC BY-NC 4.0, non-commercial use only; its scale is assumed 1:100 until the user sets it). The page or image as a locked, faded underlay on X-PLAN, the walls, doors, windows and rooms as 2D review geometry on wall, door, window, A-ROOM and label. Nothing is 3D until the user generates. Reports counts, how the scale stands, the licence, and what needs review. Omit every path and the user picks the file.",
                    new JObject
                    {
                        ["pdf_path"] = Str("Absolute path of a PDF plan. Instead of image_path."),
                        ["page"] = Num("The PDF's page, 1-based. Omit it and the user picks when there are several."),
                        ["image_path"] = Str("Absolute path of a PNG or JPEG of the plan. Alone, the raster source reads it."),
                        ["plan_path"] = Str("Absolute path of the image's forsk.plan_import.v0 JSON, only when the user gives one."),
                        ["scale_hint"] = Str("The drawing's scale when the user states it, such as 1:100. It sizes the image; two points still set the scale."),
                        ["image_dpi"] = Num("Image resolution when the file does not carry it."),
                        ["image_width_mm"] = Num("Width of the image on the plan in mm, when known."),
                        ["replace"] = Bool("Swap an earlier import. Default false.")
                    }),
                Fn(ForskPlanImport.ScaleTool,
                    "Set the imported plan's scale from two points and the real length between them. The underlay and the plan geometry move together. With no points the user picks them in the viewport and types the length. With points and no length it only measures.",
                    new JObject
                    {
                        ["p1"] = Pair("First point [x, y] in mm. Omit to let the user pick."),
                        ["p2"] = Pair("Second point [x, y] in mm."),
                        ["length_mm"] = Num("Real length between the points in mm."),
                        ["frame"] = Str("model (as the plan lies now, default) or source (the detection's own mm).")
                    }),
                Fn("rooms_from_layer",
                    "Room markers from closed curves on A-ROOM (alias room): each the boundary curve with its tag data, no surface. Count 0 if the layer is missing. Does not find rooms from walls; rooms_detect does.",
                    new JObject
                    {
                        ["layer"] = Str("Source layer. Default A-ROOM.")
                    }),
                Fn("rooms_detect",
                    "Make rooms / find rooms: closed regions between the Forsk walls, closed at doors and split by space_divider, drawn on A-ROOM, then room markers. Outlines already on A-ROOM win. Reports rooms, total area, and regions left open with the reason.",
                    new JObject()),
                Fn("mark_as_existing",
                    "Move the selection onto X-EXIST and stamp forsk:kind=existing. Empty selection is refused.",
                    new JObject()),
                Fn("add_opening",
                    "Write a door or window on one Forsk wall and rebuild that host from its path. Does not boolean the old solid. host_id omitted uses the selected wall. Refuses X-EXIST hosts.",
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
                    "Slide one selected opening along its host, then rebuild that wall only. delta_mm is signed millimetres. Id omitted uses the selected marker or frame. Does not clear the model. Refuses X-EXIST.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker or frame GUID. Omit to use the selection."),
                        ["delta_mm"] = Num("Signed distance along the wall, mm. Positive follows the segment."),
                        ["t"] = Num("Absolute 0–1 position. Exclusive with delta_mm.")
                    }),
                Fn("set_opening",
                    "Set width, sill, or head on one selected opening, then rebuild that wall only. Pass only the fields the user named. Id omitted uses the selected marker or frame. Does not clear the model. Refuses X-EXIST.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker or frame GUID. Omit to use the selection."),
                        ["width"] = Num("Opening width in mm."),
                        ["sill"] = Num("Bottom Z in mm."),
                        ["head"] = Num("Top Z in mm.")
                    }),
                Fn("set_opening_type",
                    "Set type, hand, or swing on the selected openings, then rebuild each host once. Pass at least one of type, hand, swing. Id omitted uses the selection, including two or more. Does not clear the model. Refuses X-EXIST. A refused change leaves the document unchanged.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker or frame GUID. Omit to use the selection."),
                        ["type"] = Str("door.hinged_single, door.hinged_double, door.sliding, door.pocket, window.fixed, window.side_hung, or window.top_hung."),
                        ["hand"] = Str("L, R, or flip."),
                        ["swing"] = Str("in, out, or flip.")
                    }),
                Fn("delete_opening",
                    "Remove each selected opening (marker, frame, and record) and rebuild each host wall once from its path. No filler plate. Id omitted uses the selection, including two or more openings. One id removes that opening. Refuses X-EXIST. If the rebuild fails, the openings return and the wall stays.",
                    new JObject
                    {
                        ["id"] = Str("Opening marker GUID. Omit to use the selection.")
                    }),
                Fn("move_wall",
                    "Move one straight wall run across itself, then rebuild that host from its path. The whole plan is one wall record, so name the run: side for an outer wall, or at for the face nearest a point. Both faces move, the walls that meet it stretch, the openings on it move with it. The floor slab and flat roof from that record are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date. A move that would close a room is refused with its depth. Refuses X-EXIST.",
                    new JObject
                    {
                        ["side"] = Compass("The outer wall facing this way. Exclusive with at."),
                        ["at"] = Pair("[x, y] in mm on or beside the wall, within 1000 mm. Exclusive with side."),
                        ["toward"] = Compass("The way it moves. An east–west wall moves north or south."),
                        ["distance_mm"] = Num("How far in mm, above 0."),
                        ["id"] = Str("Wall GUID. Omit to use the selected wall, or the only one.")
                    },
                    "toward", "distance_mm"),
                Fn("delete_wall",
                    "Delete one straight wall run and the openings in it, then rebuild that host from its path. Name the run as for move_wall: side for an outer wall, or at for the face nearest a point. A partition out joins its two rooms; an outer wall out opens the ring; a wall standing on its own goes whole. Refused when the walls left would stand in two pieces. The floor slab and flat roof from that record are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date. Refuses X-EXIST.",
                    new JObject
                    {
                        ["side"] = Compass("The outer wall facing this way. Exclusive with at."),
                        ["at"] = Pair("[x, y] in mm on or beside the wall, within 1000 mm. Exclusive with side."),
                        ["id"] = Str("Wall GUID. Omit to use the selected wall, or the only one.")
                    }),
                Fn("add_wall",
                    "Add one straight wall on its centreline: from and to, or line_id for a straight line the user drew (make this line a wall). Ends short of a wall face by up to 300 mm run on to it. It joins the wall it touches (a partition across a room splits it) or stands on its own as a new wall. Refused when it would join two separate walls or run across an opening. The floor slab and flat roof from that record are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date.",
                    new JObject
                    {
                        ["from"] = Pair("Centreline start [x, y] in mm."),
                        ["to"] = Pair("Centreline end [x, y] in mm."),
                        ["line_id"] = Str("A straight line's GUID: the centreline from its start to its end. Exclusive with from and to."),
                        ["thickness"] = Num("mm, at most 600. Omit for the nearest wall's thickness."),
                        ["height"] = Num("mm, for a wall standing on its own. Omit for the nearest wall's height.")
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
                    "A3 Layout pages of a greyscale drawing. One Detail per view shows black S-DRAW curves, plus a title block bottom-right. Not a PDF. Requires walls. Default pages are plan, north, east, south, west, and schedules. The schedules page holds the door, window and room lists (dørliste, vindusliste, romliste) from the model; doors and windows get marks (D01, V01) on the plan and in their rows. The plan carries dimensions from the model (a chain outside each facade to the opening centres, jogs and overall per side, each rectangular room's width and depth); a room name too wide for its room sits outside on a leader.",
                    new JObject
                    {
                        ["paper"] = Str("A3 only. Default A3."),
                        ["views"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = PageEnum("A page to lay out."),
                            ["description"] = "Omit for plan, four elevations, and schedules."
                        },
                        ["scale"] = Num("Requested scale denominator. 100 means 1:100."),
                        ["replace"] = Bool("Replace Forsk pages for these views. Default true."),
                        ["include_existing"] = Bool("Include X-EXIST in the greyscale drawing. Default true."),
                        ["schedule_kinds"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("door", "window", "room")
                            },
                            ["description"] = "Which lists on the schedules page. Omit for door, window, and room."
                        }
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
                            ["items"] = PageEnum("A page to clear."),
                            ["description"] = "Omit to clear every Forsk layout."
                        },
                        ["dry_run"] = Bool("List matches without deleting. Default false.")
                    }),
                Fn("section_add",
                    "Store a cross section (snitt) A–A: a vertical cut along a line in plan, looking to one side. Give a room (its name or id: the line runs through it, across the building, or along it with axis long), or from and to points, or line_id of a line the user drew or picked. Then layout_pack views plan and section_<letter> draws the marker on the plan and the section sheet (poché, lines beyond, ground line, levels, free height, gesims and møne). The same letter again moves that section.",
                    new JObject
                    {
                        ["letter"] = Str("A to Z. Omit for the next free letter."),
                        ["room"] = Str("A room name or id from the plan tags, such as Stue or rd-01."),
                        ["axis"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("cross", "long"),
                            ["description"] = "With room: cross (tverrsnitt, across the building's long side, default) or long (lengdesnitt)."
                        },
                        ["from"] = Pair("Line start [x, y] in model mm."),
                        ["to"] = Pair("Line end [x, y] in model mm."),
                        ["line_id"] = Str("Id of a line in the document to cut along."),
                        ["look"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("north", "south", "east", "west"),
                            ["description"] = "Which side the section looks toward. Omit for the left of the line."
                        }
                    }),
                Fn("section_clear",
                    "Remove a stored section, its sheet and its plan marker. Omit letter to remove every section.",
                    new JObject
                    {
                        ["letter"] = Str("A to Z. Omit for all.")
                    }),
                Fn("print_profile",
                    "Choose how the sheets are inked: default (solid black poché, black lines), grey (grey poché, grey lines) or hatch (hatched poché, lighter cut line). It is stored in the document and drives the plan, the sections and the schedule rules; the next layout_pack or Print draws with it, and pages already made keep their old look until then. Omit name to read the current profile.",
                    new JObject
                    {
                        ["name"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("default", "grey", "hatch"),
                            ["description"] = "The profile to use. Omit to read the current one."
                        }
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

Import a DXF with dxf_import, not Rhino's own Import: it keeps DXF text escapes, so a label like Bøttekott names its room, and it reads the DXF's units, so the plan lands at true size in mm. Pass the absolute .dxf path the user gave. With none, call it with no path and the user picks the file.

Import a floor plan with plan_import: a PDF as pdf_path (page when the user names one), or a scan or photo of the plan (PNG or JPEG) as image_path alone. With no path the user picks the file. It lands as a faded underlay plus 2D walls, doors, windows and rooms to review. Pass on the receipt: counts, how the scale stands, what needs review, and the licence line when the raster source read the plan. When nothing was imported, pass on the reason and the next step it gives. Set scale is plan_scale; with no points the user picks two and types the length. Nothing goes 3D until the user asks to generate.

Defaults: walls 3000, floor thickness 400, roof 200, doors sill 0 head 2100 width 900, windows sill 900 head 2100 width 1200. Pass stated heights as tool params. If the user states none, use the defaults and say so once.

Never bake from layer X-EXIST. Refuse: X-EXIST is existing underlay, not a bake source.
Never edit an opening on X-EXIST or forsk:kind=existing. Refuse: Existing underlay is not a Forsk host wall.
Roof or openings before walls: Walls first. Call walls_from_layer before roof_flat_from_walls. Or the openings / add_opening line with the same shape.

Delete or remove a door or window, including these windows when two or more are selected, is one delete_opening with no id. Add is add_opening. Both rebuild the host from its path. No filler plate. Move millimetres along the wall is move_opening delta_mm. Set width, sill, or head on the selected opening is set_opening. make this a sliding door, top-hung window, flip swing, and change hand are one set_opening_type. Two or more selected openings are one call and no id. Do not call clear_generated. Do not call delete_object for an opening. Do not use the last opening created. The delete status line is the tool message, such as Removed 2 windows from w01.

Sheets prefers Layout pages and a PDF. Print, make PDF, or skriv ut opens a save dialog. Do not invent a file path. set_project_meta stores project, client, and address. layout_pack bakes black S-DRAW curves and makes the pages. clear_layouts removes those pages and the S-DRAW curves. Sheet cache on S-PLAN and S-ELEV stays: sheet_pack, make2d_view, clear_drawings. Never clear_generated for drawings or layouts.

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
                    + "Two or more selected windows are one delete_opening with no id. "
                    + "Status line for that call: Removed 2 windows from w01. "
                    + "Move a wall is move_wall: side for an outer wall or at [x, y], toward, distance_mm. With neither, ask which wall. "
                    + "Delete a wall is delete_wall, named the same way; its openings go with it. "
                    + "Add a wall is add_wall: from and to [x, y], or line_id for a drawn line. "
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
                    + "Print PDF opens a save dialog. Do not invent a file path. Never clear_generated for drawings.";
            }
            if (intent == ForskIntent.Print)
            {
                return "Turn bias: Print. At most two sentences. No Target block on success. "
                    + "layout_pack, export_pdf, clear_layouts. Print includes the schedules page, the plan's dimensions and every stored section. "
                    + "Print in a profile (grey, hatch) is print_profile first, then Print. "
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
            if (intent == ForskIntent.Dxf) return ForskDxf.Bias;
            if (intent == ForskIntent.Import)
            {
                return "Turn bias: Import. plan_import brings in a PDF (pdf_path) or a scan or photo of the plan (image_path alone); with no path the user picks. plan_scale sets the scale. "
                    + "Reply with the counts, how the scale stands, the licence line when there is one, and what needs review, from the tool message. "
                    + "When nothing was imported, give the reason and the next step from the message, such as the command that fetches the model weights. "
                    + "Set scale with no points given is plan_scale with no arguments: the user picks two points and types the length. "
                    + "Do not generate 3D here. The user reviews and traces missing walls on the wall layer first, then asks to generate.";
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

        public static void RunTurn(string userText, string target, List<JObject> history, Action<string, string> show)
        {
            history.Add(new JObject { ["role"] = "user", ["content"] = userText });
            var key = ForskKeys.Load();
            if (string.IsNullOrEmpty(key))
            {
                history.Add(new JObject { ["role"] = "assistant", ["content"] = ForskKeys.Missing });
                show("assistant", ForskKeys.Missing);
                return;
            }

            var intent = ForskIntentRouter.Classify(userText, target);
            var system = PanelHeader(intent) + "\n\n" + ForskPrompts.Load(intent);
            for (var round = 0; round < MaxRounds; round++)
            {
                JObject message;
                try
                {
                    message = Complete(key, system, history, ForskTools.ToolsFor());
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

                foreach (var token in calls)
                {
                    var call = token as JObject;
                    var id = call?["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) id = "call_" + round;
                    var fn = call?["function"] as JObject;
                    var name = fn?["name"]?.ToString() ?? "";
                    var args = ParseArgs(fn?["arguments"]?.ToString());
                    var envelope = CallOnUi(name, args);
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
            }

            show("assistant", "Stopped after " + MaxRounds + " tool rounds.");
            Trim(history);
        }

        static string PanelHeader(ForskIntent intent)
        {
            var bias = intent == ForskIntent.General
                ? "No tool bias this turn. Follow the message."
                : "This turn is biased toward " + IntentName(intent) + ". The bias is a hint.";
            var text = "You are answering inside the Forsk panel. " + bias + " "
                + "Every panel tool stays available. Call the tool the user asked for. "
                + "Reply in at most two sentences: one past-tense status line, then at most three short facts. "
                + "Do not print a Target block on success. Empty selection uses the refuse copy. "
                + "Ambiguous selection prints one Target line and asks. "
                + "Do not restate the user. Do not say you are happy to help. Do not teach unless they asked how or why. "
                + "Do not narrate tools. The panel prints one dim row per tool, such as walls_from_layer · ok. "
                + "A multi-step bake is one line: Floor, walls, roof, openings, rooms baked. Say defaults once. "
                + "Do not call capture_viewport unless the user asks to see the view.";
            if (intent == ForskIntent.Print || intent == ForskIntent.Sheets)
            {
                text += " When the user states a project, client, or address, call set_project_meta. "
                    + "For a PDF, call layout_pack if the pages are not already there, then export_pdf with path omitted. "
                    + "The panel opens a save dialog. Do not invent a path and do not ask the user to type one. "
                    + "Clear layouts is clear_layouts. Clear drawings is clear_drawings.";
            }
            if (intent == ForskIntent.Build)
            {
                text += " Rebuild, bake again, or clear and regenerate is clear_generated, then the bake order. "
                    + "There is no Rebuild button.";
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
            if (intent == ForskIntent.Dxf) return "Import DXF";
            if (intent == ForskIntent.Import) return "Import";
            return "General";
        }

        static JObject CallOnUi(string name, JObject args)
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
            // No DXF path to open as it stands: the user picks the file.
            if (name == ForskDxf.Tool && ForskDxf.NeedsPick(args?["path"]?.ToString()))
            {
                var path = ForskPlanImport.PickDxfFromBackground();
                if (string.IsNullOrEmpty(path))
                    return ForskTools.Fail("Import DXF cancelled.");
                ForskPrint.SettleAfterDialog();
                callArgs = new JObject { ["path"] = path };
            }
            // No plan to open as it stands: the user picks a PDF, an image or a DXF. A PDF of several pages asks which.
            if (name == ForskPlanImport.ImportTool && ForskPlanImport.NeedsSource(args) != null)
            {
                callArgs = ForskPlanImport.SourceFromBackground(args);
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
            var rooms = Call("rooms_from_layer", new JObject());
            lines.Add(ForskTools.Receipt("rooms_from_layer", rooms));
            // Drawn outlines already became markers when the count is above zero.
            if (BakeChip.ShouldDetectRooms(ResultCount(rooms)))
                Step("rooms_detect", new JObject(), lines, false);
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

            Report(progress, "Layouts ready — choose where to save.");
            var path = PickPathFromBackground(parent);
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
            var message = result?["message"]?.ToString() ?? "";
            var blankAt = message.IndexOf("Blank preview:", StringComparison.Ordinal);
            var blank = blankAt >= 0 ? " · " + ForskTools.Clip(message.Substring(blankAt)) : "";
            if (string.IsNullOrWhiteSpace(count))
                return "Print PDF · ok · " + written + blank;
            return "Print PDF · ok · " + count + " · " + written + blank;
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
            foreach (var obj in global::RhinoMCPPlugin.Functions.RhinoMCPFunctions.ListSelected(doc))
            {
                if (obj?.Attributes == null) continue;
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
            if (obj?.Attributes == null) return obj == null ? "" : obj.Id.ToString();
            var name = "";
            try
            {
                name = string.IsNullOrWhiteSpace(obj.Name) ? "" : obj.Name;
            }
            catch (NullReferenceException)
            {
                name = "";
            }
            var layer = "";
            var index = obj.Attributes.LayerIndex;
            if (index >= 0 && index < doc.Layers.Count)
            {
                var layerObj = doc.Layers[index];
                layer = layerObj.ParentLayerId == Guid.Empty
                    ? (layerObj.Name ?? "")
                    : (layerObj.FullPath ?? layerObj.Name ?? "");
            }

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
            if (string.Equals(kind, "opening", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase))
            {
                var openingKind = attrs?["forsk:opening_kind"]?.ToString();
                var hostId = attrs?["forsk:host_id"]?.ToString();
                if (!string.IsNullOrEmpty(openingKind)) parts.Add(openingKind);
                if (!string.IsNullOrEmpty(hostId)) parts.Add(hostId);
            }
            if (parts.Count == 0) return obj.Id.ToString();
            return string.Join(" · ", parts);
        }
    }
}
