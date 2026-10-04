using System.Drawing;
using System.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F5.4 print profiles, headless: the default is the F5.0 look to the number
/// (solid black poché, black pens at 0.50 / 0.35 / 0.18 / 0.13 mm), an
/// alternative changes every tier it should, names resolve the way a user says
/// them, and chat routes a profile request to the sheets turn. Whether the
/// drawing code reads the profile is what the live smokes show.
/// </summary>
public class PrintProfilesTests
{
    [Fact]
    public void The_default_is_what_F5_0_drew()
    {
        var p = PrintProfiles.Default;
        Assert.Equal("default", p.Name);
        Assert.Equal(0.50, p.Cut.Mm);
        Assert.Equal(0.35, p.Silhouette.Mm);
        Assert.Equal(0.18, p.Beyond.Mm);
        Assert.Equal(0.13, p.Thin.Mm);
        foreach (var colour in new[] { p.Cut.Color, p.Silhouette.Color, p.Beyond.Color, p.Thin.Color, p.Dashed, p.Text, p.Poche })
            Assert.Equal(Color.Black.ToArgb(), colour.ToArgb());
        Assert.True(p.PocheSolid);
        Assert.Equal("Solid", p.PochePattern);
        // Solid never depends on a hatch scale.
        Assert.Equal(1.0, PrintProfiles.HatchScale(p, 100));
    }

    [Fact]
    public void The_active_profile_is_the_default_until_one_is_set_and_never_null()
    {
        var before = PrintProfiles.Active;
        try
        {
            PrintProfiles.Active = null;
            Assert.Equal("default", PrintProfiles.Active.Name);
            PrintProfiles.Active = PrintProfiles.Grey;
            Assert.Equal("grey", PrintProfiles.Active.Name);
            Assert.Equal(PrintProfiles.Grey.Poche.ToArgb(), PrintProfiles.Active.Poche.ToArgb());
        }
        finally
        {
            PrintProfiles.Active = before;
        }
    }

    [Fact]
    public void The_grey_profile_is_visibly_different_in_poche_and_every_tier()
    {
        var d = PrintProfiles.Default;
        var g = PrintProfiles.Grey;
        Assert.NotEqual(d.Poche.ToArgb(), g.Poche.ToArgb());
        Assert.True(g.PocheSolid);
        Assert.NotEqual(d.Cut.Color.ToArgb(), g.Cut.Color.ToArgb());
        Assert.NotEqual(d.Beyond.Color.ToArgb(), g.Beyond.Color.ToArgb());
        Assert.NotEqual(d.Thin.Color.ToArgb(), g.Thin.Color.ToArgb());
        Assert.NotEqual(d.Dashed.ToArgb(), g.Dashed.ToArgb());
        // The tiers stay tiers: a cut line is never lighter than what lies beyond, nor colours collide.
        Assert.True(g.Cut.Mm > g.Beyond.Mm && g.Beyond.Mm > g.Thin.Mm);
        Assert.Equal(3, new[] { g.Cut.Color, g.Beyond.Color, g.Thin.Color }.Select(c => c.ToArgb()).Distinct().Count());
        // The poché reads as grey: equal channels, neither black nor white.
        Assert.True(g.Poche.R == g.Poche.G && g.Poche.G == g.Poche.B && g.Poche.R > 0 && g.Poche.R < 255);
    }

    [Fact]
    public void Every_shipped_pen_is_black_or_grey()
    {
        foreach (var profile in PrintProfiles.All)
        {
            foreach (var colour in new[]
            {
                profile.Cut.Color, profile.Silhouette.Color, profile.Beyond.Color,
                profile.Thin.Color, profile.Dashed, profile.Text, profile.Poche
            })
            {
                Assert.Equal(colour.R, colour.G);
                Assert.Equal(colour.G, colour.B);
            }
        }
    }

    [Fact]
    public void The_hatch_profile_hatches_the_poche_and_lightens_the_cut()
    {
        var h = PrintProfiles.Hatched;
        Assert.False(h.PocheSolid);
        Assert.Equal("Hatch1", h.PochePattern);
        Assert.True(h.Cut.Mm < PrintProfiles.Default.Cut.Mm);
        // 1.5 mm of paper between lines on a 1:100 sheet is 150 model units at one unit a line.
        Assert.Equal(150.0, PrintProfiles.HatchScale(h, 100), 6);
        Assert.Equal(75.0, PrintProfiles.HatchScale(h, 50), 6);
        // A sheet scale below 1 cannot divide the spacing away.
        Assert.True(PrintProfiles.HatchScale(h, 0) > 0);
    }

