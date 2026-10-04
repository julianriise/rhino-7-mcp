using System.Globalization;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// D4 detail sheets on the detail garage: the drawings shelf-packed per
/// scale into the 400 × 254 area, a detail's drawings in one row, the sheet
/// numbers A-50-00n and the plan callouts that point at them.
/// </summary>
public class DetailSheetTests
{
    static List<DetailSheet.Sheet> Sheets(IfcExport.Model model, params (string Wall, string Opening)[] picks)
    {
        var facts = picks.Select((p, i) => Details.Resolve(
            new Details.Record { Id = "DET" + (i + 1).ToString("00"), Wall = p.Wall, Opening = p.Opening }, model, DetailFixtures.Tol)).ToList();
        return DetailSheet.Plan(DetailSheet.Items(facts));
    }

    static double Right(Details.Placed p, int scale) => p.X - Details.BandMm + DetailSheet.BoxWidth(p.Drawing, scale);

    [Fact]
    public void TheBrokenSouthWall_ThenTheDoor_TakeTwoSheetsAt1To20()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2" }, sheets.Select(s => s.Id));
        var ids = sheets.Select(s => s.Id).ToList();
        Assert.Equal(new[] { "A-50-001", "A-50-002" }, ids.Select(id => DetailSheet.Number(id, ids)));

        var wall = sheets[0].Drawings;
        Assert.Equal(new[] { 1, 2 }, wall.Select(p => p.Number));
        Assert.Equal(new[] { Details.Plan, Details.Cut }, wall.Select(p => p.Drawing.View));
        Assert.Equal(new[] { 262.5, 80.0 }, wall.Select(p => DetailSheet.BoxWidth(p.Drawing, 20)));

