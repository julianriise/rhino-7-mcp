using Jint;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The Forsk window's page script, run headless in Jint. No DOM: the script only boots its DOM half in a browser.</summary>
static class PageScript
{
    public static Engine Load()
    {
        var engine = new Engine();
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js")));
        engine.Execute(@"
            var sent = [];
            function press(k, mods, card, text, composer, cancel) {
              var m = {};
              (mods || '').split('+').forEach(function (x) { if (x) m[x] = true; });
              var e = { key: k, keyCode: m.ime ? 229 : 0, isComposing: !!m.composing,
                        shiftKey: !!m.shift, metaKey: !!m.meta, ctrlKey: !!m.ctrl, altKey: !!m.alt, prevented: false };
              e.preventDefault = function () { e.prevented = true; };
              e.stopPropagation = function () {};
              var before = sent.length;
              var state = { composer: composer !== false, card: card || null, text: text == null ? 'hello' : text, cancel: !!cancel };
              var a = Forsk.handleKey(e, state, function (x) { sent.push(x); });
              return JSON.stringify({ kind: a ? a.kind : null, prevented: e.prevented, sent: sent.length - before,
                                      last: sent.length > before ? sent[sent.length - 1] : null });
            }");
        return engine;
    }

    public static JObject Press(Engine engine, string key, string mods = "", string card = null, string text = null, bool composer = true, bool cancel = false)
    {
        var js = "press(" + Quote(key) + "," + Quote(mods) + "," + (card == null ? "null" : Quote(card)) + ","
            + (text == null ? "null" : Quote(text)) + "," + (composer ? "true" : "false") + "," + (cancel ? "true" : "false") + ")";
        return JObject.Parse(engine.Evaluate(js).AsString());
    }

    public static string Quote(string s)
    {
        return Newtonsoft.Json.JsonConvert.ToString(s);
    }
}

/// <summary>
/// D0: the keyboard contract, headless. Enter sends, Shift+Enter breaks the
/// line, Cmd+1..4 and Cmd+/ are wired, Esc closes a card. A mapped key is
/// eaten in the page and becomes one Forsk action, so it never reaches Rhino.
/// Every other key, æ ø å and a dead key included, is left to the field.
/// </summary>
public class PageKeyboardTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    public void CmdDigit_FiresThatSlot(string key, int slot)
    {
        var engine = PageScript.Load();
        var r = PageScript.Press(engine, key, "meta");
        Assert.Equal("slot", r["kind"]!.ToString());
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal(slot, r["last"]!["slot"]!.Value<int>());
    }

    /// <summary>A press outside the open ⋯ sheet closes it. Inside it, on ⋯ or in the ⋯ menu it stays.</summary>
    [Fact]
    public void APressOutsideTheSheet_ClosesIt()
    {
        var engine = PageScript.Load();
        Assert.Equal("true", engine.Evaluate("String(Forsk.closesSheet(false, false, false))").ToString());
        Assert.Equal("false", engine.Evaluate("String(Forsk.closesSheet(true, false, false))").ToString());
        Assert.Equal("false", engine.Evaluate("String(Forsk.closesSheet(false, true, false))").ToString());
        Assert.Equal("false", engine.Evaluate("String(Forsk.closesSheet(false, false, true))").ToString());
    }

    [Theory]
    [InlineData("meta")]
    [InlineData("meta+shift")] // Slash is Shift+7 on a Norwegian keyboard.
    public void CmdSlash_OpensWhatCanIDoHere(string mods)
    {
        var r = PageScript.Press(PageScript.Load(), "/", mods);
        Assert.Equal("help", r["kind"]!.ToString());
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal(1, r["sent"]!.Value<int>());
    }

    [Fact]
    public void Enter_SendsTheComposerText()
    {
        var r = PageScript.Press(PageScript.Load(), "Enter", "", text: "  skriv ut  ");
        Assert.Equal("send", r["kind"]!.ToString());
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal("skriv ut", r["last"]!["text"]!.ToString());
    }

    [Fact]
    public void Enter_OnAnEmptyComposer_IsEatenAndSendsNothing()
    {
        var r = PageScript.Press(PageScript.Load(), "Enter", "", text: "   ");
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal(0, r["sent"]!.Value<int>());
    }

    [Theory]
    [InlineData("Enter", "shift")]     // Shift+Enter: the field breaks the line.
    [InlineData("Enter", "composing")] // Enter that confirms an IME or dead-key composition.
    [InlineData("Enter", "ime")]
    [InlineData("Enter", "alt")]
    [InlineData("Enter", "meta")]
    [InlineData("æ", "")]
    [InlineData("ø", "")]
    [InlineData("å", "")]
    [InlineData("Å", "shift")]
    [InlineData("Dead", "")]           // The first half of a dead key: ¨ then u.
    [InlineData("u", "composing")]     // Its second half.
    [InlineData("a", "meta")]          // Select all, copy, paste, cut and undo stay the field's.
    [InlineData("c", "meta")]
    [InlineData("v", "meta")]
    [InlineData("x", "meta")]
    [InlineData("z", "meta")]
    [InlineData("1", "ctrl")]
    [InlineData("1", "meta+alt")]
    [InlineData("1", "meta+shift")]
    [InlineData("Escape", "")]         // No card open: Esc is not ours.
    public void OtherKeys_AreLeftToTheField(string key, string mods)
    {
        var r = PageScript.Press(PageScript.Load(), key, mods);
        Assert.True(r["kind"]!.Type == JTokenType.Null, key + " " + mods + " was taken: " + r);
        Assert.False(r["prevented"]!.Value<bool>());
        Assert.Equal(0, r["sent"]!.Value<int>());
    }

