using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The debug report's sections and redaction. The Rhino walk is not in this project.</summary>
public class ForskDebugTests
{
    static List<RoomDetect.Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<RoomDetect.Pt> { new RoomDetect.Pt(x0, y0), new RoomDetect.Pt(x1, y0), new RoomDetect.Pt(x1, y1), new RoomDetect.Pt(x0, y1) };

    [Fact]
    public void Report_PrintsTheSections_AndRedactsSecrets()
    {
        var commit = "0123456789abcdef0123456789abcdef01234567";
        var snap = new DebugSnapshot
        {
            Commit = commit,
            Rhino = "7.34.23267.11001",
            File = "house.3dm",
            Units = "millimetres",
            WallRecords = 2,
            WallRuns = 8,
            Openings = 3,
            Floors = 1,
            Roofs = 1,
            Daylight = true,
            UnassignedCells = 2,
            Review = "scale was guessed",
            WindowLog = "page aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"
                + "Bearer sk-abcdefghijklmnopqrstuvwxyz\n"
                + "w01 at (11375, 21735)\n"
                + "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            PluginLog = "ghp_0123456789abcdef0123456789abcdef\n"
                + "\"api_key\": \"secret-value\"\n"
                + "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        };
        snap.Rooms.Add(new DebugRoom { Name = "Kitchen", Area = "24500000", Closed = true, Cells = 12 });
        snap.Rooms.Add(new DebugRoom { Name = "Hall", Closed = false, Cells = 0 });
        var turn = new DebugTurn { Intent = "Edit", Role = "Modeller", Receipt = "Moved the north wall of w01." };
        turn.Calls.Add(new DebugCall { Name = "move_wall", Args = "{\"side\":\"north\"}" });
        snap.Turns.Add(turn);

        var text = ForskDebug.Publish(snap);
        var padded = "\n" + text;
        var at = 0;
        foreach (var header in ForskDebug.Sections)
        {
            var next = padded.IndexOf("\n" + header + "\n", at, System.StringComparison.Ordinal);
            Assert.True(next >= at, header);
            at = next + header.Length;
        }

        Assert.Contains(commit, text);
        Assert.Contains("house.3dm · millimetres", text);
        Assert.Contains("walls: 2 records, 8 runs", text);
        Assert.Contains("openings: 3", text);
        Assert.Contains("Kitchen · 24.5 m² · closed", text);
        Assert.Contains("Hall · open", text);
        Assert.Contains("floors: 1", text);
        Assert.Contains("roofs: 1", text);
        Assert.Contains("Kitchen: 12 cells", text);
        Assert.Contains("Hall: 0 cells", text);
        Assert.Contains("unassigned: 2 cells", text);
        Assert.Contains("1. Edit · Modeller", text);
        Assert.Contains("move_wall {\"side\":\"north\"}", text);
        Assert.Contains("Moved the north wall of w01.", text);
        Assert.Contains("scale was guessed", text);
        Assert.Contains("w01", text);
        Assert.Contains("(11375, 21735)", text);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", text);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text);
        Assert.DoesNotContain("sk-abc", text);
        Assert.DoesNotContain("ghp_", text);
        Assert.DoesNotContain("secret-value", text);
        Assert.DoesNotContain("eyJhbGci", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void LastTurns_TakesTheReplyMark_TheCall_AndTheLastTwenty()
    {
        var items = new List<JObject>
        {
            new JObject { ["role"] = "user", ["text"] = "move the north wall 500 mm" },
            new JObject { ["role"] = "receipt", ["mark"] = "Modeller", ["text"] = "Moved the north wall of w01." }
        };
        var args = "{\"side\":\"north\",\"note\":\"" + new string('x', 400) + "\"}";
        var history = new List<JObject>
        {
            new JObject { ["role"] = "user", ["content"] = "move the north wall 500 mm" },
            new JObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JArray
                {
                    new JObject { ["function"] = new JObject { ["name"] = "move_wall", ["arguments"] = args } }
                }
            }
        };
        var turns = ForskDebug.LastTurns(items, history);
        Assert.Single(turns);
        Assert.Equal("Edit", turns[0].Intent);
        Assert.Equal("Modeller", turns[0].Role);
        Assert.Equal("move_wall", turns[0].Calls[0].Name);
        Assert.True(turns[0].Calls[0].Args.Length <= ForskDebug.ArgLimit + 3);
        Assert.EndsWith("...", turns[0].Calls[0].Args);
        Assert.Equal("Moved the north wall of w01.", turns[0].Receipt);

        var many = new List<JObject>();
        for (var i = 0; i < 21; i++)
            many.Add(new JObject { ["role"] = "user", ["text"] = "print " + i });
        var kept = ForskDebug.LastTurns(many, null);
        Assert.Equal(20, kept.Count);
        Assert.Equal("Print", kept[0].Intent);
        Assert.Equal("print 1", kept[0].User);
        Assert.Equal("print 20", kept[19].User);
    }

    [Fact]
    public void AssignCells_CountsTheRoom_AndLeavesTheRest()
    {
        var cells = new List<RoomDetect.Pt>
        {
            new RoomDetect.Pt(500, 500),
            new RoomDetect.Pt(1500, 500),
            new RoomDetect.Pt(5000, 5000)
        };
        var counts = ForskDebug.AssignCells(cells, new[] { Rect(0, 0, 1000, 1000), Rect(1000, 0, 2000, 1000) }, out var unassigned);
        Assert.Equal(new[] { 1, 1 }, counts);
        Assert.Equal(1, unassigned);
    }

    [Fact]
    public void CommitOf_ReadsTheRevision_OrUnknown()
    {
        Assert.Equal("abc123", ForskDebug.CommitOf("0.3.1-r7+abc123"));
        Assert.Equal("unknown", ForskDebug.CommitOf("0.3.1-r7"));
        Assert.Equal("unknown", ForskDebug.CommitOf(null));
        Assert.Equal("c\nd", ForskDebug.Tail("a\nb\nc\nd", 2));
        var empty = ForskDebug.Format(new DebugSnapshot());
        Assert.Contains("rooms: 0", empty);
        Assert.Contains("daylight: none", empty);
        Assert.Contains("Turns\nnone", empty);
        Assert.Contains("Import review\nnone", empty);
    }
}
