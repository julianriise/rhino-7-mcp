using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// One scale for the set, from the standard list (1:5 through 1:1000).
/// Spans are the baked drawings' boxes in model mm, the detail is A3 less
/// margins and the footer reserve (400 × 264 mm).
/// </summary>
public class SheetScaleTests
{
    static readonly SheetScale.Span Detail = new SheetScale.Span(400, 264);

    // The garage, 8 × 4 m, with its dimension rows and tags: about 11 × 7 m.
    static readonly SheetScale.Span GaragePlan = new SheetScale.Span(11000, 7000);
    static readonly SheetScale.Span GarageFacade = new SheetScale.Span(8400, 3600);
    // The office: the poché is 30243 × 22510, with the dimension rows about 33 × 26 m.
    static readonly SheetScale.Span OfficePlan = new SheetScale.Span(33000, 26000);
    static readonly SheetScale.Span OfficeFacade = new SheetScale.Span(30600, 4200);
    static readonly SheetScale.Span OfficeSection = new SheetScale.Span(23000, 5200);

    [Fact]
    public void TheLadder_IsTheStandardList_FinestFirst()
    {
        Assert.Equal(new[]
        {
            5, 10, 20, 25, 50, 75, 100, 125, 150, 175, 200, 250, 300, 350, 400, 500, 750, 1000
        }, SheetScale.Ladder);
        Assert.Equal(50, SheetScale.Listed(30));
        Assert.Equal(75, SheetScale.Listed(75));
        Assert.Equal(100, SheetScale.Listed(80));
        Assert.Equal(1000, SheetScale.Listed(2000));
        Assert.Equal(0, SheetScale.Parse("Fit"));
        Assert.Equal(200, SheetScale.Parse("1:200"));
    }

