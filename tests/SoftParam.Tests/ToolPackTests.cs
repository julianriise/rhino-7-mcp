using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D4: the tool list narrowed by the router's intent, measured offline on the
/// panel prompts recorded for the intent spike. No model call. A named
/// intent sends fewer tools than the union; General sends the union; a role
/// override reorders and never removes. Set FORSK_PACK_REPORT to a path to
/// write the report forsk keeps at docs/metrics/v3_pack_narrowing.json.
/// </summary>
public class ToolPackTests
{
    static readonly Dictionary<string, ForskIntent> RouterNames = new()
    {
        ["daylight"] = ForskIntent.Daylight,
        ["build"] = ForskIntent.Build,
        ["print"] = ForskIntent.Print,
        ["sheets"] = ForskIntent.Sheets,
        ["import_dxf"] = ForskIntent.Dxf,
        ["import_plan"] = ForskIntent.Import,
        ["edit"] = ForskIntent.Edit,
        ["support"] = ForskIntent.Support,
        ["general"] = ForskIntent.General,
        ["area"] = ForskIntent.Area,
        ["takeoff"] = ForskIntent.Takeoff
    };

    static IEnumerable<JObject> Recorded()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "recorded_prompts.json");
        return ((JArray)JObject.Parse(File.ReadAllText(path))["cases"]!).Cast<JObject>();
    }

    static ForskIntent Route(JObject c)
    {
        var selection = c["selection"]!.ToString();
        // The recorded selections are the old target texts; an opening's says forsk:opening.
        var picked = selection == "empty" ? Picked.None : selection.Contains("forsk:opening") ? Picked.Opening : Picked.Other;
        return ForskIntentRouter.Classify(c["utterance"]!.ToString(), picked);
    }

    static int Chars(IEnumerable<string> names) => ForskToolPacks.Schemas(names).ToString(Formatting.None).Length;

    [Fact]
    public void TheRecordedPrompts_StillRouteAsRecorded()
    {
        foreach (var c in Recorded())
            Assert.True(RouterNames[c["router"]!.ToString()] == Route(c), c["id"] + " " + c["utterance"]);
    }

    [Fact]
    public void EveryNamedIntent_SendsFewerToolsThanTheUnion_GeneralSendsTheUnion()
    {
        var union = ForskToolPacks.Union;
        foreach (var c in Recorded())
        {
            var intent = Route(c);
            var sent = ForskToolPacks.For(intent);
            if (intent == ForskIntent.General)
                Assert.Equal(union, sent);
            else
                Assert.True(sent.Count < union.Count, c["id"] + ": " + intent + " sends " + sent.Count + " of " + union.Count);
            // Support's list is read-only and adds debug_report, which the modelling union does not carry.
            if (intent == ForskIntent.Support)
            {
                Assert.Equal(new[] { "get_document_summary", "get_selected_objects_info", "get_object_info", "get_objects", "debug_report" }, sent);
                Assert.DoesNotContain("select_objects", sent);
                Assert.DoesNotContain("capture_viewport", sent);
            }
            else
                Assert.Subset(union.ToHashSet(), sent.ToHashSet());
        }
    }

    [Fact]
    public void EveryToolInEveryPack_HasASchema()
    {
        var missing = ForskToolPacks.Union.Where(name => !ForskToolPacks.Catalog.ContainsKey(name)).ToList();
        Assert.Empty(missing);
        Assert.Equal(ForskToolPacks.Union.Count, ForskToolPacks.Schemas(ForskToolPacks.Union).Count);
    }

    [Fact]
    public void ADaylightTurn_CarriesTheDaylightTool()
    {
        var daylight = ForskToolPacks.For(ForskIntent.Daylight);
        Assert.Contains(ForskToolPacks.DaylightTool, daylight);
        Assert.Contains(ForskToolPacks.Schemas(daylight), t => t["function"]!["name"]!.ToString() == ForskToolPacks.DaylightTool);
    }

    [Theory]
    [InlineData(ForskIntent.General)]
    [InlineData(ForskIntent.Edit)]
    [InlineData(ForskIntent.Print)]
    [InlineData(ForskIntent.Import)]
    [InlineData(ForskIntent.Daylight)]
    [InlineData(ForskIntent.Support)]
    public void AnOverride_ReordersTheTools_NeverRemovesOne(ForskIntent intent)
    {
        var plain = ForskToolPacks.For(intent);
        foreach (var role in Enum.GetValues(typeof(ForskRole)).Cast<ForskRole>())
        {
            var ordered = ForskToolPacks.For(intent, role);
            Assert.Equal(plain.OrderBy(n => n), ordered.OrderBy(n => n));
        }
        // Plotter's tools come first: layout_pack moves ahead of the bake tools it followed.
        var general = ForskToolPacks.For(ForskIntent.General).ToList();
        var plotter = ForskToolPacks.For(ForskIntent.General, ForskRole.Plotter).ToList();
        Assert.Equal(general.Count, plotter.Count);
        Assert.True(general.IndexOf("layout_pack") > general.IndexOf("floor_from_layer"));
        Assert.True(plotter.IndexOf("layout_pack") < plotter.IndexOf("floor_from_layer"));
        Assert.True(plotter.IndexOf("get_document_summary") > plotter.IndexOf("print_profile"));
        // v3: the Plotter pack sets the sheets (print_pages) on a Print and a Sheets turn.
        Assert.Contains("print_pages", ForskToolPacks.For(ForskIntent.Print));
        Assert.Contains("print_pages", ForskToolPacks.For(ForskIntent.Sheets));
        Assert.True(plotter.IndexOf("print_pages") < plotter.IndexOf("floor_from_layer"));
        Assert.True(plotter.IndexOf("takeoff") < plotter.IndexOf("floor_from_layer"));
        Assert.Contains("takeoff", ForskToolPacks.For(ForskIntent.Takeoff));
        Assert.DoesNotContain("layout_pack", ForskToolPacks.For(ForskIntent.Takeoff));

        // Analyser puts its tools first. area_stats joins daylight and rooms detection. The count is the pack's: nothing added or removed.
        var analyser = ForskToolPacks.For(ForskIntent.General, ForskRole.Analyser).ToList();
        Assert.Equal(general.Count, analyser.Count);
        Assert.Equal("rooms_detect", analyser[0]);
        Assert.Equal(ForskToolPacks.DaylightTool, analyser[1]);
        Assert.Equal("area_stats", analyser[2]);
        Assert.True(analyser.IndexOf("add_opening") > analyser.IndexOf(ForskToolPacks.DaylightTool));
        var daylight = ForskToolPacks.For(ForskIntent.Daylight).ToList();
        var daylightAsAnalyser = ForskToolPacks.For(ForskIntent.Daylight, ForskRole.Analyser).ToList();
        Assert.Equal(daylight.Count, daylightAsAnalyser.Count);
        Assert.Equal(new[] { ForskToolPacks.DaylightTool, "rooms_detect", "area_stats" }, daylightAsAnalyser.Take(3));
        Assert.Contains("add_opening", daylightAsAnalyser);
        Assert.Contains("area_stats", daylight);
        var area = ForskToolPacks.For(ForskIntent.Area).ToList();
        Assert.Contains("area_stats", area);
        Assert.Contains("rooms_detect", area);
        Assert.DoesNotContain("move_wall", area);
        Assert.Equal(area.Count, ForskToolPacks.For(ForskIntent.Area, ForskRole.Analyser).Count);
        Assert.Equal(new[] { "area_stats", "rooms_detect" }, ForskToolPacks.For(ForskIntent.Area, ForskRole.Analyser).Take(2));

        // Support reads the model and the selection, then the debug report. It adds nothing to another pack and edits nothing.
        var support = ForskToolPacks.For(ForskIntent.Support).ToList();
        Assert.Equal(new[] { "get_document_summary", "get_selected_objects_info", "get_object_info", "get_objects", ForskToolPacks.DebugTool }, support);
        Assert.Equal(support.Count, ForskToolPacks.Schemas(support).Count);
        Assert.DoesNotContain(support, n => n.Contains("move") || n.Contains("delete") || n.Contains("add") || n == "select_objects" || n == "capture_viewport" || n == "clear_generated");
        Assert.False(ForskToolPacks.Allows(ForskIntent.Support, "move_wall"));
        Assert.False(ForskToolPacks.Allows(ForskIntent.Support, "select_objects"));
        Assert.True(ForskToolPacks.Allows(ForskIntent.Support, ForskToolPacks.DebugTool));
        Assert.True(ForskToolPacks.Allows(ForskIntent.Edit, "move_wall"));
        var asSupport = ForskToolPacks.For(ForskIntent.General, ForskRole.Support).ToList();
        Assert.Equal(general.Count, asSupport.Count);
        Assert.DoesNotContain(ForskToolPacks.DebugTool, asSupport);
        Assert.Contains("move_wall", asSupport);
    }

    /// <summary>The measurement, and the report when FORSK_PACK_REPORT names a path.</summary>
    [Fact]
    public void TheMeasurement_OnTheRecordedPrompts()
    {
        var union = ForskToolPacks.Union;
        var intents = new JObject();
        foreach (var intent in Enum.GetValues(typeof(ForskIntent)).Cast<ForskIntent>())
        {
            var names = ForskToolPacks.For(intent);
            intents[RouterNames.First(p => p.Value == intent).Key] = new JObject { ["tools"] = names.Count, ["schema_chars"] = Chars(names) };
        }
        var cases = new JArray();
        int before = 0, after = 0, charsBefore = 0, charsAfter = 0;
        foreach (var c in Recorded())
        {
            var intent = Route(c);
            var names = ForskToolPacks.For(intent);
            cases.Add(new JObject { ["id"] = c["id"], ["intent"] = RouterNames.First(p => p.Value == intent).Key, ["tools"] = names.Count });
            before += union.Count;
            after += names.Count;
            charsBefore += Chars(union);
            charsAfter += Chars(names);
        }
        Assert.True(after < before);
        Assert.True(charsAfter < charsBefore);

        var report = new JObject
        {
            ["version"] = 1,
            ["source"] = "rhino-7-mcp tests/SoftParam.Tests/ToolPackTests.cs over the 30 panel prompts in docs/metrics/jev_intent_fixtures.json suite B. Offline: the keyword router and the tool packs, no model call.",
            ["union"] = new JObject { ["tools"] = union.Count, ["schema_chars"] = Chars(union) },
            ["intents"] = intents,
            ["prompts"] = new JObject
            {
                ["count"] = cases.Count,
                ["tools_sent_before"] = before,
                ["tools_sent_after"] = after,
                ["schema_chars_before"] = charsBefore,
                ["schema_chars_after"] = charsAfter
            },
            ["cases"] = cases
        };
        var target = Environment.GetEnvironmentVariable("FORSK_PACK_REPORT");
        if (!string.IsNullOrWhiteSpace(target))
            File.WriteAllText(target, report.ToString(Formatting.Indented) + "\n");
    }
}