    [Fact]
    public void Escape_ClosesTheOpenCard()
    {
        var r = PageScript.Press(PageScript.Load(), "Escape", "", card: "m4");
        Assert.Equal("card.close", r["kind"]!.ToString());
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal("m4", r["last"]!["card"]!.ToString());
    }

    [Fact]
    public void Escape_OnAForm_SendsCancel()
    {
        var r = PageScript.Press(PageScript.Load(), "Escape", "", card: "m4", cancel: true);
        Assert.Equal("card", r["kind"]!.ToString());
        Assert.True(r["prevented"]!.Value<bool>());
        Assert.Equal("m4", r["last"]!["card"]!.ToString());
        Assert.Equal("cancel", r["last"]!["pill"]!.ToString());
    }

    [Fact]
    public void AForm_IsAnOpenCardThatAsksForAValue()
    {
        var engine = PageScript.Load();
        string Eval(string js) => engine.Evaluate(js).ToString();

        Assert.Equal("true", Eval("String(Forsk.isForm({role:'card', state:'open', fields:[{key:'project', value:''}]}))"));
        Assert.Equal("true", Eval("String(Forsk.isForm({role:'card', state:'open', fields:[{key:'scale', options:['Fit']}]}))"));
        Assert.Equal("true", Eval("String(Forsk.isForm({role:'card', state:'open', fields:[{key:'description', long:true}]}))"));
        Assert.Equal("false", Eval("String(Forsk.isForm({role:'card', state:'open', fields:[{key:'front', check:true, value:'1'}]}))"));
        Assert.Equal("false", Eval("String(Forsk.isForm({role:'card', state:'answered', answer:'Cancel', fields:[{key:'project'}]}))"));
        Assert.Equal("Project info saved · Test house, 2026-07",
            Eval("Forsk.cardLine({state:'answered', question:'Project info for the title blocks.', answer:'Save', receipt:'Project info saved · Test house, 2026-07'})"));
        Assert.Equal("Which door type? · Sliding door",
            Eval("Forsk.cardLine({state:'answered', question:'Which door type?', answer:'Sliding door'})"));
        Assert.Equal("Stair sizes. The steps stay equal: their count follows the height.",
            Eval("Forsk.cardLine({state:'stale', question:'Stair sizes. The steps stay equal: their count follows the height.', answer:'Save'})"));
        Assert.Equal("false", Eval("String(Forsk.isForm({role:'card', state:'open', pills:[{id:'save'},{id:'cancel'}]}))"));
        Assert.Equal("false", Eval("String(Forsk.isForm({role:'receipt', ok:true, text:'Printed'}))"));
        Assert.Equal("true", Eval("String(Forsk.cardCancels({thread:[{id:'m4', pills:[{id:'save'},{id:'cancel'}]}]}, 'm4'))"));
        Assert.Equal("false", Eval("String(Forsk.cardCancels({thread:[{id:'m4', pills:[{id:'save'}]}]}, 'm4'))"));
    }

    [Fact]
    public void Enter_OutsideTheComposer_IsLeftToThePage()
    {
        // A focused pill takes Enter as a click. The composer does not send.
        var r = PageScript.Press(PageScript.Load(), "Enter", "", composer: false);
        Assert.True(r["kind"]!.Type == JTokenType.Null);
        Assert.False(r["prevented"]!.Value<bool>());
    }

    [Fact]
    public void EveryShortcut_IsAForskAction_NeverAKeyForRhino()
    {
        var engine = PageScript.Load();
        foreach (var key in new[] { "1", "2", "3", "/", "Enter", "Escape", "a", "z", "c", "v", "x", "æ", "Dead", "F1", "Tab" })
            foreach (var mods in new[] { "", "meta", "meta+shift", "ctrl", "alt", "shift", "composing" })
                PageScript.Press(engine, key, mods, card: "m1");
        var kinds = engine.Evaluate("JSON.stringify(sent.map(function (a) { return a.kind; }))").AsString();
        var sent = JArray.Parse(kinds).Select(k => k.ToString()).ToList();
        Assert.NotEmpty(sent);
        Assert.All(sent, kind => Assert.Contains(kind, new[] { "send", "slot", "help", "card.close" }));
    }

    [Fact]
    public void ThePage_DoesNotMoveTheKeyboardWhenThePointerEnters()
    {
        var engine = PageScript.Load();
        Assert.Equal("undefined", engine.Evaluate("typeof Forsk.hoverAction").AsString());
        Assert.Equal("undefined", engine.Evaluate("typeof Forsk.hover").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Forsk.keyAction").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Forsk.focus").AsString());
    }
}
