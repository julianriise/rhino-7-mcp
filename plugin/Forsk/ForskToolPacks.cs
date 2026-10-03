using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The chat's tools: the catalog of typed tools the window offers the
    /// model, and one pack per intent. A named intent sends its pack, fewer
    /// tools than the union; General sends the union. A role override only
    /// reorders the turn's tools, its own first: none is added or removed.
    /// Geometry stays in the [McpCommand] handlers. No RhinoCommon, so the
    /// counts are measured headless (ToolPackTests).
    /// </summary>
    public static class ForskToolPacks
    {
        public const string DaylightTool = "daylight_from_model";
        public const string ImportTool = "plan_import";
        public const string ScaleTool = "plan_scale";
        public const string DebugTool = "debug_report";
        /// <summary>The set's sheets, on/off and order. A window tool: no server tool, no contract.</summary>
        public const string PrintPagesTool = "print_pages";
        /// <summary>The takeoff, read only. A window tool: no server tool, no contract.</summary>
        public const string TakeoffTool = "takeoff";

        /// <summary>Every turn reads the document and the selection.</summary>
        static readonly string[] Shared =
        {
            "get_document_summary",
            "get_selected_objects_info",
            "get_object_info",
            "select_objects",
            "capture_viewport"
        };

        static readonly string[] BuildPack =
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

        static readonly string[] EditPack =
        {
            "add_opening",
            "move_opening",
            "set_opening",
            "set_opening_type",
            "delete_opening",
            "move_wall",
            "delete_wall",
            "add_wall",
            "split_walls",
            "room_push_pull"
        };

        static readonly string[] SheetsPack =
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
            "print_profile",
            PrintPagesTool
        };

        /// <summary>Paper only: the Make2D sheet cache is a sheets turn.</summary>
        static readonly string[] PrintPack =
        {
            "set_project_meta",
            "layout_pack",
            "export_pdf",
            "clear_layouts",
            "section_add",
            "section_clear",
            "print_profile",
            PrintPagesTool
        };

        /// <summary>No rooms: rooms_detect. No windows: add_opening. area_stats is read only.</summary>
        static readonly string[] DaylightPack = { DaylightTool, "rooms_detect", "add_opening", "area_stats" };

        /// <summary>Quantities: the takeoff alone.</summary>
        static readonly string[] TakeoffPack = { TakeoffTool };

        /// <summary>The figures. rooms_detect when the file has no rooms yet.</summary>
        static readonly string[] AreaPack = { "area_stats", "rooms_detect" };

        /// <summary>A picked file can be a DXF, so the plan import carries dxf_import too.</summary>
        static readonly string[] ImportPack = { ImportTool, ScaleTool, ForskDxf.Tool };

        static readonly string[] DxfPack = { ForskDxf.Tool };

        /// <summary>
        /// Read-only model, selection, and layer objects, then the debug report.
        /// No select, no capture, no tool that edits the model. An empty selection
        /// still reads the document and the layer the report is about.
        /// </summary>
        static readonly string[] SupportPack =
        {
            "get_document_summary",
            "get_selected_objects_info",
            "get_object_info",
            "get_objects",
            DebugTool
        };

        /// <summary>The whole list, in the order the panel always sent it.</summary>
        public static readonly IReadOnlyList<string> Union = Distinct(Shared, BuildPack, EditPack, SheetsPack, TakeoffPack, DaylightPack, ImportPack);

        public static readonly IReadOnlyDictionary<string, JObject> Catalog = BuildCatalog();

        /// <summary>The turn's tool names: its intent's pack after the shared reads, or the union for General. Support is only its own pack.</summary>
        public static IReadOnlyList<string> For(ForskIntent intent, ForskRole role = ForskRole.None)
        {
            var names = Names(intent);
            if (role == ForskRole.None) return names;
            var first = new HashSet<string>(RoleTools(role), StringComparer.Ordinal);
            // A stable reorder: the role's tools first, each group in its own order. Nothing added or removed.
            return names.Where(first.Contains).Concat(names.Where(n => !first.Contains(n))).ToList();
        }

        /// <summary>Support may call only its pack. Every other intent keeps the panel's allow list.</summary>
        public static bool Allows(ForskIntent intent, string name)
        {
            if (intent != ForskIntent.Support) return true;
            return SupportPack.Contains(name);
        }

        static IReadOnlyList<string> Names(ForskIntent intent)
        {
            if (intent == ForskIntent.General) return Union;
            if (intent == ForskIntent.Support) return SupportPack;
            return Distinct(Shared, Pack(intent));
        }

        static string[] Pack(ForskIntent intent)
        {
            switch (intent)
            {
                case ForskIntent.Build: return BuildPack;
                case ForskIntent.Edit: return EditPack;
                case ForskIntent.Sheets: return SheetsPack;
                case ForskIntent.Print: return PrintPack;
                case ForskIntent.Daylight: return DaylightPack;
                case ForskIntent.Area: return AreaPack;
                case ForskIntent.Takeoff: return TakeoffPack;
                case ForskIntent.Dxf: return DxfPack;
                case ForskIntent.Import: return ImportPack;
                default: return new string[0];
            }
        }

        /// <summary>
        /// The tools a role knows, used only to reorder a pack. Planner imports,
        /// Modeller builds and edits, Plotter prints, Analyser runs daylight,
        /// area statistics and the rooms detection both need. add_opening stays
        /// in the daylight pack so a model with no windows can still cut one,
        /// and it stays Modeller's tool. Nothing is removed. Support's reads
        /// come first; the reorder adds nothing, so the debug report stays on a
        /// Support turn. Render has no pack, so that override does not reorder.
        /// </summary>
        static IEnumerable<string> RoleTools(ForskRole role)
        {
            switch (role)
            {
                case ForskRole.Planner: return ImportPack.Concat(new[] { "rooms_detect", "rooms_from_layer" });
                case ForskRole.Modeller: return BuildPack.Concat(EditPack);
                case ForskRole.Plotter: return PrintPack.Concat(SheetsPack).Concat(TakeoffPack);
                case ForskRole.Analyser: return new[] { "area_stats", DaylightTool, "rooms_detect" };
                case ForskRole.Support: return SupportPack;
                default: return new string[0];
            }
        }

        /// <summary>The schemas for these names, in order. A name with no schema is a bug ToolPackTests catches.</summary>
        public static JArray Schemas(IEnumerable<string> names)
        {
            var tools = new JArray();
            foreach (var name in names)
                if (Catalog.TryGetValue(name, out var tool)) tools.Add(tool.DeepClone());
            return tools;
        }

        static List<string> Distinct(params string[][] groups)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var group in groups)
                foreach (var name in group)
                    if (seen.Add(name)) list.Add(name);
            return list;
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

        /// <summary>A Layout page: the front sheet, a drawing view, the schedules sheet, or a stored section (section_a to section_z).</summary>
        static JObject PageEnum(string description)
        {
            var pages = new JArray("front", "plan", "north", "east", "south", "west", "schedules", "takeoff");
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
                Fn(DebugTool,
                    "The redacted debug report: plugin, Rhino, file, model counts, recent turns, and logs. Read only. Does not change the model or the selection.",
                    new JObject()),
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
                Fn(ImportTool,
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
                Fn(ScaleTool,
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
                Fn(DaylightTool,
                    "Daylight / dagslys: an estimated daylight factor (CIE overcast) painted as one mesh on A-ANALYSE, from the Forsk walls, openings, overhang and rooms. target floor scores every room; selection scores the selected room marker as one space, and an empty selection is refused. A rerun replaces the map. Not lux and not a code check: pass on the tool's disclaimer line word for word. Hide and show keep the mesh; never delete it.",
                    new JObject
                    {
                        ["target"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("floor", "selection"),
                            ["description"] = "floor (every room, default) or selection (the selected room marker)."
                        }
                    }),
                Fn("rooms_detect",
                    "Make rooms / find rooms: closed regions between the Forsk walls, closed at doors and split by space_divider, drawn on A-ROOM, then room markers. Outlines already on A-ROOM win. Reports rooms, total area, and regions left open with the reason.",
                    new JObject()),
                Fn("area_stats",
                    "Area statistics: net room area per floor, per use and for the whole model, the same figures as the plan tags and the Room schedule (Romliste). BRA and BTA per floor from the outer wall outline and its thickness, or the reason one is left out. Read only. No rooms: rooms_detect.",
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
                    "Move one straight wall run across itself, then rebuild that host from its path. Wall records that touch are read as one, so name the run: side for an outer wall, or at for the face nearest a point; omit both when the selected wall is one run. Both faces move, the walls joined to it stretch along their own lines in every record that touches, the openings on it move with it. The receipt names the walls that followed. The floor slab and flat roof under those walls are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date. A move that would close a room is refused with its depth. Refuses X-EXIST.",
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
                    "Delete one straight wall run and the openings in it, then rebuild that host from its path. Name the run as for move_wall: side for an outer wall, or at for the face nearest a point; omit both when the selected wall is one run. A partition out joins its two rooms; an outer wall out opens the ring; a wall standing on its own goes whole, and so does a record of its own that the run covers. Refused when a wall record left would stand in two pieces. The floor slab and flat roof under those walls are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date. Refuses X-EXIST.",
                    new JObject
                    {
                        ["side"] = Compass("The outer wall facing this way. Exclusive with at."),
                        ["at"] = Pair("[x, y] in mm on or beside the wall, within 1000 mm. Exclusive with side."),
                        ["id"] = Str("Wall GUID. Omit to use the selected wall, or the only one.")
                    }),
                Fn("split_walls",
                    "Split each wall record that holds more than one straight run into one record per run, so a click picks one wall. Files made before this version keep one record for the whole plan. The union stays the same, so rooms, the floor and the joined edits read as before. Each opening goes to the wall that holds it and is cut again. A record whose split is refused (a curved wall, an opening across two of its walls) stays whole and the receipt says why. One undo. Refuses X-EXIST.",
                    new JObject
                    {
                        ["id"] = Str("Wall GUID: split that record only. Omit to split every whole record.")
                    }),
                Fn("room_push_pull",
                    "Push or pull one side of a room: the wall run whose face is that side moves out (the room grows) or in, and the walls joined to it follow, as move_wall moves it. The room is id, else the one selected room. The receipt names the room and the walls that followed. Refused as move_wall refuses. Refuses X-EXIST.",
                    new JObject
                    {
                        ["side"] = Compass("The room's side that moves."),
                        ["distance_mm"] = Num("How far in mm, above 0."),
                        ["way"] = new JObject { ["type"] = "string", ["enum"] = new JArray("out", "in"), ["description"] = "out grows the room, in shrinks it." },
                        ["id"] = Str("Room GUID. Omit to use the one selected room.")
                    },
                    "side", "distance_mm"),
                Fn("add_wall",
                    "Add one straight wall on its centreline: from and to, or line_id for a straight line the user drew (make this line a wall). Ends short of a wall face by up to 300 mm run on to it. It joins the wall it touches (a partition across a room splits it), or stands as a new wall of its own: on its own, or joined to two or more separate walls it touches. Refused when it would run across an opening. The floor slab and flat roof from that record are rebuilt, and rooms are detected again. A shown daylight map is hidden as out of date.",
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
                    "Store project, client, address and revision for the Layout title block. Omitted keys stay. An empty string clears that key. Call when the user states project, client, address, or a revision (Revision B). The sheet prints today's date, and its scale is the detail's own scale. No revision, no Rev. cell.",
                    new JObject
                    {
                        ["project"] = Str("Project name."),
                        ["client"] = Str("Client (byggherre) name."),
                        ["address"] = Str("Site address."),
                        ["revision"] = Str("Revision letter or number, such as B. An empty string removes the Rev. cell.")
                    }),
                Fn("layout_pack",
                    "A3 Layout pages of a greyscale drawing. One Detail per view shows black S-DRAW curves, plus a title block bottom-right. Not a PDF. Requires walls. With no views it lays out the set: the sheets that are on, in the set's order (the front sheet with the Drawing list and Areas, the plan, the four facades, each stored section, the lists). The schedules page holds the Door, Window and Room schedules from the model; doors and windows get marks (D01, V01) on the plan and in their rows. The plan carries dimensions from the model (a chain outside each facade to the opening centres, jogs and overall per side, each rectangular room's width and depth); a room name too wide for its room sits outside on a leader.",
                    new JObject
                    {
                        ["paper"] = Str("A3 only. Default A3."),
                        ["views"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = PageEnum("A page to lay out."),
                            ["description"] = "Omit for the set."
                        },
                        ["scale"] = Num("The set's scale, 100 means 1:100. Kept for the next Print. 0 clears it so the set fits again. Omit to keep the kept scale or, with none, take the first of 1:100, 1:200, 1:500 every sheet fits."),
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
                        ["layout"] = Str("Optional page name or view: plan, north, east, south, west. Omit for every Forsk page, in set order.")
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
                Fn(TakeoffTool,
                    "Takeoff (Quantities; also mengdeliste), read only: exterior and interior walls per thickness (length m, area net of openings one side m², volume m³), walls on X-EXIST apart, each slab and roof (m², m³), doors and windows per type and size (count, m²), and gross area (BTA), usable area (BRA) and net area per floor. Every figure is approx. Answer one line with the figure asked for; the window shows the whole list on a card. Norwegian questions such as hvor mye yttervegg are this tool.",
                    new JObject()),
                Fn(PrintPagesTool,
                    "The set Print writes (the Choose sheets card): which sheets are on and their order. Sheets are front (Drawing list and areas), plan, north, east, south, west (the elevations), section_<letter>, schedules (the Door, Window and Room schedules), takeoff (Quantities, off unless asked for). Pass only what the user changed. drop the facades is off north, east, south, west; put section A before the plan is order section_a, plan (the others keep their places). reset forgets the user's set. Print prints the set. Norwegian names (tegning, fasade, snitt, mengdeliste) mean the same sheets.",
                    new JObject
                    {
                        ["on"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "Sheet ids to switch on." },
                        ["off"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "Sheet ids to switch off." },
                        ["order"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "Sheet ids in the order they should print, from where the first of them stands." },
                        ["reset"] = Bool("Forget the user's set: Forsk infers it again.")
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
}
