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
        // The history button is gone (Julian, 2026-10-05). Its spot holds the view picker (Julian, 2026-10-07).
        Assert.DoesNotContain("id=\"history\"", html);
        Assert.Contains("id=\"view\"", header);
        Assert.Contains("id=\"view-menu\"", header);
        Assert.True(header.IndexOf("id=\"view\"", StringComparison.Ordinal) < header.IndexOf("id=\"role-pill\"", StringComparison.Ordinal));
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
        // Lucide's settings icon (1.52), at Lucide's stroke.
        Assert.Contains("stroke-width=\"2\"", button);
        Assert.Contains("M9.671 4.136", button);
        Assert.DoesNotContain("M19.4 15", button);
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
        // The one picture in the page is the logo the Project info card previews, never a face.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"el\('img'"));
        Assert.Contains("el('img', 'logo-preview')", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"\.src\b"));

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
    /// symbol: 24 units, currentColor, Lucide's 2 stroke, no ids, so a copy needs no
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
            Assert.Contains("stroke-width=\"2\"", svg);
            Assert.Contains("stroke-linecap=\"round\"", svg);
            Assert.Equal(1, svg.Split(" id=").Length - 1);
        }
        var holders = html.IndexOf("<div id=\"icons\" hidden>", StringComparison.Ordinal);
        Assert.True(holders > 0 && holders < html.IndexOf("</header>", StringComparison.Ordinal));
        Assert.Contains("}, p.icon);", script);
        Assert.Contains(".pill .icon {", html);
    }

    /// <summary>
    /// Every icon in the window is Lucide (or a door or window plan symbol
    /// drawn on Lucide's grid): 24 units, currentColor, stroke 2, round caps.
    /// No text glyph stands in for an icon. The crew avatars are brand art.
    /// </summary>
    [Fact]
    public void EveryIcon_IsLucide_AndNoGlyphStandsInForOne()
    {
        var html = Html();
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));

        var svgs = 0;
        for (var at = html.IndexOf("<svg", StringComparison.Ordinal); at >= 0; at = html.IndexOf("<svg", at + 1, StringComparison.Ordinal))
        {
            var svg = html.Substring(at, html.IndexOf("</svg>", at, StringComparison.Ordinal) - at);
            Assert.Contains("viewBox=\"0 0 24 24\"", svg);
            Assert.Contains("stroke=\"currentColor\"", svg);
            Assert.Contains("stroke-width=\"2\"", svg);
            Assert.Contains("stroke-linecap=\"round\"", svg);
            svgs++;
        }
        Assert.True(svgs >= 18, svgs.ToString());

        foreach (var name in new[] { "check", "x", "minus", "arrow-up", "arrow-down", "ellipsis" })
            Assert.Contains("<div id=\"icon-" + name + "\"><svg", html);
        Assert.Contains("M9.671 4.136", html); // settings
        Assert.Contains("m9 18 6-6-6-6", html); // chevron-right
        Assert.Contains("M20 4v7a4 4 0 0 1-4 4H4", html); // corner-down-left

        Assert.DoesNotContain("&#x23CE;", html);
        Assert.DoesNotContain("&#8593;", html);
        Assert.DoesNotContain(">+</button>", html);
        Assert.DoesNotContain("'\u2713'", script);
        Assert.DoesNotContain("'\u2717'", script);
        Assert.DoesNotContain("'\\u2191'", script);
        Assert.DoesNotContain("'\\u2193'", script);
        Assert.DoesNotContain("createElementNS", script);
    }

    [Fact]
    public void TheHeaderFace_MatchesTheTurn_ThenTheLastRole_ThenAPick()
    {
        var engine = PageScript.Load();
        string Eval(string js) => engine.Evaluate(js).ToString();

        // A message's role wins over a pick, so the header matches that avatar.
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'render'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'analyser'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("analyser", Eval("Forsk.shownRole({role:{value:'auto'}, thread:[{mark:'Modeller'},{mark:'Analyser'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'support'}, thread:[{mark:'Planner'}]})"));
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
        // The turn running now: its role shows at once, before any reply, over the last answer and the override.
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'auto'}, turn:'Modeller', thread:[{mark:'Planner'}]})"));
        Assert.Equal("plotter", Eval("Forsk.shownRole({role:{value:'auto'}, turn:'Plotter', thread:[]})"));
        Assert.Equal("analyser", Eval("Forsk.shownRole({role:{value:'modeller'}, turn:'Analyser'})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'auto'}, turn:'Planner', shown:'Modeller', thread:[{mark:'Modeller'}]})"));
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'auto'}, busy:{kind:'step', text:'Generating', mark:'Modeller'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'planner'}, busy:{kind:'thinking', mark:'Modeller'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'auto'}, busy:{kind:'step', mark:'Daylight'}, thread:[{mark:'Planner'}]})"));
        Assert.Equal("planner", Eval("Forsk.shownRole({role:{value:'auto'}, busy:{kind:'step'}, thread:[{mark:'Planner'}]})"));
        // Idle keeps the role the turn took. It does not snap back to the pick or to an older message.
        Assert.Equal("modeller", Eval("Forsk.shownRole({role:{value:'planner'}, shown:'Modeller', thread:[{mark:'Planner'}]})"));
        Assert.Equal("analyser", Eval("Forsk.shownRole({role:{value:'render'}, shown:'Analyser', thread:[]})"));
        Assert.Equal("render", Eval("Forsk.shownRole({role:{value:'render'}, thread:[]})"));
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
    /// at 14px. The icons sit on their own row, and the auto-grow uses that
    /// same line height rather than the 32px buttons.
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

        Assert.Contains("font-size: 14px", rule);
        Assert.Contains("line-height: 1.4", rule);
        Assert.Contains("height: calc(14px * 1.4)", rule);
        Assert.Contains("max-height: calc(14px * 1.4 * 5)", rule);
        Assert.Contains("padding: 0", rule);
        Assert.DoesNotContain("line-height: 32px", rule);
        Assert.DoesNotContain("32px * 5", rule);

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
        Assert.Contains("color: var(--on-brand)", rule);
        Assert.Contains("opacity: 0.85", rule);
        Assert.Contains("cursor: default", rule);
        Assert.DoesNotContain("rgba(255, 255, 255", rule);
        Assert.DoesNotContain("var(--ink)", rule);
        Assert.Contains("id=\"send\"", html);
        Assert.Contains("class=\"send\"", html);
    }

    /// <summary>
    /// A layer name and a file path have no spaces. The bubble is a flex child,
    /// so it needs min-width 0 or the word paints past the panel. Card chips wrap.
    /// A bar chip keeps one line until it is the only chip and wider than the
    /// row. The header column cannot grow the page.
    /// </summary>
    [Fact]
    public void UnbreakableText_WrapsInsideThePanel()
    {
        var html = Html();
        Assert.Contains("overflow-wrap: anywhere", html);
        var bubbleAt = html.IndexOf(".bubble {", StringComparison.Ordinal);
        var bubble = html.Substring(bubbleAt, html.IndexOf('}', bubbleAt) - bubbleAt);
        Assert.Contains("min-width: 0", bubble);
        Assert.Contains("overflow-wrap: anywhere", bubble);
        var pillAt = html.IndexOf(".pill {", StringComparison.Ordinal);
        var pill = html.Substring(pillAt, html.IndexOf('}', pillAt) - pillAt);
        Assert.Contains("max-width: 100%", pill);
        Assert.Contains("overflow-wrap: anywhere", pill);
        Assert.DoesNotContain("white-space: nowrap", pill);
        var slotAt = html.IndexOf(".slot {", StringComparison.Ordinal);
        var slot = html.Substring(slotAt, html.IndexOf('}', slotAt) - slotAt);
        Assert.Contains("white-space: nowrap", slot);
        Assert.Contains("minmax(0, 1fr)", html);
        Assert.Contains("max-width: 100%", html.Substring(html.IndexOf(".role-sub {", StringComparison.Ordinal), 220));
    }

    /// <summary>
    /// The reader stays on the newest line until they scroll away. A card's
    /// field takes focus without pulling that line off the screen. WebKit only
    /// tabs to a button that has tabindex. The field has no ring. A button's
    /// ring is 1.5px, and only after Tab sets data-kbd. Secondary text is
    /// #66707A, 4.66:1 on #F5F6F8.
    /// </summary>
    [Fact]
    public void TheNewestLine_StaysPut_AndButtonsTakeTheKeyboard()
    {
        var html = Html();
        Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("-webkit-tap-highlight-color: transparent", html);
        Assert.Contains("button:focus, textarea:focus, input:focus, select:focus { outline: none; }", html);
        Assert.Contains("html[data-kbd] button:focus-visible { outline: 1.5px solid var(--focus); outline-offset: 2px; }", html);
        Assert.Contains("html[data-kbd] .menu button:focus-visible", html);
        Assert.DoesNotContain("--glow", html);
        Assert.DoesNotContain(".field:focus-within", html);
        Assert.DoesNotContain("outline: 2px solid var(--brand)", html);
        Assert.DoesNotContain("outline: 3px", html);
        Assert.DoesNotContain("0 0 0 2px var(--brand)", html);
        Assert.Contains("--meta: #66707A", html);
        Assert.Contains("-webkit-user-select: none; user-select: none;", html);

        var engine = PageScript.Load();
        Assert.Equal("true", engine.Evaluate("String(Forsk.nearEnd(100, 80, 20))").ToString());
        Assert.Equal("true", engine.Evaluate("String(Forsk.nearEnd(100, 41, 20))").ToString());
        Assert.Equal("false", engine.Evaluate("String(Forsk.nearEnd(100, 40, 20))").ToString());
        Assert.Equal("false", engine.Evaluate("String(Forsk.nearEnd(1000, 0, 100))").ToString());
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("var follow = followLatest || Forsk.nearEnd(thread.scrollHeight, thread.scrollTop, thread.clientHeight);", script);
        Assert.Contains("else followLatest = Forsk.nearEnd(thread.scrollHeight, thread.scrollTop, thread.clientHeight);", script);
        Assert.Contains("root.addEventListener('resize'", script);
        Assert.Contains("focus.focus({ preventScroll: true })", script);
        Assert.Contains("node.tabIndex = 0", script);
        Assert.Contains("if (e.key === 'Tab') document.documentElement.setAttribute('data-kbd', '')", script);
        Assert.Contains("document.documentElement.removeAttribute('data-kbd')", script);
    }

    /// <summary>
    /// A suggestion chip never shrinks to a fragment. The primary stays, and
    /// wraps to two lines only when it is wider than the row. The rest move
    /// into the ⋯ menu. The reason wraps to two lines instead of an ellipsis.
    /// </summary>
    [Fact]
    public void AChipThatDoesNotFit_MovesIntoTheMenu()
    {
        var html = Html();
        var slotAt = html.IndexOf(".slot {", StringComparison.Ordinal);
        var slot = html.Substring(slotAt, html.IndexOf('}', slotAt) - slotAt);
        Assert.Contains("flex: none", slot);
        Assert.Contains("white-space: nowrap", slot);
        Assert.DoesNotContain("text-overflow", slot);
        Assert.DoesNotContain("min-width: 0", slot);
        Assert.Contains("-webkit-line-clamp: 2", html);
        Assert.DoesNotContain("nth-child(3) { display: none", html);

        var reasonAt = html.IndexOf(".reason {", StringComparison.Ordinal);
        var reason = html.Substring(reasonAt, html.IndexOf('}', reasonAt) - reasonAt);
        Assert.Contains("-webkit-line-clamp: 2", reason);
        Assert.DoesNotContain("text-overflow", reason);
        Assert.DoesNotContain("white-space: nowrap", reason);

        var engine = PageScript.Load();
        Assert.Equal("0", engine.Evaluate("String(Forsk.visibleSlots(400, [], 30, 6))").ToString());
        Assert.Equal("1", engine.Evaluate("String(Forsk.visibleSlots(320, [209, 97], 30, 6))").ToString());
        Assert.Equal("1", engine.Evaluate("String(Forsk.visibleSlots(200, [400, 80], 30, 6))").ToString());
        Assert.Equal("2", engine.Evaluate("String(Forsk.visibleSlots(420, [209, 97, 106], 30, 6))").ToString());
        Assert.Equal("3", engine.Evaluate("String(Forsk.visibleSlots(800, [209, 97, 106], 30, 6))").ToString());
        Assert.Equal("2", engine.Evaluate("String(Forsk.visibleSlots(300, [200, 58], 30, 6))").ToString());
        Assert.Equal("1", engine.Evaluate("String(Forsk.visibleSlots(300, [200, 59], 30, 6))").ToString());

        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("Forsk.visibleSlots(row.clientWidth, widths, helpW, gap)", script);
        Assert.Contains("fitSlots();", script);
        Assert.Contains("root.requestAnimationFrame(fitSlots)", script);
    }

    /// <summary>
    /// The field is a column. The line uses the whole width and grows upward.
    /// Import stays on the left of the row under it. Return and send stay on the right.
    /// </summary>
    [Fact]
    public void TheComposer_PinsTheIconsUnderTheLine()
    {
        var html = Html();
        var formAt = html.IndexOf("<form id=\"composer\"", StringComparison.Ordinal);
        var composer = html.Substring(formAt, html.IndexOf("</form>", formAt, StringComparison.Ordinal) - formAt);
        var textAt = composer.IndexOf("<textarea", StringComparison.Ordinal);
        var toolsAt = composer.IndexOf("class=\"tools\"", StringComparison.Ordinal);
        Assert.True(textAt >= 0 && toolsAt > textAt);
        var tools = composer.Substring(toolsAt);
        var addAt = tools.IndexOf("id=\"add\"", StringComparison.Ordinal);
        var hintAt = tools.IndexOf("class=\"key-hint\"", StringComparison.Ordinal);
        var sendAt = tools.IndexOf("id=\"send\"", StringComparison.Ordinal);
        Assert.True(addAt >= 0 && hintAt > addAt && sendAt > hintAt);

        var fieldAt = html.IndexOf(".field {", StringComparison.Ordinal);
        var field = html.Substring(fieldAt, html.IndexOf('}', fieldAt) - fieldAt);
        Assert.Contains("flex-direction: column", field);
        var lineAt = html.IndexOf("#q {", StringComparison.Ordinal);
        var line = html.Substring(lineAt, html.IndexOf('}', lineAt) - lineAt);
        Assert.Contains("width: 100%", line);
        Assert.Contains("flex: none", line);
        var hint = html.Substring(html.IndexOf(".key-hint {", StringComparison.Ordinal), 80);
        Assert.Contains("margin-left: auto", hint);
    }

    /// <summary>
    /// The message list draws a 6px thumb over the text. The native bar is
    /// zero-width, so the lines do not shift when the thumb fades in. The
    /// pin and the composer keep their own bars. The geometry is headless.
    /// </summary>
    [Fact]
    public void TheMessageList_DrawsAThinThumb_ThatDoesNotReserveAGutter()
    {
        var html = Html();
        var frameAt = html.IndexOf("<div id=\"thread-frame\">", StringComparison.Ordinal);
        var dockAt = html.IndexOf("<div class=\"dock\">", frameAt, StringComparison.Ordinal);
        Assert.True(frameAt >= 0 && dockAt > frameAt);
        var frame = html.Substring(frameAt, dockAt - frameAt);
        Assert.Contains("<main id=\"thread\"", frame);
        Assert.Contains("id=\"thread-bar\" hidden", frame);
        Assert.Contains("id=\"thread-thumb\"", frame);
        Assert.Contains("aria-hidden=\"true\"", frame);

        Assert.Contains("padding: 8px 20px 12px", html);
        // The message list and the ⋯ sheet hide the native bar; nothing else does.
        Assert.Equal(2, html.Split("scrollbar-width: none").Length - 1);
        var sheetAt = html.IndexOf("#sheet-body {", StringComparison.Ordinal);
        var sheetCss = html.Substring(sheetAt, html.IndexOf('}', sheetAt) - sheetAt);
        Assert.Contains("overflow-y: auto", sheetCss);
        Assert.Contains("scrollbar-width: none", sheetCss);
        Assert.DoesNotContain("scrollbar-gutter", html);
        var native = html.Substring(html.IndexOf("#thread::-webkit-scrollbar", StringComparison.Ordinal), 80);
        Assert.Contains("width: 0", native);
        var ink = html.Substring(html.IndexOf("#thread-thumb::before", StringComparison.Ordinal), 220);
        Assert.Contains("width: 6px", ink);
        Assert.Contains("border-radius: 3px", ink);
        Assert.DoesNotContain("scrollerStyle", html);
        Assert.DoesNotContain("NSWindow", html);
        Assert.DoesNotContain("NSScrollView", html);

        var pinAt = html.IndexOf("#pin {", StringComparison.Ordinal);
        var pin = html.Substring(pinAt, html.IndexOf("#pin .card", pinAt, StringComparison.Ordinal) - pinAt);
        Assert.Contains("overflow-y: auto", pin);
        Assert.DoesNotContain("scrollbar-width", pin);
        var fieldAt = html.IndexOf("\ntextarea {", StringComparison.Ordinal);
        var field = html.Substring(fieldAt, html.IndexOf("#q {", fieldAt, StringComparison.Ordinal) - fieldAt);
        Assert.Contains("overflow-y: auto", field);
        Assert.DoesNotContain("scrollbar-width", field);
        Assert.Contains("button:focus, textarea:focus, input:focus, select:focus { outline: none; }", html);
        Assert.Contains("html[data-kbd] button:focus-visible { outline: 1.5px solid var(--focus); outline-offset: 2px; }", html);

        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("Forsk.scrollThumb = function", script);
        Assert.Contains("Forsk.scrollForThumb = function", script);
        Assert.Contains("syncThreadThumb();", script);
        Assert.Contains("revealThreadThumb();", script);
        Assert.Contains("thread.scrollTop = thread.scrollHeight", script);
        Assert.Contains("thumb.addEventListener('mousedown'", script);
        Assert.Contains("thumb.addEventListener('wheel'", script);
        Assert.Contains("overlayThumb('sheet', 'sheet-body', 'sheet-bar', 'sheet-thumb')", script);
        Assert.DoesNotContain("scrollerStyle", script);
        Assert.DoesNotContain("NSWindow", script);

        var engine = PageScript.Load();
        Assert.Equal("null", engine.Evaluate("JSON.stringify(Forsk.scrollThumb(200, 200, 0))").ToString());
        Assert.Equal("null", engine.Evaluate("JSON.stringify(Forsk.scrollThumb(300, 300.4, 0))").ToString());
        Assert.Equal("null", engine.Evaluate("JSON.stringify(Forsk.scrollThumb(0, 400, 0))").ToString());
        Assert.Equal("null", engine.Evaluate("JSON.stringify(Forsk.scrollThumb(NaN, 400, 0))").ToString());
        Assert.Equal("40", engine.Evaluate("String(Forsk.scrollThumb(200, 1000, 400).h)").ToString());
        Assert.Equal("80", engine.Evaluate("String(Forsk.scrollThumb(200, 1000, 400).y)").ToString());
        Assert.Equal("0", engine.Evaluate("String(Forsk.scrollThumb(200, 1000, -20).y)").ToString());
        Assert.Equal("160", engine.Evaluate("String(Forsk.scrollThumb(200, 1000, 5000).y)").ToString());
        Assert.Equal("24", engine.Evaluate("String(Forsk.scrollThumb(200, 10000, 0).h)").ToString());
        Assert.Equal("176", engine.Evaluate("String(Forsk.scrollThumb(200, 10000, 9800).y)").ToString());
        Assert.Equal("24", engine.Evaluate("String(Forsk.scrollThumb(180, 9000, 4410, 170, 24).h)").ToString());
        Assert.Equal("73", engine.Evaluate("String(Forsk.scrollThumb(180, 9000, 4410, 170, 24).y)").ToString());
        Assert.Equal("4410", engine.Evaluate("String(Forsk.scrollForThumb(180, 9000, 73, 170, 24))").ToString());
        Assert.Equal("400", engine.Evaluate("String(Forsk.scrollForThumb(200, 1000, 80))").ToString());
        Assert.Equal("0", engine.Evaluate("String(Forsk.scrollForThumb(200, 1000, -10))").ToString());
        Assert.Equal("800", engine.Evaluate("String(Forsk.scrollForThumb(200, 1000, 999))").ToString());
        Assert.Equal("0", engine.Evaluate("String(Forsk.scrollForThumb(200, 180, 10))").ToString());
    }

    /// <summary>A secret field (the Grok API key) is a password input: the pasted key shows as dots and is not autofilled.</summary>
    [Fact]
    public void ASecretField_IsAPasswordInput()
    {
        var engine = PageScript.Load();
        Assert.Equal("password", engine.Evaluate("Forsk.textType({key:'key', secret:true, value:''})").ToString());
        Assert.Equal("text", engine.Evaluate("Forsk.textType({value:'1200', unit:'mm'})").ToString());
        Assert.Equal("text", engine.Evaluate("Forsk.fieldKind({secret:true, value:''})").ToString());
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("input.type = Forsk.textType(field);", script);
        Assert.Contains("if (field.secret) input.autocomplete = 'off';", script);
    }
}
