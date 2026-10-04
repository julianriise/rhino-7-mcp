using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Draw stair: start and end to run, riser count, going and width, the snap
/// flush to a wall face, and that the record matches the one add_stair writes.
/// </summary>
public class StairDrawTests
{
    const double Tol = 1.0;
    const double Reach = StairDraw.ReachMm;

    /// <summary>A 4000 x 3000 room, 200 thick walls on the centreline: the room is 100..3900 by 100..2900.</summary>
    static List<List<List<Pt>>> Room()
    {
        var records = new List<List<List<Pt>>>();
        var corners = new List<Pt> { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000), new Pt(0, 3000) };
        foreach (var segment in WallDraw.Plan(corners, true, 200))
        {
            Assert.True(WallEdit.TryAdd(records, segment.From, segment.To, 200, Tol, out var added, out var why, outline: segment.Ring), why);
            if (added.Joined >= 0) records[added.Joined] = added.Rings;
            else records.Add(added.Rings);
        }
        return records;
    }

    static List<StairDraw.Face> Faces() => StairDraw.Faces(Room());

    static StairDraw.Setup Setup(double rise = 3400, double width = 900) => new StairDraw.Setup { Rise = rise, Width = width };

    static StairDraw.FootHit Free(double x, double y) => new StairDraw.FootHit { Foot = new Pt(x, y) };

    // ---- faces and the foot ----

    [Fact]
    public void Faces_LookIntoTheRoom_OnTheHole_AndOutOnTheOuterLoop()
    {
        var faces = Faces();
        Assert.Equal(8, faces.Count);
        var centre = new Pt(2000, 1500);
        bool OnTheRoom(StairDraw.Face f) => Mid(f).X >= 100 && Mid(f).X <= 3900 && Mid(f).Y >= 100 && Mid(f).Y <= 2900;
        var inner = faces.Where(OnTheRoom).ToList();
        Assert.Equal(4, inner.Count);
        Assert.All(inner, f => Assert.True(Dot(f.Free, Sub(centre, Mid(f))) > 0));
        Assert.All(faces.Where(f => !OnTheRoom(f)), f => Assert.True(Dot(f.Free, Sub(centre, Mid(f))) < 0));
    }

    [Fact]
    public void Foot_NearAFace_SitsFlushAgainstIt_HalfTheWidthOut()
    {
        var hit = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 900, Reach, false);
        Assert.NotNull(hit.Face);
        Assert.Equal(new Pt(1500, 550), hit.Foot);
        var wide = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 1200, Reach, false);
        Assert.Equal(new Pt(1500, 700), wide.Foot);
    }

    [Fact]
    public void Foot_NearACorner_LocksToTheFacesEnd()
    {
        var hit = StairDraw.SnapFoot(Faces(), new Pt(160, 140), 900, Reach, false);
        Assert.Equal(new Pt(100, 550), hit.Foot);
        // Nearer the east face than the south one: the stair stands against the east wall, at its south end.
        var far = StairDraw.SnapFoot(Faces(), new Pt(3880, 160), 900, Reach, false);
        Assert.Equal(new Pt(3450, 100), far.Foot);
    }

    [Fact]
    public void Foot_OnTheWallSide_StillComesOutOnTheFreeSide()
    {
        var hit = StairDraw.SnapFoot(Faces(), new Pt(1500, 40), 900, Reach, false);
        Assert.Equal(new Pt(1500, 550), hit.Foot);
    }

    [Fact]
    public void Foot_OutOfReach_IsOnTheGrid_ShiftOnTheMillimetre()
    {
        var free = StairDraw.SnapFoot(Faces(), new Pt(2003.4, 1498.7), 900, Reach, false);
        Assert.Null(free.Face);
        Assert.Equal(new Pt(2000, 1500), free.Foot);
        Assert.Equal(new Pt(2003, 1499), StairDraw.SnapFoot(Faces(), new Pt(2003.4, 1498.7), 900, Reach, true).Foot);
    }

    // ---- the way up ----

    [Fact]
    public void Direction_AlongTheFace_WithinTenDegrees_EitherWay()
    {
        var face = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 900, Reach, false).Face;
        var foot = new Pt(1500, 550);
        var east = StairDraw.Direction(foot, new Pt(3000, 620), face, false, out var along);
        Assert.True(along);
        Assert.Equal(new Pt(1, 0), new Pt(Math.Round(east.X, 6), Math.Round(east.Y, 6)));
        var west = StairDraw.Direction(foot, new Pt(500, 480), face, false, out along);
        Assert.True(along);
        Assert.Equal(-1, Math.Round(west.X, 6));
    }

    [Fact]
    public void Direction_AwayFromTheFace_TakesThe15DegreeStep_ShiftIsFree()
    {
        var face = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 900, Reach, false).Face;
        var foot = new Pt(1500, 550);
        var raw = new Pt(foot.X + 1000 * Math.Cos(40 * Math.PI / 180), foot.Y + 1000 * Math.Sin(40 * Math.PI / 180));
        var snapped = StairDraw.Direction(foot, raw, face, false, out var along);
        Assert.False(along);
        Assert.Equal(45, Math.Atan2(snapped.Y, snapped.X) * 180 / Math.PI, 6);
        var free = StairDraw.Direction(foot, raw, face, true, out along);
        Assert.Equal(40, Math.Atan2(free.Y, free.X) * 180 / Math.PI, 6);
    }

    // ---- the run, the count, the going ----

    [Fact]
    public void Plan_TheRiserCountFollowsTheHeight_TheDrawnRunSetsTheGoing()
    {
        // 3400 / 180 = 18.9 -> 19 risers, 18 treads.
        var draft = StairDraw.Plan(Setup(), Free(1000, 1000), new Pt(6400, 1000), false);
        Assert.True(draft.Valid, draft.Why);
        Assert.Equal(19, draft.Flight.Risers);
        Assert.Equal(3400.0 / 19, draft.Flight.Riser, 6);
        Assert.Equal(300, draft.Flight.Going, 6);
        Assert.Equal(5400, draft.Flight.Run, 6);
        Assert.Equal(900, draft.Flight.Width, 6);
        Assert.Equal("UP 19 × 179/300", draft.Label);
        Assert.Equal("5400 mm · steep: 2R+G = 658", draft.Dimension);
    }

    [Theory]
    [InlineData(4680, 260)]
    [InlineData(4690, 260)]
    [InlineData(4540, 260)]
    [InlineData(4500, 250)]
    [InlineData(4000, 222)]
    public void Plan_TheGoingSnapsToTheDefault_WithinEightMillimetres_AndRoundsToTheMillimetre(double run, double going)
    {
        var draft = StairDraw.Plan(Setup(), Free(0, 0), new Pt(run, 0), true);
        Assert.Equal(going, draft.Flight.Going, 6);
    }

    [Fact]
    public void Plan_ALongOrShortRun_IsHeldAtTheGoingLimits_AndSaysSo()
    {
        var longRun = StairDraw.Plan(Setup(), Free(0, 0), new Pt(20000, 0), true);
        Assert.True(longRun.Held);
        Assert.Equal(600, longRun.Flight.Going, 6);
        Assert.Equal(10800, longRun.Flight.Run, 6);
        var shortRun = StairDraw.Plan(Setup(), Free(0, 0), new Pt(500, 0), true);
        Assert.True(shortRun.Held);
        Assert.Equal(150, shortRun.Flight.Going, 6);
    }

    [Fact]
    public void Plan_TheEndOnTheFoot_IsNotAStair()
    {
        var draft = StairDraw.Plan(Setup(), Free(0, 0), new Pt(5, 0), true);
        Assert.False(draft.Valid);
        Assert.Equal("Move the end farther from the start.", draft.Why);
    }

    [Fact]
    public void Plan_ATypedRun_IsExact_AndNeedsNoMouse()
    {
        var draft = StairDraw.Plan(Setup(), Free(0, 0), new Pt(1, 0), true, 3600);
        Assert.True(draft.Valid, draft.Why);
        Assert.Equal(200, draft.Flight.Going, 6);
        Assert.Equal(3600, draft.Flight.Run, 6);
        Assert.Equal("3600 mm · shallow: 2R+G = 558", draft.Dimension);
    }

    [Fact]
    public void Plan_TheWidth_IsTheSetupWidth()
    {
        Assert.Equal(1100, StairDraw.Plan(Setup(width: 1100), Free(0, 0), new Pt(4680, 0), true).Flight.Width, 6);
    }

    [Fact]
    public void Plan_NoStairIsPossible_ReadsTheToolsWords()
    {
        var narrow = StairDraw.Plan(Setup(width: 400), Free(0, 0), new Pt(4680, 0), true);
        Assert.False(narrow.Valid);
        Assert.Null(narrow.Flight);
        Assert.Equal("Stair width must be between 500 and 5000 mm.", narrow.Why);
        var low = StairDraw.Plan(Setup(rise: 150), Free(0, 0), new Pt(4680, 0), true);
        Assert.False(low.Valid);
        Assert.StartsWith("The rise is too small", low.Why);
    }

    [Fact]
    public void Against_IsTheSideTheWallIsOn_LookingUp()
    {
        var hit = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 900, Reach, false);
        var east = StairDraw.Plan(Setup(), hit, new Pt(3500, 560), false);
        Assert.Equal("right", east.Against);
        var west = StairDraw.Plan(Setup(), hit, new Pt(500, 560), false);
        Assert.Equal("left", west.Against);
        var off = StairDraw.Plan(Setup(), hit, new Pt(1500 + 2000, 550 + 2000), false);
        Assert.Null(off.Against);
    }

    // ---- the record matches add_stair ----

    /// <summary>What add_stair does with from, to, width, going and against (PlaceStair, then Stairs.Write).</summary>
    static Dictionary<string, string> AsTheToolWritesIt(JObject call, double autoRise)
    {
        var from = call["from"].Select(t => t.Value<double>()).ToArray();
        var to = call["to"].Select(t => t.Value<double>()).ToArray();
        var length = Math.Sqrt((to[0] - from[0]) * (to[0] - from[0]) + (to[1] - from[1]) * (to[1] - from[1]));
        var spec = new Stairs.Spec
        {
            Width = call["width"]?.Value<double>() ?? Stairs.WidthDefault,
            RiserMax = Stairs.RiserMaxDefault,
            Going = call["going"]?.Value<double>() ?? Stairs.GoingDefault,
            Rise = null,
            Z = 0,
            X = from[0],
            Y = from[1],
            Dx = (to[0] - from[0]) / length,
            Dy = (to[1] - from[1]) / length,
            Against = call["against"]?.ToString()
        };
        return Stairs.Write(spec, Stairs.Plan(spec, autoRise));
    }

    [Fact]
    public void TheDrawnStair_WritesTheRecordAddStairWrites()
    {
        var hit = StairDraw.SnapFoot(Faces(), new Pt(1500, 140), 900, Reach, false);
        var draft = StairDraw.Plan(Setup(), hit, new Pt(6200, 560), false);
        Assert.True(draft.Valid, draft.Why);
        var call = StairDraw.ToolParams(draft);
        Assert.Equal(new[] { "against", "from", "going", "to", "width" }, call.Properties().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal("right", call["against"].ToString());
        Assert.Null(call["rise"]);
        var tool = AsTheToolWritesIt(call, 3400);
        var drawn = Stairs.Write(draft.Spec, draft.Flight);
        Assert.Equal(tool, drawn);
        Assert.Equal("auto", drawn[Stairs.RiseKey]);
        Assert.Equal("19", drawn[Stairs.RisersKey]);
        Assert.Equal("1500,550,0", drawn[Stairs.StartKey]);
    }

    [Fact]
    public void ADefaultDrawnStair_IsTheDefaultStair()
    {
        var draft = StairDraw.Plan(Setup(), Free(1000, 2000), new Pt(5680, 2000), true);
        var call = StairDraw.ToolParams(draft);
        Assert.Equal(new JArray(1000, 2000), call["from"]);
        Assert.Equal(new JArray(5680, 2000), call["to"]);
        Assert.Equal(900, call["width"].Value<double>());
        Assert.Equal(260, call["going"].Value<double>());
        Assert.Null(call["against"]);
        var chat = Stairs.Write(new Stairs.Spec { X = 1000, Y = 2000, Dx = 1, Dy = 0 }, Stairs.Plan(new Stairs.Spec(), 3400));
        Assert.Equal(chat, Stairs.Write(draft.Spec, draft.Flight));
    }

    [Fact]
    public void AnObliqueStair_ComesBackFromTheToolsParameters_WithinARoundingError()
    {
        var draft = StairDraw.Plan(Setup(), Free(500, 500), new Pt(500 + 4000 * Math.Cos(30 * Math.PI / 180), 500 + 4000 * Math.Sin(30 * Math.PI / 180)), false);
        var tool = AsTheToolWritesIt(StairDraw.ToolParams(draft), 3400);
        var drawn = Stairs.Write(draft.Spec, draft.Flight);
        var a = tool[Stairs.DirectionKey].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var b = drawn[Stairs.DirectionKey].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(b[0], a[0], 4);
        Assert.Equal(b[1], a[1], 4);
        Assert.Equal(tool[Stairs.RisersKey], drawn[Stairs.RisersKey]);
        Assert.Equal(tool[Stairs.GoingKey], drawn[Stairs.GoingKey]);
    }

    [Fact]
    public void TheReceipt_IsTheToolsLine()
    {
        var draft = StairDraw.Plan(Setup(), Free(0, 0), new Pt(4680, 0), true);
        Assert.Equal("Added a straight stair, 19 steps of 179.", Stairs.Receipt("Added", draft.Flight));
    }

    // ---- the live symbol ----

    [Fact]
    public void Symbol_IsThePlanSymbolTheSheetDraws_InPlanCoordinates()
    {
        var draft = StairDraw.Plan(Setup(), Free(0, 0), new Pt(0, 4680), true);
        Assert.Equal(new Pt(0, 1), new Pt(Math.Round(draft.Dir.X, 6), Math.Round(draft.Dir.Y, 6)));
        var pieces = StairDraw.Symbol(draft);
        var text = Assert.Single(pieces, p => p.Shape == "text");
        Assert.Equal("UP 19 × 179/260", text.Text);
        Assert.Contains(pieces, p => p.Shape == "dot");
        // The first riser runs across the width at the foot: left of a northward climb is west.
        Assert.Contains(pieces, p => p.Shape == "line" && Near(p.A, -450, 0) && Near(p.B, 450, 0) || p.Shape == "line" && Near(p.B, -450, 0) && Near(p.A, 450, 0));
        // The text sits inside the footprint.
        var xs = draft.Footprint.Select(p => p.X);
        var ys = draft.Footprint.Select(p => p.Y);
        Assert.InRange(text.A.X, xs.Min(), xs.Max());
        Assert.InRange(text.A.Y, ys.Min(), ys.Max());
        // As many marks as the print draws for the same flight.
        Assert.Equal(Stairs.PlanSymbol(draft.Flight, StairDraw.PlanCutMm, StairDraw.PreviewScale, Stairs.LabelHeight(draft.Flight, StairDraw.PreviewScale)).Count, pieces.Count);
    }

    [Fact]
    public void Symbol_OfNoStair_IsEmpty()
    {
        Assert.Empty(StairDraw.Symbol(StairDraw.Plan(Setup(width: 400), Free(0, 0), new Pt(4680, 0), true)));
        Assert.Empty(StairDraw.Symbol(null));
    }

    static bool Near(Pt p, double x, double y) => Math.Abs(p.X - x) < 1e-6 && Math.Abs(p.Y - y) < 1e-6;

    static Pt Mid(StairDraw.Face f) => new Pt((f.A.X + f.B.X) / 2, (f.A.Y + f.B.Y) / 2);

    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
}
