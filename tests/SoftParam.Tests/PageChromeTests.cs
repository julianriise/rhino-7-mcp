using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The v3 window's chrome. The role picker lives in the header. The composer
/// keeps import, the field, and send. The page script decides the face and a
/// bullet's lead without a document.
/// </summary>
public class PageChromeTests
{
    static string Html() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));

    [Fact]
    public void TheRolePicker_SitsInTheHeader_NotInTheComposer()
    {
        var html = Html();
        var headerAt = html.IndexOf("<header", StringComparison.Ordinal);
        var headerEnd = html.IndexOf("</header>", headerAt, StringComparison.Ordinal);
        var header = html.Substring(headerAt, headerEnd - headerAt);
        var formAt = html.IndexOf("<form id=\"composer\"", StringComparison.Ordinal);
        var formEnd = html.IndexOf("</form>", formAt, StringComparison.Ordinal);
        var composer = html.Substring(formAt, formEnd - formAt);

        Assert.Contains("id=\"role-pill\"", header);
        Assert.Contains("id=\"role-menu\"", header);
        Assert.Contains("id=\"history\"", header);
        Assert.Contains("id=\"more\"", header);
        Assert.Contains("id=\"file\"", header);
        Assert.DoesNotContain("class=\"logo\"", header);
        Assert.DoesNotContain("<select", html);
        Assert.DoesNotContain("role", composer);
        Assert.Contains("Ask Forsk", composer);
        Assert.Contains("id=\"add\"", composer);
        Assert.Contains("id=\"send\"", composer);
        Assert.Contains("%%AVATAR_PLANNER%%", html);
        Assert.Contains("%%AVATAR_MODELLER%%", html);
        Assert.Contains("%%AVATAR_PLOTTER%%", html);
        Assert.Contains("%%AVATAR_RENDER%%", html);
        foreach (var name in new[] { "avatar-planner.svg", "avatar-modeller.svg", "avatar-plotter.svg", "avatar-render.svg" })
            Assert.True(new FileInfo(Path.Combine(AppContext.BaseDirectory, "page", name)).Length > 1000, name);
    }

    [Fact]
    public void TheHeaderFace_IsTheOverride_ElseTheLastRole_ElsePlanner()
    {
        var engine = PageScript.Load();
        string Eval(string js) => engine.Evaluate(js).ToString();

        Assert.Equal("render", Eval("Forsk.shownRole({role:{value:'render'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[{mark:'Modeller'},{mark:'Daylight'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[]})"));
        Assert.Equal("office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm', role:{value:'planner'}})"));
        Assert.Equal("auto \u00b7 office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm', role:{value:'auto'}})"));
        Assert.Equal("office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm'})"));
        Assert.Equal("[\"planner\",\"render\",\"auto\"]", Eval("JSON.stringify(Forsk.roleMenu([{id:'auto',label:'Auto'},{id:'planner',label:'Planner'},{id:'render',label:'Render'}]).map(function(o){return o.id;}))"));
    }

    [Fact]
    public void ABulletLine_BoldsItsLeadWords()
    {
        var engine = PageScript.Load();
        string Eval(string js) => engine.Evaluate(js).ToString();

        Assert.Equal("[\"- \",\"Checked the plan\",\". The walls are 2700.\"]", Eval("JSON.stringify(Forsk.leadWords('- Checked the plan. The walls are 2700.'))"));
        Assert.Equal("[\"- \",\"one two three four\",\" five\"]", Eval("JSON.stringify(Forsk.leadWords('- one two three four five'))"));
        Assert.Equal("null", Eval("JSON.stringify(Forsk.leadWords('Hello'))"));
    }
}