    [Fact]
    public void Garage_PrintsAt50()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, GarageFacade, GarageFacade }, Detail, null);
        Assert.Equal(50, picked.Scale);
        Assert.All(picked.Scales, s => Assert.Equal(50, s));
        Assert.Empty(picked.Bumped);
        // The facade alone fits 1:25. The plan needs 1:50, and the set keeps one scale.
        Assert.Equal(25, SheetScale.Pick(new[] { GarageFacade }, Detail, null).Scale);
    }

    [Fact]
    public void Office_PrintsAt125_EverySheet()
    {
        var picked = SheetScale.Pick(new[] { OfficePlan, OfficeFacade, OfficeFacade, OfficeSection }, Detail, null);
        Assert.Equal(125, picked.Scale);
        // The facades alone would fit 1:100; the set keeps one scale.
        Assert.Equal(100, SheetScale.Pick(new[] { OfficeFacade }, Detail, null).Scale);
        Assert.All(picked.Scales, s => Assert.Equal(125, s));
    }

    [Fact]
    public void OneSectionTooLong_PutsTheWholeSetAt250()
    {
        var longSection = new SheetScale.Span(80000, 6000);
        var spans = new[] { OfficePlan, OfficeFacade, longSection };

        var fitted = SheetScale.Pick(spans, Detail, null);
        Assert.Equal(250, fitted.Scale);
        Assert.All(fitted.Scales, s => Assert.Equal(250, s));
        Assert.Empty(fitted.Bumped);

        // Asked 1:200 does not fit the section, so the whole set steps to 1:250.
        var asked = SheetScale.Pick(spans, Detail, 200);
        Assert.Equal(250, asked.Scale);
        Assert.Equal(new[] { 250, 250, 250 }, asked.Scales);
        Assert.Empty(asked.Bumped);
    }

    [Fact]
    public void Asked50_OnTheGarage_Is50()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, GarageFacade }, Detail, 50);
        Assert.Equal(50, picked.Scale);
        Assert.Equal(new[] { 50, 50 }, picked.Scales);
    }

    [Fact]
    public void Asked200_OnTheOffice_Stays200()
    {
        var picked = SheetScale.Pick(new[] { OfficePlan, OfficeFacade, OfficeSection }, Detail, 200);
        Assert.Equal(200, picked.Scale);
        Assert.All(picked.Scales, s => Assert.Equal(200, s));
    }

    [Fact]
    public void AnAskedScaleOffTheList_Snaps_ThenTheWholeSetStepsUp()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, OfficePlan }, Detail, 30);
        Assert.Equal(125, picked.Scale);
        Assert.Equal(new[] { 125, 125 }, picked.Scales);
        // 1:75 is on the list and still does not fit the office.
        var named = SheetScale.Pick(new[] { GaragePlan, OfficePlan }, Detail, 75);
        Assert.Equal(125, named.Scale);
        Assert.All(named.Scales, s => Assert.Equal(125, s));
    }

    [Fact]
    public void TooBigForEveryStep_StaysOnTheLastStep()
    {
        var picked = SheetScale.Pick(new[] { new SheetScale.Span(400000, 1000) }, Detail, null);
        Assert.Equal(1000, picked.Scale);
        Assert.False(SheetScale.Fits(new SheetScale.Span(400000, 1000), Detail, 1000));
    }

    [Fact]
    public void NoDrawing_IsTheFirstStep()
    {
        Assert.Equal(100, SheetScale.Pick(new SheetScale.Span[0], Detail, null).Scale);
        Assert.Equal(50, SheetScale.Pick(new SheetScale.Span[0], Detail, 50).Scale);
    }

    [Fact]
    public void Need_FillsNinetyPercentOfTheDetail_OnTheTighterSide()
    {
        Assert.Equal(36000.0 / 360.0, SheetScale.Need(new SheetScale.Span(36000, 1000), Detail), 6);
        Assert.Equal(26000.0 / (264 * 0.9), SheetScale.Need(OfficePlan, Detail), 6);
        Assert.True(SheetScale.Fits(new SheetScale.Span(36000, 1000), Detail, 100));
        Assert.False(SheetScale.Fits(new SheetScale.Span(36001, 1000), Detail, 100));
    }

    [Fact]
    public void PlanAndFacadeInOnePack_ReportTheSamePageScale()
    {
        var picked = SheetScale.Pick(new[] { OfficePlan, OfficeFacade }, Detail, null);
        Assert.Equal(picked.Scales[0], picked.Scales[1]);
        Assert.Equal(125, picked.Scale);
    }

    [Fact]
    public void Again_AfterTagsGrewAtTheNewScale_OnlyStepsUp()
    {
        var first = SheetScale.Pick(new[] { OfficePlan }, Detail, null);
        Assert.Equal(125, first.Scale);
        var grown = new SheetScale.Span(73000, 30000);
        var second = SheetScale.Pick(new[] { grown }, Detail, null, first.Scales);
        Assert.Equal(250, second.Scale);
        // A smaller box never steps back down.
        var third = SheetScale.Pick(new[] { GaragePlan }, Detail, null, second.Scales);
        Assert.Equal(250, third.Scale);
    }

    [Fact]
    public void Clause_NamesABumpedSheet_InOneClause()
    {
        Assert.Equal("", SheetScale.Clause(new string[0], new int[0]));
        Assert.Equal(" Section A–A at 1:500 to fit.", SheetScale.Clause(new[] { "Section A–A" }, new[] { 500 }));
        Assert.Equal(" Section A–A and Section B–B at 1:500 to fit.",
            SheetScale.Clause(new[] { "Section A–A", "Section B–B" }, new[] { 500, 500 }));
        Assert.Equal(" Ground floor plan at 1:200 and Section A–A at 1:500 to fit.",
            SheetScale.Clause(new[] { "Ground floor plan", "Section A–A" }, new[] { 200, 500 }));
    }

    [Fact]
    public void TheSetScale_IgnoresTheDetailSheets()
    {
        // The pack sets the detail sheets aside before it builds the spans Pick reads.
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "LayoutPack.cs"));
        var aside = source.IndexOf("detailViews.Add(viewName);\n                continue;", StringComparison.Ordinal);
        var spans = source.IndexOf("sheets.Add(sheet);", StringComparison.Ordinal);
        var pick = source.IndexOf("SheetScale.Pick(sheets.Select(PackSpan).ToList()", StringComparison.Ordinal);
        Assert.True(aside > 0 && aside < spans && spans < pick, "a detail sheet is set aside before it becomes a span");
        Assert.Contains("if (Details.TrySheetId(viewName, out _, out _))", source);
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
