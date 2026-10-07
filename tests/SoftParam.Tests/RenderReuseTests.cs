using Xunit;

namespace SoftParam.Tests;

/// <summary>Speed (2026-10-07): the page reuses an unchanged item's node, keyed by its JSON and count.</summary>
public class RenderReuseTests
{
    [Fact]
    public void EqualItems_GetTheirOwnKeys_AndAChangedItemANewOne()
    {
        var engine = PageScript.Load();
        Assert.Equal("true", engine.Evaluate("var a = Forsk.entryKeys([{role:'line',text:'x'},{role:'line',text:'x'}]); String(a[0] !== a[1])").ToString());
        Assert.Equal("true", engine.Evaluate("var b = Forsk.entryKeys([{role:'card',id:'c1',state:'open'}]); var c = Forsk.entryKeys([{role:'card',id:'c1',state:'answered'}]); String(b[0] !== c[0])").ToString());
        Assert.Equal("true", engine.Evaluate("var d = Forsk.entryKeys([{role:'user',text:'hi'}]); var e = Forsk.entryKeys([{role:'user',text:'hi'}]); String(d[0] === e[0])").ToString());
    }
}
