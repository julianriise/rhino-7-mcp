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
        Assert.Contains("aria-label=\"Settings\"", header);
        Assert.Contains("id=\"file\"", header);
        Assert.DoesNotContain("class=\"logo\"", header);
        Assert.DoesNotContain("<select", html);
        Assert.DoesNotContain("role", composer);
        Assert.Contains("Ask Forsk", composer);
        Assert.Contains("id=\"add\"", composer);
        Assert.Contains("id=\"send\"", composer);
    }

    /// <summary>
    /// The header trigger is a settings gear, inline, at the same size and
    /// stroke as History. The three dots are gone. The red dot is an element
    /// the page shows from the attention list.
    /// </summary>
    [Fact]
    public void TheHeader_HasASettingsGear_AndNoThreeDots()
    {
        var html = Html();
        var headerAt = html.IndexOf("<header", StringComparison.Ordinal);
        var headerEnd = html.IndexOf("</header>", headerAt, StringComparison.Ordinal);
        var header = html.Substring(headerAt, headerEnd - headerAt);
        var buttonAt = header.IndexOf("id=\"more\"", StringComparison.Ordinal);
        var buttonEnd = header.IndexOf("</button>", buttonAt, StringComparison.Ordinal);
        var button = header.Substring(buttonAt, buttonEnd - buttonAt);

        Assert.Contains("aria-label=\"Settings\"", button);
        Assert.Contains("aria-haspopup=\"menu\"", button);
        Assert.Contains("aria-controls=\"more-menu\"", button);
        Assert.Contains("class=\"gear\"", button);
        Assert.Contains("id=\"gear-dot\"", button);
        Assert.Contains("class=\"notice\"", button);
        Assert.Contains("width=\"16\" height=\"16\"", button);
        Assert.Contains("stroke-width=\"1.7\"", button);
        Assert.Contains("M19.4 15", button);
        Assert.Contains("overflow=\"visible\"", button);
        Assert.DoesNotContain("aria-label=\"More\"", header);
        Assert.DoesNotContain("r=\"1.35\"", header);
        Assert.DoesNotContain("cx=\"6\"", header);
        Assert.DoesNotContain("cx=\"18\"", header);
        Assert.DoesNotContain("<img", button);
        Assert.DoesNotContain("data:image", button);
    }

    /// <summary>
    /// An img of the SVG is painted at CSS pixels, so the face was soft on
    /// Retina. The page inlines each face and every copy gets its own mask
    /// and gradient ids. The white is #ffffff: older WebKit drops lab().
    /// </summary>
    [Fact]
    public void TheFaces_AreInlineVectors_WithTheirOwnMaskIds()
    {
        var html = Html();
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));

        Assert.Contains("id=\"avatar\"", html);
        Assert.Contains("class=\"avatar\"", html);
        Assert.Contains("width: 44px; height: 44px", html);
        Assert.Contains(".mini { width: 20px; height: 20px;", html);
        Assert.Contains(".meta .mini { width: 16px; height: 16px;", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("data:image", html);
        Assert.DoesNotContain("image-rendering", html);
        Assert.DoesNotContain("will-change", html);
        Assert.Contains("cloneNode", script);
        Assert.Contains("Forsk.faceStamp", script);
        Assert.DoesNotContain("el('img'", script);
        Assert.DoesNotContain(".src", script);

        var engine = PageScript.Load();
        Assert.Equal("planner-1-mask", engine.Evaluate("Forsk.faceStamp('planner', 1).mask").ToString());
        Assert.Equal("planner-1-fill", engine.Evaluate("Forsk.faceStamp('planner', 1).fill").ToString());
        Assert.Equal("render-2-fill", engine.Evaluate("Forsk.faceStamp('render', 2).fill").ToString());
        Assert.NotEqual(
            engine.Evaluate("Forsk.faceStamp('planner', 1).mask").ToString(),
            engine.Evaluate("Forsk.faceStamp('modeller', 1).mask").ToString());

        foreach (var name in new[] { "planner", "modeller", "plotter", "analyser", "support", "render" })
        {
            Assert.Contains("id=\"avatar-" + name + "\"", html);
            Assert.Contains("%%AVATAR_" + name.ToUpperInvariant() + "%%", html);
            var svg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "avatar-" + name + ".svg"));
            Assert.True(svg.Length > 1000, name);
            Assert.DoesNotContain("lab(", svg);
            Assert.Contains("fill=\"#ffffff\"", svg);
            Assert.DoesNotContain("width=\"512\"", svg);
            Assert.DoesNotContain("height=\"512\"", svg);
            Assert.DoesNotContain("_r_4_", svg);
            Assert.Contains("id=\"" + name + "-mask\"", svg);
            Assert.Contains("url(#" + name + "-mask)", svg);
            Assert.Contains("id=\"" + name + "-fill\"", svg);
            Assert.Contains("url(#" + name + "-fill)", svg);
        }
    }

    [Fact]
    public void TheHeaderFace_IsTheOverride_ElseTheLastRole_ElsePlanner()
    {
        var engine = PageScript.Load();
        string Eval(string js) => engine.Evaluate(js).ToString();

        Assert.Equal("render", Eval("Forsk.shownRole({role:{value:'render'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("analyser", Eval("Forsk.shownRole({role:{value:'analyser'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("analyser", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[{mark:'Modeller'},{mark:'Analyser'}]})"));
        Assert.Equal("support", Eval("Forsk.shownRole({role:{value:'support'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("support", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[{mark:'Analyser'},{mark:'Support'}]})"));
        Assert.Equal("true", Eval("String(Forsk.fieldChecked({check:true, value:'1'}))"));
        Assert.Equal("false", Eval("String(Forsk.fieldChecked({check:true, value:'0'}))"));
        Assert.Equal("false", Eval("String(Forsk.fieldChecked({value:'1'}))"));
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[{mark:'Modeller'},{mark:'Daylight'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[]})"));
        Assert.Equal("office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm', role:{value:'planner'}})"));
        Assert.Equal("auto \u00b7 office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm', role:{value:'auto'}})"));
        Assert.Equal("office_2D.3dm", Eval("Forsk.roleSubtitle({file:'office_2D.3dm'})"));
        Assert.Equal("[\"planner\",\"render\",\"auto\"]", Eval("JSON.stringify(Forsk.roleMenu([{id:'auto',label:'Auto'},{id:'planner',label:'Planner'},{id:'render',label:'Render'}]).map(function(o){return o.id;}))"));
        Assert.Equal(
            "[\"planner\",\"modeller\",\"plotter\",\"analyser\",\"support\",\"render\",\"auto\"]",
            Eval("JSON.stringify(Forsk.roleMenu([{id:'auto',label:'Auto'},{id:'planner',label:'Planner'},{id:'modeller',label:'Modeller'},{id:'plotter',label:'Plotter'},{id:'analyser',label:'Analyser'},{id:'support',label:'Support'},{id:'render',label:'Render'}]).map(function(o){return o.id;}))"));
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
