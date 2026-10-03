using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// v3 P2: one scale for the set, from a fixed ladder (1:100, 1:200, 1:500;
/// 1:50 only when asked). Spans are the baked drawings' boxes in model mm,
/// the detail is A3 less margins and the footer reserve (400 × 264 mm).
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
    public void TheLadder_IsFiftyOnRequest_ThenOneTwoFiveHundred()
    {
        Assert.Equal(new[] { 50, 100, 200, 500 }, SheetScale.Ladder);
    }

    [Fact]
    public void Garage_PrintsAt100()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, GarageFacade, GarageFacade }, Detail, null);
        Assert.Equal(100, picked.Scale);
        Assert.All(picked.Scales, s => Assert.Equal(100, s));
        Assert.Empty(picked.Bumped);
    }

    [Fact]
    public void Office_PrintsAt200_EverySheet_NotTheOldFit125()
    {
        var picked = SheetScale.Pick(new[] { OfficePlan, OfficeFacade, OfficeFacade, OfficeSection }, Detail, null);
        Assert.Equal(200, picked.Scale);
        // The facades alone would fit 1:100; the set keeps one scale.
        Assert.All(picked.Scales, s => Assert.Equal(200, s));
    }

    [Fact]
    public void OneSectionTooLong_PutsTheWholeSetAt500_UnlessAScaleWasAsked()
    {
        var longSection = new SheetScale.Span(80000, 6000);
        var spans = new[] { OfficePlan, OfficeFacade, longSection };

        var fitted = SheetScale.Pick(spans, Detail, null);
        Assert.Equal(500, fitted.Scale);
        Assert.All(fitted.Scales, s => Assert.Equal(500, s));

        var asked = SheetScale.Pick(spans, Detail, 200);
        Assert.Equal(200, asked.Scale);
        Assert.Equal(new[] { 200, 200, 500 }, asked.Scales);
        Assert.Equal(new[] { 2 }, asked.Bumped);
    }

    [Fact]
    public void Asked50_OnTheGarage_Is50()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, GarageFacade }, Detail, 50);
        Assert.Equal(50, picked.Scale);
        Assert.Equal(new[] { 50, 50 }, picked.Scales);
    }

    [Fact]
    public void AnAskedScaleOffTheLadder_StandsWhereItFits_AndBumpsOntoTheLadder()
    {
        var picked = SheetScale.Pick(new[] { GaragePlan, OfficePlan }, Detail, 75);
        Assert.Equal(75, picked.Scale);
        Assert.Equal(new[] { 75, 200 }, picked.Scales);
    }

    [Fact]
    public void TooBigForEveryStep_StaysOnTheLastStep()
    {
        var picked = SheetScale.Pick(new[] { new SheetScale.Span(400000, 1000) }, Detail, null);
        Assert.Equal(500, picked.Scale);
        Assert.False(SheetScale.Fits(new SheetScale.Span(400000, 1000), Detail, 500));
    }

    [Fact]
    public void NoDrawing_IsTheFirstStep()
    {
        Assert.Equal(100, SheetScale.Pick(new SheetScale.Span[0], Detail, null).Scale);
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
        // The pack's scale plan: every drawing sheet takes the set's scale.
        var picked = SheetScale.Pick(new[] { OfficePlan, OfficeFacade }, Detail, null);
        Assert.Equal(picked.Scales[0], picked.Scales[1]);
    }

    [Fact]
    public void Again_AfterTagsGrewAtTheNewScale_OnlyStepsUp()
    {
        // Baked at 1:100 the plan needed 1:200; baked at 1:200 its tags grew past it.
        var first = SheetScale.Pick(new[] { OfficePlan }, Detail, null);
        var grown = new SheetScale.Span(73000, 30000);
        var second = SheetScale.Pick(new[] { grown }, Detail, null, first.Scales);
        Assert.Equal(500, second.Scale);
        // A smaller box never steps back down.
        var third = SheetScale.Pick(new[] { GaragePlan }, Detail, null, second.Scales);
        Assert.Equal(500, third.Scale);
    }

    [Fact]
    public void Clause_NamesABumpedSheet_InOneClause()
    {
        Assert.Equal("", SheetScale.Clause(new string[0], new int[0]));
        Assert.Equal(" Snitt A–A at 1:500 to fit.", SheetScale.Clause(new[] { "Snitt A–A" }, new[] { 500 }));
        Assert.Equal(" Snitt A–A and Snitt B–B at 1:500 to fit.",
            SheetScale.Clause(new[] { "Snitt A–A", "Snitt B–B" }, new[] { 500, 500 }));
        Assert.Equal(" Plan 1. etg at 1:200 and Snitt A–A at 1:500 to fit.",
            SheetScale.Clause(new[] { "Plan 1. etg", "Snitt A–A" }, new[] { 200, 500 }));
    }
}
