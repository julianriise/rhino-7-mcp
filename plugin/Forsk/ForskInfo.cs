using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The Properties panel (2.0 UX.5, called the info panel in code): pick
    /// anything Forsk made and the top of the window says what it is, without
    /// asking in chat. A room: type, net area, ceiling height, floor, daylight.
    /// A door or window: type, width, height, sill, head, hand, swing, host
    /// wall, mark. A wall: thickness, height, length. A stair: steps, riser,
    /// going, width, total rise. A piece of furniture: what it is, its size and
    /// its turn. Several things: how many of each kind, the walls' total length
    /// and the rooms' total area. Every property a tool can set is a field, and
    /// a change runs the same tool as the chat would: set_opening,
    /// set_opening_type, rooms_set_type, set_wall, edit_stair, move_furniture.
    /// Built from the picked rows' records (ChipRow.Info). No RhinoCommon.
    /// </summary>
    public static class ForskInfo
    {
        /// <summary>Computed by the row reader beside the forsk:* strings.</summary>
        public const string CeilingKey = "info:ceiling";
        public const string HeightKey = "info:height";
        public const string HostKey = "info:host";
        /// <summary>The room's floor above the walls' foot, mm: a ceiling typed in is the walls' height less this.</summary>
        public const string CeilingLiftKey = "info:ceiling_lift";
        /// <summary>A piece of furniture's turn, degrees counter-clockwise from world x.</summary>
        public const string RotationKey = "info:rotation";
        /// <summary>The thickness and length rows of a wall that is more than one run.</summary>
        public const string SplitNote = "Split walls for picking to change one wall's thickness or length.";

        public sealed class Option
        {
            public string Id;
            public string Label;
        }

        public sealed class Row
        {
            public string Label;
            public string Value;
            /// <summary>The field an edit sends (width, thickness, rotation, room_type…), or null when the row only reads.</summary>
            public string Field;
            /// <summary>"mm" for a number typed in millimetres, "°" for degrees.</summary>
            public string Unit;
            /// <summary>A choice instead of a typed value; Value is then the chosen id.</summary>
            public List<Option> Options;
        }

        public sealed class Panel
        {
            public string Title;
            public string Subtitle;
            /// <summary>The object an edit goes to: the opening's marker, the room's marker, the wall, stair or piece.</summary>
            public string Id;
            /// <summary>A line under the rows, in the meta colour: why a property only reads.</summary>
            public string Note;
            public List<Row> Rows = new List<Row>();
            /// <summary>What an edit needs besides the typed value (an opening's sill, a piece's turn). Not sent to the page.</summary>
            public Dictionary<string, double> Context = new Dictionary<string, double>();
            /// <summary>Buttons under the rows: a registry action id, its label and a Lucide icon. A click fires the action.</summary>
            public List<(string Id, string Label, string Icon)> Actions = new List<(string Id, string Label, string Icon)>();

            public JObject ToJson()
            {
                var rows = new JArray();
                foreach (var row in Rows)
                {
                    var item = new JObject { ["label"] = row.Label, ["value"] = row.Value ?? "" };
                    if (row.Field != null) item["field"] = row.Field;
                    if (row.Unit != null) item["unit"] = row.Unit;
                    if (row.Options != null)
                        item["options"] = new JArray(row.Options.Select(o => new JObject { ["id"] = o.Id, ["label"] = o.Label }));
                    rows.Add(item);
                }
                var json = new JObject { ["title"] = Title, ["rows"] = rows };
                if (!string.IsNullOrEmpty(Subtitle)) json["subtitle"] = Subtitle;
                if (!string.IsNullOrEmpty(Id)) json["id"] = Id;
                if (!string.IsNullOrEmpty(Note)) json["note"] = Note;
                if (Actions.Count > 0)
                    json["actions"] = new JArray(Actions.Select(a => new JObject { ["id"] = a.Id, ["label"] = a.Label, ["icon"] = a.Icon }));
                return json;
            }
        }

        /// <summary>The panel for what is picked, or null: nothing picked, or nothing Forsk made.</summary>
        public static Panel For(FileFacts facts)
        {
            var things = (facts?.Selected ?? new List<ChipRow>()).Where(r => r != null && r.Generated && !r.Existing && KindOf(r) != null).ToList();
            if (things.Count == 0) return null;
            var df = facts.Analysis?.RoomDf ?? new Dictionary<string, double>();
            if (things.Count == 1)
            {
                var row = things[0];
                switch (KindOf(row))
                {
                    case "room": return Room(row, df);
                    case "door":
                    case "window": return Opening(row);
                    case "wall": return Wall(row);
                    case "stair": return Stair(row);
                    case "furniture": return Piece(row);
                }
            }
            return Several(things);
        }

        /// <summary>room, door, window, wall, stair, furniture, or null for anything else.</summary>
        public static string KindOf(ChipRow row)
        {
            var kind = (row?.Kind ?? "").ToLowerInvariant();
            switch (kind)
            {
                case "room":
                case "room_plate": return "room";
                case "opening_marker":
                case "opening":
                    var opening = (row.OpeningKind ?? "").ToLowerInvariant();
                    return opening == "door" || opening == "window" ? opening : null;
                case "wall":
                case "stair":
                case "furniture": return kind;
                default: return null;
            }
        }

        static Panel Room(ChipRow row, Dictionary<string, double> df)
        {
            var id = Get(row, "forsk:room_id") ?? row.ForskId;
            var panel = new Panel
            {
                Title = string.IsNullOrWhiteSpace(row.Name) ? "Room" : row.Name,
                Subtitle = string.IsNullOrEmpty(id) ? "Room" : "Room " + id,
                Id = string.IsNullOrEmpty(row.Marker) ? row.Id : row.Marker
            };
            panel.Rows.Add(new Row
            {
                Label = "Type",
                Value = RoomTypes.Read(row.RoomType),
                Field = "room_type",
                Options = RoomTypes.All.Select(k => new Option { Id = k, Label = RoomTypes.English(k) }).ToList()
            });
            panel.Rows.Add(new Row { Label = "Net area", Value = SquareMetres(Mm(row.Area)) });
            var ceiling = Mm(Get(row, CeilingKey));
            var lift = Mm(Get(row, CeilingLiftKey));
            if (ceiling.HasValue && lift.HasValue)
            {
                // Every room shares the storey's walls: a new ceiling is a new wall height.
                panel.Context["ceiling_lift"] = lift.Value;
                panel.Rows.Add(new Row { Label = "Ceiling height", Value = Number(ceiling), Field = "ceiling", Unit = "mm" });
            }
            else if (ceiling.HasValue) panel.Rows.Add(new Row { Label = "Ceiling height", Value = Millimetres(ceiling) });
            panel.Rows.Add(new Row { Label = "Floor", Value = Floor(Get(row, "forsk:level")) });
            panel.Rows.Add(new Row
            {
                Label = "Daylight",
                Value = !string.IsNullOrEmpty(id) && df.TryGetValue(id, out var mean)
                    ? mean.ToString("0.0", CultureInfo.InvariantCulture) + " % mean"
                    : "Not run yet"
            });
            // One room: render it from inside (Julian, 2026-10-09). The card asks which way to look.
            panel.Actions.Add(("room.inside", ForskText.Get("room.inside"), "camera"));
            return panel;
        }

        static Panel Opening(ChipRow row)
        {
            var kind = KindOf(row);
            var type = Get(row, OpeningTypes.TypeKey);
            OpeningTypes.TypeDef def = null;
            if (!OpeningTypes.TryGet(type, out def)) def = OpeningTypes.All.FirstOrDefault(t => t.Kind == kind);
            var name = kind == "door" ? "Door" : "Window";
            var mark = !string.IsNullOrEmpty(row.Mark) ? row.Mark : row.ForskId;
            var panel = new Panel
            {
                Title = string.IsNullOrEmpty(mark) ? name : name + " " + mark,
                Subtitle = def?.Label,
                Id = row.Id
            };
            panel.Rows.Add(new Row
            {
                Label = "Type",
                Value = def?.Id,
                Field = "type",
                Options = OpeningTypes.All.Where(t => t.Kind == kind).Select(t => new Option { Id = t.Id, Label = t.Label }).ToList()
            });
            var sill = Mm(row.Sill);
            var head = Mm(row.Head);
            panel.Rows.Add(new Row { Label = "Width", Value = Number(Mm(row.Width)), Field = "width", Unit = "mm" });
            if (sill.HasValue && head.HasValue)
            {
                // The height keeps the sill: a new height is a new head.
                panel.Context["sill"] = sill.Value;
                panel.Rows.Add(new Row { Label = "Height", Value = Number(head - sill), Field = "height", Unit = "mm" });
            }
            else panel.Rows.Add(new Row { Label = "Height", Value = "–" });
            panel.Rows.Add(new Row { Label = "Sill", Value = Number(sill), Field = "sill", Unit = "mm" });
            panel.Rows.Add(new Row { Label = "Head", Value = Number(head), Field = "head", Unit = "mm" });
            if (def != null && def.HasHand)
                panel.Rows.Add(new Row
                {
                    Label = "Hand",
                    Value = Hand(Get(row, OpeningTypes.HandKey)),
                    Field = "hand",
                    Options = new List<Option> { new Option { Id = "L", Label = "Left" }, new Option { Id = "R", Label = "Right" } }
                });
            if (def != null && def.HasSwing)
                panel.Rows.Add(new Row
                {
                    Label = "Opens",
                    Value = Swing(Get(row, OpeningTypes.SwingKey)),
                    Field = "swing",
                    Options = new List<Option> { new Option { Id = "in", Label = "In" }, new Option { Id = "out", Label = "Out" } }
                });
            var host = Get(row, HostKey);
            if (!string.IsNullOrEmpty(host)) panel.Rows.Add(new Row { Label = "In wall", Value = host });
            return panel;
        }

        /// <summary>
        /// A wall of one run (ForskPick.OneRunWall) changes its thickness and,
        /// with an end standing free, its length. Its height is the storey's:
        /// every wall follows, and the roof sits on them again. A whole record
        /// of several runs reads its thickness and length only.
        /// </summary>
        static Panel Wall(ChipRow row)
        {
            var oneRun = row.Runs == 1 && !string.IsNullOrEmpty(row.RunName);
            var panel = new Panel
            {
                Title = string.IsNullOrEmpty(row.ForskId) ? "Wall" : "Wall " + row.ForskId,
                Subtitle = string.IsNullOrEmpty(row.RunName) ? null : Capital(row.RunName),
                Id = row.Id
            };
            var thickness = Mm(row.Thickness);
            panel.Rows.Add(oneRun && thickness.HasValue
                ? new Row { Label = "Thickness", Value = Number(thickness), Field = "thickness", Unit = "mm" }
                : new Row { Label = "Thickness", Value = Millimetres(thickness) });
            var height = Mm(Get(row, "forsk:height") ?? Get(row, HeightKey));
            panel.Rows.Add(height.HasValue
                ? new Row { Label = "Height", Value = Number(height), Field = "wall_height", Unit = "mm" }
                : new Row { Label = "Height", Value = "–" });
            if (oneRun && row.RunFreeEnd && row.RunLength.HasValue)
                panel.Rows.Add(new Row { Label = "Length", Value = Number(row.RunLength), Field = "length", Unit = "mm" });
            else
                panel.Rows.Add(new Row { Label = "Length", Value = Metres(oneRun && row.RunLength.HasValue ? row.RunLength : WallLength(row)) });
            if (row.Runs > 1) panel.Note = SplitNote;
            return panel;
        }

        static Panel Stair(ChipRow row)
        {
            var panel = new Panel { Title = string.IsNullOrEmpty(row.ForskId) ? "Stair" : "Stair " + row.ForskId, Id = row.Id };
            panel.Rows.Add(new Row { Label = "Steps", Value = string.IsNullOrEmpty(row.Risers) ? "–" : row.Risers });
            // The riser typed is the most a step may rise; the steps re-plan, all equal, and may come out lower.
            panel.Rows.Add(Size("Riser", Mm(Get(row, Stairs.RiserKey)), "riser_max"));
            panel.Rows.Add(Size("Going", Mm(row.Going), "going"));
            panel.Rows.Add(Size("Width", Mm(Get(row, Stairs.WidthKey)), "stair_width"));
            var rise = Get(row, Stairs.RiseKey);
            if (string.Equals(rise, Stairs.Auto, StringComparison.OrdinalIgnoreCase))
                panel.Rows.Add(new Row { Label = "Total rise", Value = Stairs.Auto, Field = "rise" });
            else panel.Rows.Add(Size("Total rise", Mm(rise), "rise"));
            return panel;
        }

        static Panel Piece(ChipRow row)
        {
            var piece = Furniture.Find(Get(row, Furniture.CatalogKey));
            var panel = new Panel
            {
                Title = piece?.Name ?? "Furniture",
                Subtitle = piece == null ? null : (piece.Fixed ? "Fixed fitting" : "Furniture"),
                Id = row.Id
            };
            if (piece != null)
                panel.Rows.Add(new Row
                {
                    Label = "Size",
                    Value = Number(piece.W) + " × " + Number(piece.D) + " × " + Number(piece.H) + " mm"
                });
            var turn = Mm(Get(row, RotationKey));
            if (turn.HasValue)
            {
                panel.Context["rotation"] = turn.Value;
                panel.Rows.Add(new Row { Label = "Rotation", Value = Degrees(turn.Value), Field = "rotation", Unit = "°" });
            }
            var room = Get(row, Furniture.RoomKey);
            if (!string.IsNullOrEmpty(room)) panel.Rows.Add(new Row { Label = "Room", Value = room });
            panel.Rows.Add(new Row { Label = "Catalogue", Value = Get(row, Furniture.CatalogKey) ?? "–" });
            return panel;
        }

        static readonly string[] Order = { "wall", "door", "window", "room", "stair", "furniture" };

        /// <summary>The panel Id of several rooms picked: an edit goes to the selection.</summary>
        public const string SelectionId = "selection";

        static Panel Several(List<ChipRow> things)
        {
            var kinds = things.GroupBy(KindOf).OrderBy(g => Array.IndexOf(Order, g.Key)).ToList();
            var panel = new Panel { Title = kinds.Count == 1 ? Count(kinds[0].Key, things.Count) : things.Count + " things" };
            if (kinds.Count == 1 && kinds[0].Key == "room")
            {
                // Several rooms: one Type for them all (Julian, 2026-10-09), set in one step.
                panel.Id = SelectionId;
                var types = things.Select(r => RoomTypes.Read(r.RoomType)).Distinct().ToList();
                panel.Rows.Add(new Row
                {
                    Label = "Type",
                    Value = types.Count == 1 ? types[0] : "Mixed",
                    Field = "room_type",
                    Options = RoomTypes.All.Select(k => new Option { Id = k, Label = RoomTypes.English(k) }).ToList()
                });
            }
            if (kinds.Count == 1 && (kinds[0].Key == "door" || kinds[0].Key == "window"))
            {
                // Two doors: one line each, so they can be told apart.
                foreach (var row in things)
                {
                    OpeningTypes.TypeDef def;
                    OpeningTypes.TryGet(Get(row, OpeningTypes.TypeKey), out def);
                    var size = Number(Mm(row.Width)) + " × " + Number(Mm(row.Head) - Mm(row.Sill));
                    panel.Rows.Add(new Row
                    {
                        Label = !string.IsNullOrEmpty(row.Mark) ? row.Mark : row.ForskId ?? KindOf(row),
                        Value = (def == null ? "" : def.Label + " · ") + size + " mm"
                    });
                }
                return panel;
            }
            if (kinds.Count > 1)
                foreach (var kind in kinds)
                    panel.Rows.Add(new Row { Label = Capital(Plural(kind.Key)), Value = kind.Count().ToString(CultureInfo.InvariantCulture) });
            var walls = things.Where(r => KindOf(r) == "wall").ToList();
            if (walls.Count > 0)
                panel.Rows.Add(new Row { Label = "Wall length", Value = Metres(walls.Sum(r => WallLength(r) ?? 0)) });
            var rooms = things.Where(r => KindOf(r) == "room").ToList();
            if (rooms.Count > 0)
                panel.Rows.Add(new Row { Label = "Net area", Value = SquareMetres(rooms.Sum(r => Mm(r.Area) ?? 0)) });
            return panel;
        }

        /// <summary>
        /// The tool and arguments a panel edit runs, the same as asking in chat,
        /// or null with the reason. panel is the Properties panel as it stands
        /// now: an edit for something no longer picked is refused. Sizes are
        /// millimetres, a turn is degrees.
        /// </summary>
        public static (string Tool, JObject Args)? Edit(Panel panel, string id, string field, string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(id) || panel == null || !string.Equals(panel.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                error = "Pick it again, then change it.";
                return null;
            }
            value = (value ?? "").Trim();
            double? number = null;
            switch (field)
            {
                case "type":
                    if (!OpeningTypes.TryGet(value, out _)) { error = "Pick a type from the list."; return null; }
                    return ("set_opening_type", new JObject { ["id"] = id, ["type"] = value });
                case "hand":
                    if (value != "L" && value != "R") { error = "Pick left or right."; return null; }
                    return ("set_opening_type", new JObject { ["id"] = id, ["hand"] = value });
                case "swing":
                    if (value != "in" && value != "out") { error = "Pick in or out."; return null; }
                    return ("set_opening_type", new JObject { ["id"] = id, ["swing"] = value });
                case "room_type":
                    if (!RoomTypes.All.Contains(value)) { error = "Pick a room type from the list."; return null; }
                    // Several rooms picked: the tool sets every selected room.
                    return id == SelectionId
                        ? ("rooms_set_type", new JObject { ["room_type"] = value })
                        : ("rooms_set_type", new JObject { ["id"] = id, ["room_type"] = value });
                case "rise":
                    if (string.Equals(value, Stairs.Auto, StringComparison.OrdinalIgnoreCase))
                        return ("edit_stair", new JObject { ["id"] = id, ["rise"] = Stairs.Auto });
                    break;
                case "rotation":
                    number = Mm(value.Replace("°", "").Trim());
                    if (!number.HasValue) { error = "Type a turn in degrees, like 90."; return null; }
                    panel.Context.TryGetValue("rotation", out var was);
                    var turn = Turn(number.Value - was);
                    if (Math.Abs(turn) < 0.01) { error = "That is how it stands already."; return null; }
                    return ("move_furniture", new JObject { ["id"] = id, ["rotate"] = turn });
            }
            number = number ?? Mm(value.Replace("mm", "").Trim());
            if (!number.HasValue || number.Value < 0)
            {
                error = "Type a size in millimetres, like 900.";
                return null;
            }
            var mm = number.Value;
            switch (field)
            {
                case "width":
                case "sill":
                case "head":
                    if (field == "width" && mm <= 0) { error = "Type a size in millimetres, like 900."; return null; }
                    return ("set_opening", new JObject { ["id"] = id, [field] = mm });
                case "height":
                    if (mm <= 0 || !panel.Context.TryGetValue("sill", out var sill)) { error = "Type a size in millimetres, like 2100."; return null; }
                    return ("set_opening", new JObject { ["id"] = id, ["head"] = sill + mm });
                case "thickness":
                    if (mm <= 0 || mm > WallEdit.MaxThickMm)
                    {
                        error = "A wall is above 0 and at most " + Number(WallEdit.MaxThickMm) + " mm thick.";
                        return null;
                    }
                    return ("set_wall", new JObject { ["id"] = id, ["thickness_mm"] = mm });
                case "length":
                    if (mm <= 0) { error = "Type a length in millimetres, like 3600."; return null; }
                    return ("set_wall", new JObject { ["id"] = id, ["length_mm"] = mm });
                case "wall_height":
                    if (mm <= 0) { error = "Type a height in millimetres, like 2700."; return null; }
                    return ("set_wall", new JObject { ["height_mm"] = mm });
                case "ceiling":
                    if (mm <= 0 || !panel.Context.TryGetValue("ceiling_lift", out var lift)) { error = "Type a height in millimetres, like 2500."; return null; }
                    return ("set_wall", new JObject { ["height_mm"] = mm + lift });
                case "riser_max":
                    return ("edit_stair", new JObject { ["id"] = id, ["riser_max"] = mm });
                case "going":
                    return ("edit_stair", new JObject { ["id"] = id, ["going"] = mm });
                case "stair_width":
                    return ("edit_stair", new JObject { ["id"] = id, ["width"] = mm });
                case "rise":
                    if (mm <= 0) { error = "Type a rise in millimetres, or auto."; return null; }
                    return ("edit_stair", new JObject { ["id"] = id, ["rise"] = mm });
                default:
                    error = "That cannot be changed here.";
                    return null;
            }
        }

        /// <summary>A turn the short way round: above -180 and at most 180 degrees.</summary>
        static double Turn(double degrees)
        {
            var t = degrees % 360.0;
            if (t > 180) t -= 360;
            if (t <= -180) t += 360;
            return Math.Round(t, 3);
        }

        /// <summary>A size in millimetres that changes, or a dash when the record has none.</summary>
        static Row Size(string label, double? mm, string field) =>
            mm.HasValue
                ? new Row { Label = label, Value = Math.Round(mm.Value, 1).ToString("0.#", CultureInfo.InvariantCulture), Field = field, Unit = "mm" }
                : new Row { Label = label, Value = Millimetres(mm) };

        static string Degrees(double degrees) => Math.Round(degrees, 1).ToString("0.#", CultureInfo.InvariantCulture);

        static string Hand(string hand) =>
            string.IsNullOrEmpty(hand) ? "L" : char.ToUpperInvariant(hand[0]) == 'R' ? "R" : "L";

        static string Swing(string swing) =>
            string.Equals(swing, "out", StringComparison.OrdinalIgnoreCase) ? "out" : "in";

        /// <summary>The wall's length along its run: its footprint area over its thickness.</summary>
        public static double? WallLength(ChipRow row)
        {
            var thickness = Mm(row?.Thickness);
            var rings = WallEdit.Rings(Get(row, "forsk:path"));
            if (rings == null || rings.Count == 0 || !thickness.HasValue || thickness.Value <= 0) return null;
            var area = Math.Abs(RoomDetect.Area(rings[0]));
            for (var i = 1; i < rings.Count; i++) area -= Math.Abs(RoomDetect.Area(rings[i]));
            return area / thickness.Value;
        }

        static string Floor(string level)
        {
            if (string.IsNullOrEmpty(level) || level == "0") return "Ground floor";
            return "Floor " + level;
        }

        static string Count(string kind, int n) => n + " " + (n == 1 ? kind : Plural(kind));

        static string Plural(string kind) => kind == "furniture" ? "furniture" : kind + "s";

        static string Get(ChipRow row, string key) =>
            row?.Info != null && row.Info.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        static double? Mm(string text) =>
            double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

        static string Number(double? mm) => mm.HasValue ? Math.Round(mm.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "–";

        /// <summary>Whole millimetres, or one decimal when the record has one (a 178.5 mm riser).</summary>
        static string Millimetres(double? mm) => mm.HasValue ? Math.Round(mm.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) + " mm" : "–";

        static string Metres(double? mm) => mm.HasValue ? (mm.Value / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " m" : "–";

        static string SquareMetres(double? mm2) => mm2.HasValue ? (mm2.Value / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " m²" : "–";

        static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