    [Fact]
    public void Every_profile_has_its_own_name_and_the_default_is_first()
    {
        var all = PrintProfiles.All;
        Assert.Equal("default", all[0].Name);
        Assert.True(all.Count >= 2);
        Assert.Equal(all.Count, all.Select(p => p.Name).Distinct().Count());
        foreach (var p in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.Equal(p.Name, PrintProfiles.Find(p.Name)?.Name);
        }
    }

    [Theory]
    [InlineData("default", "default")]
    [InlineData("Default", "default")]
    [InlineData("standard", "default")]
    [InlineData("svart", "default")]
    [InlineData("grey", "grey")]
    [InlineData("Gray", "grey")]
    [InlineData("grå", "grey")]
    [InlineData(" grey ", "grey")]
    [InlineData("hatch", "hatch")]
    [InlineData("hatched", "hatch")]
    [InlineData("skravert", "hatch")]
    public void Names_resolve_the_way_a_user_says_them(string said, string name)
    {
        Assert.Equal(name, PrintProfiles.Find(said)?.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("neon")]
    public void An_unknown_name_finds_nothing_and_a_stored_one_falls_back_to_the_default(string said)
    {
        Assert.Null(PrintProfiles.Find(said));
        Assert.Equal("default", PrintProfiles.FromStored(said).Name);
    }

    [Fact]
    public void The_next_profile_wraps_round_the_list()
    {
        var name = PrintProfiles.Default.Name;
        var seen = new System.Collections.Generic.List<string> { name };
        var p = PrintProfiles.Default;
        for (var i = 0; i < PrintProfiles.All.Count; i++)
        {
            p = PrintProfiles.Next(p);
            seen.Add(p.Name);
        }
        Assert.Equal(name, seen.Last());
        Assert.Equal(PrintProfiles.All.Count, seen.Take(PrintProfiles.All.Count).Distinct().Count());
        Assert.Equal("default", PrintProfiles.Next(null).Name);
    }

    [Fact]
    public void The_record_lists_every_pen_as_the_tool_returns_it()
    {
        var record = PrintProfiles.Grey.Record();
        Assert.Equal("grey", (string)record["name"]);
        Assert.Equal(0.5, (double)record["cut"]["mm"]);
        Assert.Equal("30,30,30", (string)record["cut"]["rgb"]);
        Assert.Equal("150,150,150", (string)record["poche"]["rgb"]);
        Assert.Equal("Solid", (string)record["poche"]["pattern"]);
        Assert.Equal(PrintProfiles.All.Count, PrintProfiles.Available().Count);
    }

    [Theory]
    [InlineData("use the grey profile", ForskIntent.Sheets)]
    [InlineData("switch to the hatch profile", ForskIntent.Sheets)]
    [InlineData("bytt til grå profil", ForskIntent.Sheets)]
    [InlineData("hatched poché please", ForskIntent.Sheets)]
    [InlineData("grey poche", ForskIntent.Sheets)]
    [InlineData("print with the grey profile", ForskIntent.Print)]
    [InlineData("make the door wider", ForskIntent.Edit)]
    public void Chat_routes_a_profile_request_to_the_sheets_turn(string said, ForskIntent intent)
    {
        Assert.Equal(intent, ForskIntentRouter.Classify(said));
    }

    [Fact]
    public void At_1_20_a_detail_draws_heavier_cuts_and_keeps_thin_lines()
    {
        var p = PrintProfiles.AtScale(PrintProfiles.Default, 20);
        Assert.Equal(0.70, p.Cut.Mm);
        Assert.Equal(0.50, p.Silhouette.Mm);
        Assert.Equal(0.25, p.Beyond.Mm);
        Assert.Equal(0.13, p.Thin.Mm);
        Assert.Equal(PrintProfiles.Default.Name, p.Name);
    }

    [Fact]
    public void At_1_50_the_profile_is_unchanged()
    {
        var p = PrintProfiles.AtScale(PrintProfiles.Default, 50);
        Assert.Equal(new[] { 0.50, 0.35, 0.18, 0.13 }, new[] { p.Cut.Mm, p.Silhouette.Mm, p.Beyond.Mm, p.Thin.Mm });
    }
}
