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

    /// <summary>
    /// R1: every opening type has a pill icon in the page, drawn like its plan
    /// symbol: 24 units, currentColor, 1.5 stroke, no ids, so a copy needs no
    /// renaming. The card's pill passes its icon to pill().
    /// </summary>
    [Fact]
    public void EveryOpeningType_HasAnInlineIcon_ThePillDraws()
    {
        var html = Html();
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        var engine = PageScript.Load();

        foreach (var type in RhinoMCPPlugin.Functions.OpeningTypes.All)
        {
            var holder = engine.Evaluate("Forsk.iconId(" + PageScript.Quote(type.Id) + ")").ToString();
            Assert.Equal("icon-" + type.Id, holder);
            var at = html.IndexOf("<div id=\"" + holder + "\">", StringComparison.Ordinal);
            Assert.True(at > 0, type.Id);
            var svg = html.Substring(at, html.IndexOf("</div>", at, StringComparison.Ordinal) - at);
            Assert.Contains("viewBox=\"0 0 24 24\"", svg);
            Assert.Contains("stroke=\"currentColor\"", svg);
            Assert.Contains("stroke-width=\"1.5\"", svg);
            Assert.Equal(1, svg.Split(" id=").Length - 1);
        }
        var holders = html.IndexOf("<div id=\"icons\" hidden>", StringComparison.Ordinal);
        Assert.True(holders > 0 && holders < html.IndexOf("</header>", StringComparison.Ordinal));
        Assert.Contains("}, p.icon));", script);
        Assert.Contains(".pill .icon {", html);
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
        Assert.Equal("select", Eval("Forsk.fieldKind({options:['Bug','Question','Feature request'], value:'Bug'})"));
        Assert.Equal("long", Eval("Forsk.fieldKind({long:true, value:'id like to report a bug'})"));
        Assert.Equal("check", Eval("Forsk.fieldKind({check:true, value:'1'})"));
        Assert.Equal("text", Eval("Forsk.fieldKind({value:'1200', unit:'mm'})"));
        Assert.Equal("true", Eval("String(Forsk.fieldChecked({check:true, value:'1'}))"));
        Assert.Equal("false", Eval("String(Forsk.fieldChecked({check:true, value:'0'}))"));
        Assert.Equal("false", Eval("String(Forsk.fieldChecked({value:'1'}))"));
        // Choose sheets: ↑ on row 2 moves it first, and the pill posts that order with the values.
        Assert.Equal("[\"front\",\"plan\"]", Eval("JSON.stringify(Forsk.orderKeys([{key:'scale',options:['Fit']},{key:'front',order:true},{key:'plan',order:true}]))"));
        Assert.Equal("[\"b\",\"a\",\"c\"]", Eval("JSON.stringify(Forsk.moveKey(['a','b','c'], 'b', -1))"));
        Assert.Equal("[\"a\",\"c\",\"b\"]", Eval("JSON.stringify(Forsk.moveKey(['a','b','c'], 'b', 1))"));
        Assert.Equal("[\"a\",\"b\",\"c\"]", Eval("JSON.stringify(Forsk.moveKey(['a','b','c'], 'a', -1))"));
        Assert.Equal("{\"kind\":\"card\",\"card\":\"c1\",\"pill\":\"print\",\"values\":{\"a\":\"1\",\"b\":\"0\"},\"order\":[\"b\",\"a\"]}",
            Eval("JSON.stringify(Forsk.cardAction({id:'c1', fields:[{key:'a',order:true},{key:'b',order:true}]}, 'print', {a:'1',b:'0'}, Forsk.moveKey(['a','b'], 'b', -1)))"));
        // A card without ordered rows posts no order.
        Assert.Equal("{\"kind\":\"card\",\"card\":\"c2\",\"pill\":\"save\",\"values\":{\"project\":\"x\"}}",
            Eval("JSON.stringify(Forsk.cardAction({id:'c2', fields:[{key:'project'}]}, 'save', {project:'x'}, ['project']))"));
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

    /// <summary>
    /// A wrapped line was 32px, so the caret filled the row. The line is 1.4
    /// at 14px. The field's padding stays, and the auto-grow uses that same
    /// line height rather than the 32px row.
    /// </summary>
    [Fact]
    public void TheComposer_LineHeight_IsTheFont_AndTheGrowUsesIt()
    {
        var html = Html();
        var fieldAt = html.IndexOf(".field {", StringComparison.Ordinal);
        var at = html.IndexOf("textarea {", fieldAt, StringComparison.Ordinal);
        var end = html.IndexOf('}', at);
        var rule = html.Substring(at, end - at);
        var cardAt = html.IndexOf(".card textarea { height: auto", StringComparison.Ordinal);
        var cardRule = html.Substring(cardAt, html.IndexOf('}', cardAt) - cardAt);
        Assert.Contains("min-height: calc(15px * 1.4 * 4)", cardRule);
        var field = html.Substring(fieldAt, html.IndexOf('}', fieldAt) - fieldAt);

        Assert.Contains("font-size: 14px", rule);
        Assert.Contains("line-height: 1.4", rule);
        Assert.Contains("height: calc(14px * 1.4)", rule);
        Assert.Contains("max-height: calc(14px * 1.4 * 5)", rule);
        Assert.Contains("padding: 0", rule);
        Assert.DoesNotContain("line-height: 32px", rule);
        Assert.DoesNotContain("32px * 5", rule);
        Assert.Contains("padding: 6px 6px 6px 4px", field);
        Assert.Contains("min-height: 48px", field);

        var engine = PageScript.Load();
        Assert.Equal("20", engine.Evaluate("String(Forsk.composerHeight(20, 20))").ToString());
        Assert.Equal("40", engine.Evaluate("String(Forsk.composerHeight(40, 20))").ToString());
        Assert.Equal("20", engine.Evaluate("String(Forsk.composerHeight(0, 20))").ToString());
        Assert.Equal("39.2", engine.Evaluate("String(Forsk.composerHeight(39.2, 19.6))").ToString());
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("Forsk.composerHeight(q.scrollHeight, line)", script);
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

    /// <summary>
    /// A dash at the start of a line used to stay a dash. An answer renders
    /// unordered and ordered lists, one level deep, and bolds the lead.
    /// </summary>
    [Fact]
    public void AnAnswerList_RendersAsARealList()
    {
        var engine = PageScript.Load();
        string Render(string text) => engine.Evaluate("Forsk.answerHtml(" + PageScript.Quote(text) + ")").ToString();

        Assert.Equal(
            "<ul><li><span><b>Checked the plan</b>. The walls are 2700.</span></li><li><span><b>one two three four</b> five</span></li></ul>",
            Render("- Checked the plan. The walls are 2700.\n- one two three four five"));
        Assert.Equal(
            "<ol><li><span><b>First room</b></span></li><li><span><b>Second room</b></span></li></ol>",
            Render("1. First room\n2. Second room"));
        Assert.Equal(
            "<ul><li><span><b>Walls</b></span><ul><li><span><b>North</b></span></li><li><span><b>South</b></span></li></ul></li><li><span><b>Roof</b></span></li></ul>",
            Render("- Walls\n  - North\n    - South\n- Roof"));
        Assert.Equal(
            "<ol><li><span><b>Walls</b></span><ul><li><span><b>North</b></span></li></ul></li></ol>",
            Render("1. Walls\n   - North"));
        Assert.Equal(
            "<ul><li><span><b>One</b></span></li><li><span><b>Two</b></span></li></ul>",
            Render("- One\n\n- Two"));
        Assert.Equal(
            "<ul><li><span><b>One</b></span></li></ul>Hello<ul><li><span><b>Two</b></span></li></ul>",
            Render("- One\nHello\n- Two"));
        Assert.Equal("Rooms:<ul><li><span><b>Kitchen</b>. 12 m²</span></li></ul>", Render("Rooms:\n- Kitchen. 12 m²"));
        Assert.Equal("<ul><li><span><b>2 &lt; 3</b></span></li></ul>", Render("- 2 < 3"));
        Assert.Equal("Hello", Render("Hello"));
        Assert.DoesNotContain("<ul>", Render("Hello"));
        Assert.Contains("node.innerHTML = Forsk.answerHtml", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js")));

        var css = Html();
        Assert.Contains(".answer ul { list-style: disc;", css);
        Assert.Contains(".answer ol { list-style: decimal;", css);
        Assert.Contains(".answer li::marker { color: var(--meta); }", css);
        Assert.Contains(".answer li > span { color: var(--ink); }", css);
        Assert.Contains("padding: 0 0 0 1.2em", css);
    }

    /// <summary>
    /// An empty composer used to wash the send button white. It stays brand
    /// blue. Disabled is only the cursor and a slight opacity.
    /// </summary>
    [Fact]
    public void TheSendButton_StaysBrandWhenEmpty()
    {
        var html = Html();
        var at = html.IndexOf(".send:disabled", StringComparison.Ordinal);
        var end = html.IndexOf('}', at);
        var rule = html.Substring(at, end - at);

        Assert.Contains("background: var(--brand)", rule);
        Assert.Contains("color: #FFFFFF", rule);
        Assert.Contains("opacity: 0.85", rule);
        Assert.Contains("cursor: default", rule);
        Assert.DoesNotContain("rgba(255, 255, 255", rule);
        Assert.DoesNotContain("var(--ink)", rule);
        Assert.Contains("id=\"send\"", html);
        Assert.Contains("class=\"send\"", html);
    }
}
