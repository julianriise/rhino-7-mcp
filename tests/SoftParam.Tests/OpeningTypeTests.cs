using System.Collections.Generic;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Type, hand, and swing resolve without a document. A refused edit
/// returns no record to write. Keys round-trip, and an untyped opening
/// gains only its defaults.
/// </summary>
public class OpeningTypeTests
{
    [Fact]
    public void EveryId_Resolves_AndNamesItself()
    {
        Assert.Equal(7, OpeningTypes.All.Count);
        foreach (var def in OpeningTypes.All)
        {
            Assert.True(OpeningTypes.TryGet(def.Id, out var found));
            Assert.Equal(def.Id, found.Id);
            Assert.True(OpeningTypes.TryRead(def.Kind, def.Id, null, null, out var record, out var why));
            Assert.Equal("", why);
            Assert.Equal(def.Id, record.TypeId);
            Assert.Equal(def.Kind, record.Kind);
            Assert.Equal(def.Label, record.Def.Label);
            Assert.Null(OpeningTypes.PlanSymbol(record, "plan"));
        }

        Assert.Equal("Sliding door", OpeningTypes.All[2].Label);
        Assert.Equal("Top-hung window", OpeningTypes.All[6].Label);
    }

    [Fact]
    public void KindMismatch_Refuses()
    {
        var window = Read("window", "window.side_hung", "L", "in");
        Assert.False(OpeningTypes.TryApply(window, "door.sliding", null, null, out var edit, out var why));
        Assert.Null(edit);
        Assert.Equal("That is a window. Pick fixed, side_hung, or top_hung.", why);

        var door = Read("door", null, null, null);
        Assert.False(OpeningTypes.TryApply(door, "window.fixed", null, null, out edit, out why));
        Assert.Null(edit);
        Assert.Equal("That is a door. Pick hinged_single, hinged_double, sliding, or pocket.", why);

        Assert.False(OpeningTypes.TryApply(door, "door.portal", null, null, out edit, out why));
        Assert.Equal("Unknown opening type.", why);
    }

    [Fact]
    public void MissingKeys_GiveDefaults_AndUntypedGainsOnlyThose()
    {
        Assert.True(OpeningTypes.TryRead("door", null, null, null, out var door, out var why));
        Assert.Equal("", why);
        Assert.Equal("door.hinged_single", door.TypeId);
        Assert.Equal("L", door.Hand);
        Assert.Equal("in", door.Swing);
        var doorKeys = OpeningTypes.ToKeys(door);
        Assert.Equal(3, doorKeys.Count);

        Assert.True(OpeningTypes.TryRead("window", "  ", "", null, out var window, out why));
        Assert.Equal("window.side_hung", window.TypeId);
        Assert.Equal("L", window.Hand);
        Assert.Equal("in", window.Swing);
        Assert.Equal(3, OpeningTypes.ToKeys(window).Count);

        Assert.True(OpeningTypes.TryRead("window", "window.top_hung", null, null, out var top, out why));
        Assert.Null(top.Hand);
        Assert.Equal("out", top.Swing);
        var topKeys = OpeningTypes.ToKeys(top);
        Assert.Equal(2, topKeys.Count);
        Assert.False(topKeys.ContainsKey(OpeningTypes.HandKey));

        Assert.True(OpeningTypes.TryRead("window", "window.fixed", "L", "in", out var fix, out why));
        var fixedKeys = OpeningTypes.ToKeys(fix);
        var only = Assert.Single(fixedKeys);
        Assert.Equal(OpeningTypes.TypeKey, only.Key);
        Assert.Equal("window.fixed", only.Value);
    }

    [Fact]
    public void RoundTrip_KeysMatch_AndAbsentKeysStayAbsent()
    {
        var source = new Dictionary<string, string>
        {
            [OpeningTypes.TypeKey] = "door.pocket",
            [OpeningTypes.HandKey] = "R"
        };
        Assert.True(OpeningTypes.TryReadKeys(source, "door", out var record, out var why));
        Assert.Equal("", why);
        var back = OpeningTypes.ToKeys(record);
        Assert.Equal(source.Count, back.Count);
        Assert.Equal("door.pocket", back[OpeningTypes.TypeKey]);
        Assert.Equal("R", back[OpeningTypes.HandKey]);
        Assert.False(back.ContainsKey(OpeningTypes.SwingKey));

        Assert.True(OpeningTypes.TryReadKeys(back, "door", out var again, out why));
        var twice = OpeningTypes.ToKeys(again);
        Assert.Equal(back[OpeningTypes.TypeKey], twice[OpeningTypes.TypeKey]);
        Assert.Equal(back[OpeningTypes.HandKey], twice[OpeningTypes.HandKey]);
        Assert.Equal(back.Count, twice.Count);
    }

