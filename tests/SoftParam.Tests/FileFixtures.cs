using RhinoMCPPlugin.Forsk;

namespace SoftParam.Tests;

/// <summary>Object rows as RhinoMCPFunctions.ChipRows reads them from a document. Fixtures are rows.</summary>
static class Row
{
    static int _next;

    static string Id()
    {
        return "00000000-0000-0000-0000-" + (++_next).ToString("D12");
    }

    /// <param name="path">The wall carries a forsk:path that reads, as every baked wall does. False: one that does not (F2's join graph cannot see it).</param>
    public static ChipRow Wall(bool visible = true, bool selected = false, string stamp = "w01", bool path = true) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "wall", Layer = "A-WALL", Visible = visible, Selected = selected, Solid = true, Closed = true, Stamp = stamp, PathReads = path };

    public static ChipRow Floor() =>
        new ChipRow { Id = Id(), Generated = true, Kind = "floor", Layer = "A-FLOR", Visible = true, Solid = true, Closed = true, Stamp = "floor" };

    public static ChipRow Roof() =>
        new ChipRow { Id = Id(), Generated = true, Kind = "roof", Layer = "A-ROOF", Visible = false, Solid = true, Closed = true, Stamp = "roof" };

    public static ChipRow Door(bool selected = false) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "opening_marker", OpeningKind = "door", Layer = "A-OPEN", Visible = false, Selected = selected, Stamp = "D01" };

    public static ChipRow DoorFrame(bool selected = false) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "opening", OpeningKind = "door", Layer = "A-OPEN", Visible = true, Selected = selected, Solid = true, Stamp = "D01f" };

    public static ChipRow Window(bool selected = false) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "opening_marker", OpeningKind = "window", Layer = "A-OPEN", Visible = false, Selected = selected, Stamp = "V01" };

    public static ChipRow Room(bool selected = false, string name = "Stue") =>
        new ChipRow { Id = Id(), Generated = true, Kind = "room", Layer = "A-ROOM", Visible = true, Selected = selected, Curve = true, Closed = true, Stamp = name, Name = name };

    public static ChipRow Map(bool visible = true, bool stale = false) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "analysis", Layer = "A-ANALYSE", Visible = visible, Stale = stale };

    public static ChipRow PlanCurve(string layer = "wall", bool selected = false) =>
        new ChipRow { Id = Id(), Layer = layer, Curve = true, Closed = true, Visible = true, Selected = selected };

    public static ChipRow Curve(string layer, bool closed = false, bool selected = false) =>
        new ChipRow { Id = Id(), Layer = layer, Curve = true, Closed = closed, Visible = true, Selected = selected };

    public static ChipRow Box(string layer = "Default", bool selected = false) =>
        new ChipRow { Id = Id(), Layer = layer, Solid = true, Closed = true, Visible = true, Selected = selected };

    public static ChipRow Underlay(string scale, string review = null) =>
        new ChipRow { Id = Id(), Layer = "X-PLAN", ImportKind = "underlay", ScaleStatus = scale, Visible = true, Review = review };

    public static ChipRow Existing(bool selected = false) =>
        new ChipRow { Id = Id(), Layer = "X-EXIST", Existing = true, Solid = true, Closed = true, Visible = true, Selected = selected };

    public static ChipRow Drawing(string layer) =>
        new ChipRow { Id = Id(), Generated = true, Kind = "drawing", Layer = layer, Curve = true, Visible = true };
}

/// <summary>Named documents. Every registry rule is checked over all of them.</summary>
static class Docs
{
    public static DocInput Of(params ChipRow[] rows) => new DocInput { Rows = rows.ToList(), KeyPresent = true };

    /// <summary>Walls, a floor, a roof, rooms, a door and a window: a whole Forsk model.</summary>
    public static ChipRow[] House(bool doorSelected = false, bool wallSelected = false, bool roomSelected = false) => new[]
    {
        Row.Wall(selected: wallSelected), Row.Floor(), Row.Roof(),
        Row.Room(selected: roomSelected), Row.Room(name: "Kjøkken"),
        Row.Door(selected: doorSelected), Row.DoorFrame(selected: doorSelected), Row.Window()
    };