        var door = sheets[1].Drawings;
        Assert.Equal(new[] { 1, 2, 3 }, door.Select(p => p.Number));
        Assert.Equal(new[] { Details.Plan, Details.Elevation, Details.Cut }, door.Select(p => p.Drawing.View));
        Assert.Equal(new[] { 135.0, 135.0, 80.0 }, door.Select(p => DetailSheet.BoxWidth(p.Drawing, 20)));
        Assert.Equal(new[] { 15.0, 160.0, 305.0 }, door.Select(p => p.X));
        Assert.Equal(370, Right(door[2], 20), 6);
        Assert.Equal(232, door.Max(p => DetailSheet.BoxHeight(p.Drawing, 20)), 6);
        // One row, its boxes' tops on the area's top.
        Assert.All(door, p => Assert.Equal(Details.AreaHeightMm,
            p.Y - Details.TitleBandMm - Details.BandMm + DetailSheet.BoxHeight(p.Drawing, 20), 6));
    }

    [Fact]
    public void TheCallouts_ReadThePlanDetailsNumber_OverItsSheet()
    {
        var callouts = DetailCallout.Callouts(Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door")));
        Assert.Equal(new[] { (1, "A-50-001"), (1, "A-50-002") }, callouts.Select(c => (c.Number, c.Sheet)));
        Assert.Equal(new[] { "DET01", "DET02" }, callouts.Select(c => c.Detail));
        // The wall's main run's middle on its centreline; the door's centre there.
        Assert.Equal((4000.0, 100.0), (callouts[0].Target.X, callouts[0].Target.Y));
        Assert.Equal((2000.0, 100.0), (callouts[1].Target.X, callouts[1].Target.Y));
    }

    [Fact]
    public void EachOpening_TakesASheet_ADoorAndAWindowTwo()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), (null, "o-door"), (null, "o-window"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2" }, sheets.Select(s => s.Id));
        Assert.All(sheets, s => Assert.Equal(3, s.Drawings.Count));
    }

    [Fact]
    public void TwoEndWalls_AndBothOpenings_TakeFourSheetsAt1To20()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), ("w03", null), ("w04", null), (null, "o-door"), (null, "o-window"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2", "detail_20_3", "detail_20_4" }, sheets.Select(s => s.Id));
        Assert.Equal(new[] { 2, 2, 3, 3 }, sheets.Select(s => s.Drawings.Count));
    }

    [Fact]
    public void ThePlacement_IsTheSameEveryRun()
    {
        string Layout() => string.Join(";", Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door"), (null, "o-window"))
            .SelectMany(s => s.Drawings.Select(p => s.Id + " " + p.Number + " " + p.Drawing.View + " " + p.X + " " + p.Y)));
        Assert.Equal(Layout(), Layout());
    }

    [Fact]
    public void Order_IsScaleAscendingThenN_AndNumbersFollowIt()
    {
        var ids = new[] { "detail_25_1", "detail_20_2", "nonsense", "detail_20_1", "DETAIL_20_1" };
        Assert.Equal(new[] { "detail_20_1", "detail_20_2", "detail_25_1" }, DetailSheet.Order(ids));
        Assert.Equal("A-50-003", DetailSheet.Number("detail_25_1", ids));
        Assert.Equal("", DetailSheet.Number("plan", ids));
    }

    [Fact]
    public void TheViewTitle_SitsInTheTitleBand_UnderItsDrawing()
    {
        var door = Sheets(DetailFixtures.Garage(), (null, "o-door"))[0].Drawings[0];
        var title = DetailSheet.TitleOf(door, 20);
        Assert.Equal(DetailSheet.TitleCircleMm / 2, title.Radius, 6);
        Assert.Equal(door.X - Details.BandMm + title.Radius, title.Circle.X, 6);
        Assert.InRange(title.Circle.Y, door.Y - Details.BandMm - Details.TitleBandMm, door.Y - Details.BandMm);
        Assert.Equal(door.X + door.Drawing.Width / 20, title.RuleTo.X, 6);
    }

    /// <summary>
    /// What a sheet's bake draws, as one box in detail-area paper mm: each
    /// drawing's crop, its dimensions and level lines laid as the bake lays
    /// them (Arial widths, no poché), its plan's marks, and its title's
    /// circle, rule and texts. The bake takes the same box from what it adds.
    /// </summary>
    static RoomDetect.Box Content(DetailSheet.Sheet sheet, List<DetailSheet.Sheet> all)
    {
        var s = (double)sheet.Scale;
        var boxes = new List<RoomDetect.Box>();
        RoomDetect.Box Paper(RoomDetect.Box b) => new(b.MinX / s, b.MinY / s, b.MaxX / s, b.MaxY / s);
        RoomDetect.Box Text(Pt leftBase, string text, double height) =>
            new(leftBase.X, leftBase.Y, leftBase.X + DetailFixtures.Arial(text) * height, leftBase.Y + height);
        foreach (var p in sheet.Drawings)
        {
            var d = p.Drawing;
            var shift = new Pt(p.X * s - d.U0, p.Y * s - d.V0);
            boxes.Add(new RoomDetect.Box(p.X, p.Y, p.X + d.Width / s, p.Y + d.Height / s));
            var chains = DetailDims.Chains(d.Facts, d);
            foreach (var chain in chains) chain.Origin = new Pt(chain.Origin.X + shift.X, chain.Origin.Y + shift.Y);
            var taken = new List<PlanDims.Obstacle>();
            var walls = new List<List<List<Pt>>>();
            foreach (var chain in PlanDims.LayoutFixed(chains, sheet.Scale, taken, walls, t => DetailFixtures.Arial(t) * PlanDims.TextMm).Where(c => c.Placed))
            {
                boxes.AddRange(chain.Lines.Concat(chain.Ticks).Select(l => Paper(PlanDims.SegBox(l))));
                boxes.AddRange(chain.Texts.Select(l => Paper(l.Box)));
            }
            foreach (var level in DetailDims.Levels(d.Facts, d))
            {
                var at = new Pt(level.U + shift.X, level.Z + shift.Y);
                boxes.Add(Paper(PlanDims.SegBox(new PlanDims.Seg(at, new Pt(at.X + level.Side * 10 * s, at.Y)))));
            }
            foreach (var m in DetailCallout.PlaceMarks(d, all, shift, sheet.Scale, taken, walls, DetailFixtures.Arial))
                boxes.Add(Paper(new RoomDetect.Box(m.Centre.X - m.HalfX * s, m.Centre.Y - m.HalfY * s, m.Centre.X + m.HalfX * s, m.Centre.Y + m.HalfY * s)));
            var title = DetailSheet.TitleOf(p, sheet.Scale);
            boxes.Add(new RoomDetect.Box(title.Circle.X - title.Radius, title.Circle.Y - title.Radius, title.Circle.X + title.Radius, title.Circle.Y + title.Radius));
            boxes.Add(new RoomDetect.Box(title.RuleFrom.X, title.RuleFrom.Y, title.RuleTo.X, title.RuleTo.Y));
            boxes.Add(Text(title.Name, d.Title, DetailSheet.TitleTextMm));
            boxes.Add(Text(title.Scale, "1:" + sheet.Scale, DetailSheet.TitleScaleMm));
        }
        return new RoomDetect.Box(boxes.Min(b => b.MinX), boxes.Min(b => b.MinY), boxes.Max(b => b.MaxX), boxes.Max(b => b.MaxY));
    }

    [Fact]
    public void EachSmokeGarageSheet_IsCentredInItsUsableArea_TheDoorsThreeViewsAsAGroup_At1To20()
    {
        var sheets = Sheets(DetailFixtures.SmokeGarage(), ("w01", null), (null, "o-d01"));
        Assert.Equal(new[] { 1, 1, 3 }, sheets.Select(sh => sh.Drawings.Count));
        foreach (var sheet in sheets)
        {
            Assert.Equal(20, sheet.Scale);
            var content = Content(sheet, sheets);
            var move = DetailSheet.Centre(content);
            double left = content.MinX + move.X, right = Details.AreaWidthMm - content.MaxX - move.X;
            double bottom = content.MinY + move.Y, top = Details.AreaHeightMm - content.MaxY - move.Y;
            Assert.True(Math.Abs(left - right) < 0.01 && Math.Abs(bottom - top) < 0.01,
                sheet.Id + ": left " + left + " right " + right + ", bottom " + bottom + " top " + top);
            Assert.True(left >= 0 && bottom >= 0, sheet.Id + " fits its area");
        }
        // The packer alone leaves the door's row against the area's left: 0 mm there, 45 on the right.
        var door = sheets[2];
        var packed = Content(door, sheets);
        Assert.True(packed.MinX < Details.AreaWidthMm - packed.MaxX - 40, "the door sheet is off centre before the move: x " + packed.MinX + ".." + packed.MaxX);
        // One move for the whole sheet: its views keep their 10 mm gaps.
        var shift = DetailSheet.Centre(packed);
        var moved = door.Drawings.Select(p => p.X + shift.X).ToList();
        for (var i = 1; i < moved.Count; i++)
            Assert.Equal(DetailSheet.GapMm, moved[i] - moved[i - 1] - DetailSheet.BoxWidth(door.Drawings[i - 1].Drawing, 20), 6);
    }

    [Fact]
    public void TheDetailBake_CentresAllItDrew_AfterTheTitles_OnTheLayerThePageFrames()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        var titles = source.IndexOf("BakeDetailTitle(doc, layer, view, item, origin, scale", StringComparison.Ordinal);
        var centre = source.IndexOf("CentreDetailSheet(doc, layer, origin, scale, box);", StringComparison.Ordinal);
        var frame = source.IndexOf("result.Box = new BoundingBox(", StringComparison.Ordinal);
        Assert.True(titles > 0 && centre > titles && frame > centre, "centred once everything is drawn, the page still framing the whole area");
        var body = source.Substring(source.IndexOf("private static void CentreDetailSheet(", StringComparison.Ordinal));
        body = body.Substring(0, body.IndexOf("private static PrintPen DetailRulePen", StringComparison.Ordinal));
        Assert.Contains("DetailSheet.Centre(", body);
        Assert.Contains("CollectLayerDrawings(doc.Objects, layer, drawn)", body);
        Assert.Contains("Transform.Translation(dx, dy, 0)", body);
        Assert.Contains("SheetFlat.Shift(", body);
        Assert.Contains("SheetFlat.StrokeKey", body);
    }

    [Fact]
    public void AnExportedDetailDxf_MovesLineTextHatchAndPolylineTogether()
    {
        var sheets = Sheets(DetailFixtures.SmokeGarage(), ("w01", null), (null, "o-d01"));
        var sheet = sheets[0];
        var packed = Content(sheet, sheets);
        var move = DetailSheet.Centre(packed);
        var scale = sheet.Scale;
        var drawing = sheet.Drawings[0];
        var title = DetailSheet.TitleOf(drawing, scale);
        var crop = new RoomDetect.Box(drawing.X, drawing.Y, drawing.X + drawing.Drawing.Width / scale, drawing.Y + drawing.Drawing.Height / scale);
        var dim = new PlanDims.Seg(new Pt(crop.MinX, crop.MinY + 20), new Pt(crop.MaxX, crop.MinY + 20));
        var dimText = new Pt((dim.A.X + dim.B.X) / 2.0, dim.A.Y + 2);
        var name = drawing.Drawing.Title;

        // Ribbons: title rule and the dimension line, in model mm. Export writes LINEs from this.
        var stroke = SheetFlat.Encode(new[]
        {
            SheetFlat.Seg.Line(title.RuleFrom.X * scale, title.RuleFrom.Y * scale, title.RuleTo.X * scale, title.RuleTo.Y * scale),
            SheetFlat.Seg.Line(dim.A.X * scale, dim.A.Y * scale, dim.B.X * scale, dim.B.Y * scale),
            SheetFlat.Seg.Line(crop.MinX * scale, crop.MinY * scale, crop.MinX * scale, crop.MaxY * scale)
        });
        var page = new SheetFlat.Affine { A = 1.0 / scale, E = 1.0 / scale };
        var lines = SheetFlat.OnPage(SheetFlat.Shift(stroke, move.X * scale, move.Y * scale), page);
        Assert.Equal(3, lines.Count);

        Pt At(Pt p) => new(p.X + move.X, p.Y + move.Y);
        var hatch = new[] { At(new Pt(crop.MinX, crop.MinY)), At(new Pt(crop.MaxX, crop.MinY)), At(new Pt(crop.MaxX, crop.MaxY)), At(new Pt(crop.MinX, crop.MaxY)) };
        var path = Path.Combine(Path.GetTempPath(), "forsk-detail-centre.dxf");
        File.WriteAllText(path, DetailDxf(lines, At(title.Name), name, At(dimText), hatch));
        var entities = OfficeRoomsTests.Entities(path);
        var rule = entities.Single(e => e.Type == "LINE" && OfficeRoomsTests.On(e, "A-ANNO-TEXT"));
        var dimLine = entities.Single(e => e.Type == "LINE" && OfficeRoomsTests.On(e, "A-ANNO-DIMS"));
        var cut = entities.Single(e => e.Type == "LINE" && OfficeRoomsTests.On(e, "A-WALL-CUT"));
        var label = entities.Single(e => e.Type == "TEXT" && e.Text == name);
        var value = entities.Single(e => e.Type == "TEXT" && e.Text == "200");
        var fill = entities.Single(e => e.Type == "HATCH");
        var loop = entities.Single(e => e.Type == "LWPOLYLINE");

        Assert.True(Gap(Box(rule), Box(label)) <= 5, "title underline is within 5 mm of its title text");
        Assert.True(Gap(Box(dimLine), Box(value)) <= 5, "dimension text sits beside its dimension lines");
        Assert.True(Gap(Box(cut), Box(fill)) <= 5 && Gap(Box(fill), Box(loop)) <= 5,
            "LINE, HATCH and polyline extents move together");
        Assert.Equal(move.X, lines[0].P[0] - title.RuleFrom.X, 6);
        Assert.Equal(move.Y, lines[0].P[1] - title.RuleFrom.Y, 6);
        Assert.Equal(move.X, At(title.Name).X - title.Name.X, 6);
        Assert.Equal(move.Y, hatch[0].Y - crop.MinY, 6);

        var stale = SheetFlat.OnPage(stroke, page);
        Assert.True(Gap(LineBox(stale[0]), Box(label)) > 5, "a stroke left behind its text is the export bug");
    }

    static string DetailDxf(List<SheetFlat.Seg> lines, Pt titleAt, string title, Pt dimAt, Pt[] hatch)
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string Line(string layer, double[] p) =>
            "0\nLINE\n8\n" + layer + "\n10\n" + N(p[0]) + "\n20\n" + N(p[1]) + "\n11\n" + N(p[2]) + "\n21\n" + N(p[3]) + "\n";
        string Text(string layer, Pt at, string text) =>
            "0\nTEXT\n8\n" + layer + "\n10\n" + N(at.X) + "\n20\n" + N(at.Y) + "\n40\n2.5\n1\n" + text + "\n";
        string Ring(string type, string layer, Pt[] pts)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("0\n").Append(type).Append("\n8\n").Append(layer).Append("\n");
            if (type == "LWPOLYLINE")
                sb.Append("90\n").Append(pts.Length).Append("\n70\n1\n");
            foreach (var p in pts)
                sb.Append("10\n").Append(N(p.X)).Append("\n20\n").Append(N(p.Y)).Append("\n");
            return sb.ToString();
        }
        return "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1021\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n"
            + Line("A-ANNO-TEXT", lines[0].P)
            + Line("A-ANNO-DIMS", lines[1].P)
            + Line("A-WALL-CUT", lines[2].P)
            + Text("A-ANNO-TEXT", titleAt, title)
            + Text("A-ANNO-DIMS", dimAt, "200")
            + Ring("HATCH", "A-WALL-PATT", hatch)
            + Ring("LWPOLYLINE", "A-SYMB", hatch)
            + "0\nENDSEC\n0\nEOF\n";
    }

    static RoomDetect.Box Box(OfficeRoomsTests.Entity e)
    {
        var xs = e.Points.Select(p => p.X).ToList();
        var ys = e.Points.Select(p => p.Y).ToList();
        if (e.Type == "LINE")
        {
            xs.Add(e.End.X);
            ys.Add(e.End.Y);
        }
        if (e.Type == "TEXT" && e.Points.Count > 0)
        {
            xs.Add(e.Points[0].X + DetailFixtures.Arial(e.Text) * (e.Height > 0 ? e.Height : 2.5));
            ys.Add(e.Points[0].Y + (e.Height > 0 ? e.Height : 2.5));
        }
        return new RoomDetect.Box(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    static RoomDetect.Box LineBox(SheetFlat.Seg seg) =>
        new(Math.Min(seg.P[0], seg.P[2]), Math.Min(seg.P[1], seg.P[3]), Math.Max(seg.P[0], seg.P[2]), Math.Max(seg.P[1], seg.P[3]));

    static double Gap(RoomDetect.Box a, RoomDetect.Box b)
    {
        var dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        var dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Max(dx, dy);
    }

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }
}