    [Fact]
    public void Flip_TogglesOneKey_AndKeepsTheOther()
    {
        var door = Read("door", "door.hinged_single", "L", "in");
        Assert.True(OpeningTypes.TryApply(door, null, null, "flip", out var swung, out var why));
        Assert.Equal("", why);
        Assert.Equal("L", swung.After.Hand);
        Assert.Equal("out", swung.After.Swing);
        Assert.False(swung.HandChanged);
        Assert.True(swung.SwingChanged);
        Assert.False(swung.TypeChanged);

        Assert.True(OpeningTypes.TryApply(swung.After, null, null, "flip", out var back, out why));
        Assert.Equal("in", back.After.Swing);
        Assert.Equal("L", back.After.Hand);

        Assert.True(OpeningTypes.TryApply(door, null, "flip", "flip", out var both, out why));
        Assert.Equal("R", both.After.Hand);
        Assert.Equal("out", both.After.Swing);
        Assert.False(both.TypeChanged);

        Assert.True(OpeningTypes.TryApply(door, null, "R", null, out var right, out why));
        Assert.Equal("R", right.After.Hand);
        Assert.Equal("in", right.After.Swing);

        var sliding = Read("door", "door.sliding", "L", null);
        Assert.False(OpeningTypes.TryApply(sliding, null, null, "flip", out var refused, out why));
        Assert.Null(refused);
        Assert.Equal("Sliding doors have no swing.", why);

        var fix = Read("window", "window.fixed", null, null);
        Assert.False(OpeningTypes.TryApply(fix, null, null, "flip", out refused, out why));
        Assert.Equal("Fixed windows have no swing.", why);
        Assert.False(OpeningTypes.TryApply(fix, null, "flip", null, out refused, out why));
        Assert.Equal("Fixed windows have no hand.", why);

        var pocket = Read("door", "door.pocket", "R", null);
        Assert.False(OpeningTypes.TryApply(pocket, null, null, "out", out refused, out why));
        Assert.Equal("Pocket doors have no swing.", why);

        var doubled = Read("door", "door.hinged_double", null, "in");
        Assert.False(OpeningTypes.TryApply(doubled, null, "flip", null, out refused, out why));
        Assert.Equal("Double doors have no hand.", why);
    }

    [Fact]
    public void TypeChange_KeepsSharedKeys_AndDropsTheRest()
    {
        var hinged = Read("door", "door.hinged_single", "R", "out");
        Assert.True(OpeningTypes.TryApply(hinged, "door.sliding", null, null, out var sliding, out var why));
        Assert.Equal("", why);
        Assert.Equal("R", sliding.After.Hand);
        Assert.Null(sliding.After.Swing);
        Assert.False(OpeningTypes.ToKeys(sliding.After).ContainsKey(OpeningTypes.SwingKey));
        Assert.True(sliding.Changed);

        Assert.True(OpeningTypes.TryApply(sliding.After, "door.hinged_single", null, null, out var back, out why));
        Assert.Equal("R", back.After.Hand);
        Assert.Equal("in", back.After.Swing);

        var side = Read("window", "window.side_hung", "L", "in");
        Assert.True(OpeningTypes.TryApply(side, "window.top_hung", null, null, out var top, out why));
        Assert.Null(top.After.Hand);
        Assert.Equal("in", top.After.Swing);

        Assert.True(OpeningTypes.TryApply(side, "window.fixed", null, null, out var fix, out why));
        Assert.Null(fix.After.Hand);
        Assert.Null(fix.After.Swing);
        Assert.Single(OpeningTypes.ToKeys(fix.After));

        Assert.True(OpeningTypes.TryApply(sliding.After, "door.sliding", null, null, out var same, out why));
        Assert.False(same.Changed);
        Assert.Equal("Opening already sliding.", OpeningTypes.AlreadyLine(same.After.Def.ShortName));
    }

    [Fact]
    public void Receipts_NameTheHostAndTheChange()
    {
        Assert.Equal(
            "Changed 1 door to sliding on w01",
            OpeningTypes.ChangedLine(1, "door", "sliding", new[] { "w01" }));
        Assert.Equal(
            "Flipped swing on 1 door on w01",
            OpeningTypes.SwingLine(1, "door", new[] { "w01" }));
        Assert.Equal(
            "Changed hand on 1 door on w01",
            OpeningTypes.HandLine(1, "door", new[] { "w01" }));
        Assert.Equal(
            "Changed 2 doors to sliding on w01, w02",
            OpeningTypes.Receipt(new[]
            {
                Row("door", "sliding", "w01", type: true),
                Row("door", "sliding", "w02", type: true)
            }));
        Assert.Equal(
            "Changed 1 window to top-hung on w01 Changed 1 window to fixed on w01",
            OpeningTypes.Receipt(new[]
            {
                Row("window", "top-hung", "w01", type: true),
                Row("window", "fixed", "w01", type: true)
            }));
        Assert.Equal(
            "Changed hand and flipped swing on 1 door on w01",
            OpeningTypes.Receipt(new[]
            {
                Row("door", "hinged", "w01", hand: true, swing: true)
            }));
    }

    static OpeningTypes.Record Read(string kind, string type, string hand, string swing)
    {
        Assert.True(OpeningTypes.TryRead(kind, type, hand, swing, out var record, out var why));
        Assert.Equal("", why);
        return record;
    }

    static OpeningTypes.ReceiptRow Row(
        string kind,
        string shortName,
        string host,
        bool type = false,
        bool hand = false,
        bool swing = false)
    {
        return new OpeningTypes.ReceiptRow
        {
            Kind = kind,
            ShortName = shortName,
            Host = host,
            TypeChanged = type,
            HandChanged = hand,
            SwingChanged = swing
        };
    }
}
