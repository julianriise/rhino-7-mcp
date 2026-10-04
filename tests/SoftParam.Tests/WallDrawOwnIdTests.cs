using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Draw wall makes one wall element per straight segment, in one undo record,
/// in the 3D model and in a flat plan alike. The store is a fake that does
/// what the document does: records with ids when solid, outlines when flat.
/// </summary>
public class WallDrawOwnIdTests
{
    const double Tol = 1.0;
    const double T = 200;

    static List<Pt> Run4() => new List<Pt> { new Pt(0, 0), new Pt(6000, 0), new Pt(6000, 4000), new Pt(0, 4000) };

    /// <summary>The model the way add_wall keeps it: records with ids, each add through TryAdd.</summary>
    sealed class FakeModel : WallDraw.IWallStore
    {
        public readonly List<List<List<Pt>>> Records = new List<List<List<Pt>>>();
        public readonly List<string> Ids = new List<string>();
        public readonly List<List<Pt>> Outlines = new List<List<Pt>>();
        public int Opened, Closed;
        public bool Open;
        public bool RecordAlreadyOpen;
        public int RefuseAt = -1;
        int _placed;

        public bool Solid { get; set; } = true;

        public bool OpenRecord()
        {
            if (RecordAlreadyOpen) return false;
            Assert.False(Open);
            Open = true;
            Opened++;
            return true;
        }

        public void CloseRecord()
        {
            Assert.True(Open);
            Open = false;
            Closed++;
        }

        public WallDraw.Placed Place(WallDraw.Segment segment, double thickness)
        {
            if (_placed++ == RefuseAt) return new WallDraw.Placed(null, "Not added: D01 is in the way.");
            if (!Solid)
            {
                Outlines.Add(WallDraw.PlanRing(segment, thickness));
                return new WallDraw.Placed(null, null);
            }
            Assert.True(WallEdit.TryAdd(Records, segment.From, segment.To, thickness, Tol, out var added, out var why, own: true), why);
            Records.Add(added.Rings);
            var id = "w" + (Records.Count).ToString("D2");
            Ids.Add(id);
            return new WallDraw.Placed(id, null);
        }
    }

    static List<WallDraw.Segment> Segments(IList<Pt> points, bool closed) => WallDraw.Plan(points, closed, T);

    [Fact]
    public void NSegments_GiveNDistinctWallIds_InOneUndoRecord()
    {
        var model = new FakeModel();
        var outcome = WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);