    public static DocInput With(this DocInput input, Action<DocInput> change)
    {
        change(input);
        return input;
    }

    public static readonly (string Name, Func<DocInput> Make)[] All =
    {
        ("empty", () => Of()),
        ("empty, no key", () => Of().With(d => d.KeyPresent = false)),
        ("hidden wall", () => Of(Row.Wall(visible: false), Row.Floor())),
        ("partial", () => Of(Row.Floor(), Row.Roof())),
        ("partial, reviewed, door selected", () => Of(Row.Underlay("user", "[\"a door has no swing\"]"), Row.PlanCurve("wall"), Row.Floor(), Row.Door(selected: true))),
        ("aia only", () => Of(Row.Curve("A-WALL", true), Row.Curve("A-DOOR"), Row.Curve("A-GLAZ"), Row.Curve("A-ANNO-TEXT"))),
        ("hand made", () => Of(Row.Box(), Row.Curve("Default", true))),
        ("plan curves", () => Of(Row.PlanCurve("wall"), Row.PlanCurve("door"), Row.PlanCurve("window"))),
        ("plan curve selected", () => Of(Row.PlanCurve("wall", selected: true), Row.PlanCurve("door"))),
        ("unscaled", () => Of(Row.Underlay("assumed"), Row.PlanCurve("wall"), Row.PlanCurve("A-ROOM"))),
        ("unscaled, no curves", () => Of(Row.Underlay("assumed"))),
        ("scaled, reviewed", () => Of(Row.Underlay("user", "[\"2 doors without a swing\",\"the kitchen is open\"]"), Row.PlanCurve("wall"))),
        ("scaled, no curves", () => Of(Row.Underlay("user"))),
        ("existing only", () => Of(Row.Existing(), Row.Existing())),
        ("loose selected", () => Of(Row.PlanCurve("wall"), Row.Curve("Default", closed: true, selected: true))),
        ("house", () => Of(House())),
        ("house, no key", () => Of(House()).With(d => d.KeyPresent = false)),
        ("house, door selected", () => Of(House(doorSelected: true))),
        ("house, door selected, no key", () => Of(House(doorSelected: true)).With(d => d.KeyPresent = false)),
        ("house, wall selected", () => Of(House(wallSelected: true))),
        ("house, wall selected, no key", () => Of(House(wallSelected: true)).With(d => d.KeyPresent = false)),
        ("house, room selected", () => Of(House(roomSelected: true))),
        ("house, room selected, a wall without a path", () => Of(House(roomSelected: true).Append(Row.Wall(stamp: "w02", path: false)).ToArray())),
        ("house, box selected", () => Of(House().Append(Row.Box(selected: true)).ToArray())),
        ("walls only", () => Of(Row.Wall(), Row.Floor())),
        ("rooms, no window", () => Of(Row.Wall(), Row.Floor(), Row.Room(), Row.Door())),
        ("rooms, no window, no key", () => Of(Row.Wall(), Row.Floor(), Row.Room(), Row.Door()).With(d => d.KeyPresent = false)),
        ("map shown", () => Of(House().Append(Row.Map(visible: true)).ToArray())),
        ("map hidden", () => Of(House().Append(Row.Map(visible: false)).ToArray())),
        ("map stale", () => Of(House().Append(Row.Map(visible: false, stale: true)).ToArray())),
        ("printed, then edited", () => Of(House()).With(d => { d.Layouts = 6; d.StoredFingerprint = "an older model"; })),
        ("sheet cache, sections", () => Of(House().Append(Row.Drawing("S-PLAN")).ToArray()).With(d => d.SectionLetters = new List<string> { "A", "B" })),
        ("bridge down, grey ink", () => Of(House()).With(d => { d.ListenerUp = false; d.Ink = "grey"; })),
        ("forsk undo newest", () => Of(House()).With(d => d.UndoNewest = true)),
        ("inches", () => Of(Row.PlanCurve("wall")).With(d => d.Millimetres = false)),
    };

    public static FileFacts Facts(string name) => FileClassifier.Read(All.Single(f => f.Name == name).Make());

    public static IEnumerable<object[]> Names => All.Select(f => new object[] { f.Name });
}
