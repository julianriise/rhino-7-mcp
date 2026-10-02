using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A miss, or a weak bug word on a sentence that already names a task, is
/// General with the full tool pack. A clear Support sentence keeps its short list.
/// </summary>
public class RouterFallbackTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("what's the weather in Oslo tomorrow?")]
    [InlineData("what makes a living room feel bright?")]
    [InlineData("")]
    public void NoMatch_IsUnsure_AndGeneral(string text)
    {
        var route = ForskIntentRouter.Route(text);
        Assert.True(route.Unsure);
        Assert.Equal(0, route.Confidence);
        Assert.Equal(ForskIntent.General, route.Intent);
        Assert.Equal(ForskIntent.General, ForskIntentRouter.Classify(text));
        Assert.Equal(ForskToolPacks.Union, ForskToolPacks.For(route.Intent));
    }

    [Fact]
    public void AWeakBugWord_OnATask_DoesNotTakeSupportsPack()
    {
        var route = ForskIntentRouter.Route("the print is wrong");
        Assert.True(route.Unsure);
        Assert.Equal(0, route.Confidence);
        Assert.Equal(ForskIntent.General, route.Intent);
        var sent = ForskToolPacks.For(route.Intent);
        Assert.Equal(ForskToolPacks.Union, sent);
        Assert.True(sent.Count > ForskToolPacks.For(ForskIntent.Support).Count);
        Assert.Contains("export_pdf", sent);
        Assert.Contains("layout_pack", sent);
    }

    [Theory]
    [InlineData("this is broken")]
    [InlineData("how do I print")]
    [InlineData("why did my dxf labels come in wrong")]
    [InlineData("can you add a stair tool")]
    [InlineData("feil")]
    public void AClearSupportSentence_StaysSupport_AndNarrow(string text)
    {
        var route = ForskIntentRouter.Route(text);
        Assert.False(route.Unsure);
        Assert.Equal(1, route.Confidence);
        Assert.Equal(ForskIntent.Support, route.Intent);
        var sent = ForskToolPacks.For(route.Intent);
        Assert.Equal(new[] { "get_document_summary", "get_selected_objects_info", "get_object_info", "get_objects", "debug_report" }, sent);
        Assert.True(sent.Count < ForskToolPacks.Union.Count);
    }

    [Theory]
    [InlineData("run daylight", ForskIntent.Daylight)]
    [InlineData("is this room dark", ForskIntent.Daylight)]
    [InlineData("which room is darkest?", ForskIntent.Daylight)]
    [InlineData("print PDF", ForskIntent.Print)]
    [InlineData("put a window in the dark corner of the living room", ForskIntent.Edit)]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit)]
    public void AClearTask_IsSure_AndNarrowerThanTheUnion(string text, ForskIntent intent)
    {
        var route = ForskIntentRouter.Route(text);
        Assert.False(route.Unsure);
        Assert.Equal(1, route.Confidence);
        Assert.Equal(intent, route.Intent);
        Assert.True(ForskToolPacks.For(route.Intent).Count < ForskToolPacks.Union.Count);
    }
}