        Assert.Equal(4, outcome.Drawn);
        Assert.Equal(4, outcome.Ids.Count);
        Assert.Equal(4, outcome.Ids.Distinct().Count());
        Assert.Equal(outcome.Ids, model.Ids);
        Assert.Equal(1, model.Opened);
        Assert.Equal(1, model.Closed);
        Assert.False(model.Open);
    }

    [Fact]
    public void ARecordAlreadyOpen_IsLeftToItsOwner()
    {
        var model = new FakeModel { RecordAlreadyOpen = true };
        WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);
        Assert.Equal(0, model.Closed);

        var shared = new FakeModel();
        WallDraw.Commit(shared, Segments(Run4(), true), T, ownUndo: false);
        Assert.Equal(0, shared.Opened);
        Assert.Equal(0, shared.Closed);
    }

    [Fact]
    public void TheRecordClosesEvenWhenAWallIsRefused_AndEarlierWallsStay()
    {
        var model = new FakeModel { RefuseAt = 2 };
        var outcome = WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);

        Assert.Equal(2, outcome.Drawn);
        Assert.Equal(new[] { "w01", "w02" }, outcome.Ids);
        Assert.Equal("Not added: D01 is in the way.", outcome.Stopped);
        Assert.Equal(1, model.Closed);
        Assert.Equal(2, model.Records.Count);
        Assert.Contains("Stopped: Not added: D01 is in the way.", WallDraw.Receipt(outcome, T, true));
        Assert.DoesNotContain("closed", WallDraw.Receipt(outcome, T, true));
    }

    [Fact]
    public void EachDrawnSegment_IsARecordOfItsOwn_WithOneRunAndJoinedCorners()
    {
        var model = new FakeModel();
        WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);

        Assert.Equal(4, model.Records.Count);
        var cluster = WallJoins.ClusterOf(model.Records, 0, Tol);
        Assert.Equal(4, cluster.Count);
        var graph = WallJoins.Build(model.Records, cluster, Tol);
        Assert.NotNull(graph);
        Assert.Equal(4, graph.Runs.Count);
        var runs = model.Records.Select(r => WallJoins.RunIn(graph, r)).ToList();
        Assert.DoesNotContain(-1, runs);
        Assert.Equal(4, runs.Distinct().Count());
        Assert.Equal(4, graph.Joins.Count);
    }

    [Fact]
    public void AnOpenRun_OfThreeSegments_IsThreeRecords_JoinedInPairs()
    {
        var model = new FakeModel();
        var points = new List<Pt> { new Pt(0, 0), new Pt(6000, 0), new Pt(6000, 3000), new Pt(8000, 5000) };
        var outcome = WallDraw.Commit(model, Segments(points, false), T, ownUndo: true);

        Assert.Equal(3, outcome.Ids.Distinct().Count());
        var cluster = WallJoins.ClusterOf(model.Records, 0, Tol);
        Assert.Equal(3, cluster.Count);
        var graph = WallJoins.Build(model.Records, cluster, Tol);
        Assert.Equal(3, graph.Runs.Count);
        Assert.Equal(2, graph.Joins.Count);
    }

    [Fact]
    public void OwnRecords_CoverTheSameGroundAsTheMergedRecord()
    {
        var segments = Segments(Run4(), true);
        var merged = new List<List<List<Pt>>>();
        foreach (var s in segments)
        {
            Assert.True(WallEdit.TryAdd(merged, s.From, s.To, T, Tol, out var added, out var why), why);
            if (added.Joined >= 0) merged[added.Joined] = added.Rings;
            else merged.Add(added.Rings);
        }
        var model = new FakeModel();
        WallDraw.Commit(model, segments, T, ownUndo: true);

        var all = Enumerable.Range(0, model.Records.Count).ToList();
        var own = WallJoins.Shape(model.Records, all, Tol);
        Assert.NotNull(own);
        Assert.Equal(Footprint(merged[0]), Footprint(own), 3);
    }

    [Fact]
    public void WithoutOwn_TheSecondSegmentStillMergesIntoTheFirst()
    {
        var records = new List<List<List<Pt>>>();
        foreach (var s in Segments(Run4(), false).Take(2))
        {
            Assert.True(WallEdit.TryAdd(records, s.From, s.To, T, Tol, out var added, out var why), why);
            if (added.Joined >= 0) records[added.Joined] = added.Rings;
            else records.Add(added.Rings);
        }
        Assert.Single(records);
    }

    [Fact]
    public void ASegment_OntoAWallThatIsThere_StaysItsOwnRecord()
    {
        var model = new FakeModel();
        model.Records.Add(new List<List<Pt>> { WallDraw.Band(new Pt(0, 0), new Pt(8000, 0), T) });
        var s = new WallDraw.Segment { From = new Pt(4000, 100), To = new Pt(4000, 3000), Start = new Pt(4000, 100), Length = 2900 };
        Assert.True(WallEdit.TryAdd(model.Records, s.From, s.To, T, Tol, out var added, out var why, own: true), why);
        Assert.Equal(-1, added.Joined);
        Assert.Equal(new[] { 0 }, added.Touches);
        Assert.Single(added.Rings);
    }

    // ---- no 3D model: a flat plan ----

    [Fact]
    public void WithNo3DModel_EachSegmentIsAnOutline_AndNoIdIsMade()
    {
        var model = new FakeModel { Solid = false };
        var outcome = WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);

        Assert.True(outcome.Plan);
        Assert.Equal(4, outcome.Drawn);
        Assert.Empty(outcome.Ids);
        Assert.Equal(4, model.Outlines.Count);
        Assert.Empty(model.Records);
        Assert.Equal(1, model.Opened);
        Assert.Equal(1, model.Closed);
    }

    [Fact]
    public void FlatOutlines_AreTheSameBandsTheSolidWallsHave()
    {
        var segments = Segments(Run4(), true);
        var flat = new FakeModel { Solid = false };
        WallDraw.Commit(flat, segments, T, ownUndo: true);
        var solid = new FakeModel();
        WallDraw.Commit(solid, segments, T, ownUndo: true);

        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal(Math.Abs(RoomDetect.Area(flat.Outlines[i])), Footprint(solid.Records[i]), 3);
            Assert.Equal(4, flat.Outlines[i].Count);
        }
    }

    [Fact]
    public void GenerateThreeD_BuildsEachFlatSegmentAsItsOwnWall()
    {
        var flat = new FakeModel { Solid = false };
        WallDraw.Commit(flat, Segments(Run4(), true), T, ownUndo: true);

        // What walls_from_layer does: the touching outlines are one shape, cut into one piece per run.
        var records = flat.Outlines.Select(ring => new List<List<Pt>> { ring }).ToList();
        var clusters = WallJoins.Clusters(records, Tol);
        var cluster = Assert.Single(clusters);
        var graph = WallJoins.Build(records, cluster, Tol);
        Assert.NotNull(graph);
        var pieces = WallSplit.Pieces(graph, Tol, out var why);
        Assert.True(pieces != null, why);
        Assert.Equal(4, pieces.Count);
        Assert.Equal(4, pieces.Select(p => p.Run).Distinct().Count());
    }

    [Fact]
    public void FlatReceipt_SaysThePlanHasThem_AndGenerate3DBuildsThem()
    {
        var flat = new FakeModel { Solid = false };
        var outcome = WallDraw.Commit(flat, Segments(Run4(), true), T, ownUndo: true);
        var receipt = WallDraw.Receipt(outcome, T, true);

        Assert.Equal("Drew 4 walls in the plan, 200 mm thick, 20.0 m in all, closed; Generate 3D builds them.", receipt);
    }

    // ---- the receipt ----

    [Fact]
    public void Receipt_ListsEveryNewWallId_AndForskReceiptReadsThemAll()
    {
        var model = new FakeModel();
        var outcome = WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);
        var receipt = WallDraw.Receipt(outcome, T, true);

        Assert.Equal("Drew 4 walls (w01, w02, w03, w04), 200 mm thick, 20.0 m in all, closed.", receipt);
        Assert.Equal(new[] { "w01", "w02", "w03", "w04" }, ForskReceipt.IdsIn(receipt));
    }

    [Fact]
    public void Receipt_OfOneWall_NamesItsId()
    {
        var model = new FakeModel();
        var points = new List<Pt> { new Pt(0, 0), new Pt(3500, 0) };
        var outcome = WallDraw.Commit(model, Segments(points, false), T, ownUndo: true);
        Assert.Equal("Drew a 200 mm wall w01, 3.5 m long.", WallDraw.Receipt(outcome, T, false));
    }

    [Fact]
    public void Receipt_StaysWithinTwoSentences()
    {
        var model = new FakeModel { RefuseAt = 3 };
        var outcome = WallDraw.Commit(model, Segments(Run4(), true), T, ownUndo: true);
        var receipt = WallDraw.Receipt(outcome, T, true);
        Assert.Equal(2, Regex.Matches(receipt, @"[.!?](\s|$)").Count);
    }

    static double Footprint(List<List<Pt>> rings)
    {
        var outer = rings.OrderByDescending(r => Math.Abs(RoomDetect.Area(r))).First();
        var area = Math.Abs(RoomDetect.Area(outer));
        foreach (var hole in rings.Where(r => r != outer)) area -= Math.Abs(RoomDetect.Area(hole));
        return area;
    }
}
