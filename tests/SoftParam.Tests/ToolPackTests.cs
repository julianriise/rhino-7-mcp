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
        ["general"] = ForskIntent.General
    };

    static IEnumerable<JObject> Recorded()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "recorded_prompts.json");
        return ((JArray)JObject.Parse(File.ReadAllText(path))["cases"]!).Cast<JObject>();
    }

    static ForskIntent Route(JObject c)
    {
        var selection = c["selection"]!.ToString();
        return ForskIntentRouter.Classify(c["utterance"]!.ToString(), selection == "empty" ? "" : "Target: " + selection);
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

        // Analyser puts daylight and rooms detection first. The count is the pack's: nothing added or removed.
        var analyser = ForskToolPacks.For(ForskIntent.General, ForskRole.Analyser).ToList();
        Assert.Equal(general.Count, analyser.Count);
        Assert.Equal("rooms_detect", analyser[0]);
        Assert.Equal(ForskToolPacks.DaylightTool, analyser[1]);
        Assert.True(analyser.IndexOf("add_opening") > analyser.IndexOf(ForskToolPacks.DaylightTool));
        var daylight = ForskToolPacks.For(ForskIntent.Daylight).ToList();
        var daylightAsAnalyser = ForskToolPacks.For(ForskIntent.Daylight, ForskRole.Analyser).ToList();
        Assert.Equal(daylight.Count, daylightAsAnalyser.Count);
        Assert.Equal(new[] { ForskToolPacks.DaylightTool, "rooms_detect" }, daylightAsAnalyser.Take(2));
        Assert.Contains("add_opening", daylightAsAnalyser);
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
