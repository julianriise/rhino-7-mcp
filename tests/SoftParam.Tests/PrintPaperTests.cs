using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>Paper sizes A4 to A1: the table, the stored default, and the title block's width on each.</summary>
public class PrintPaperTests
{
    [Theory]
    [InlineData("A4", 297, 210, 182)]
    [InlineData("a3", 420, 297, 280)]
    [InlineData("A 2", 594, 420, 280)]
    [InlineData("A1", 841, 594, 280)]
    public void EachPaper_HasItsSize_AndATitleBlockThatFits(string name, double w, double h, double title)
    {
        var paper = PrintTemplate.Find(name);
        Assert.NotNull(paper);
        Assert.Equal(w, paper.WidthMm);
        Assert.Equal(h, paper.HeightMm);
        Assert.Equal(title, PrintTemplate.TitleBlockWidthMm(paper, 10.0), 6);
    }

    [Fact]
    public void NoneStored_IsA3_AndAnUnknownNameIsNull()
    {
        Assert.Equal("A3", PrintTemplate.Stored(null).Name);
        Assert.Equal("A3", PrintTemplate.Stored("B5").Name);
        Assert.Null(PrintTemplate.Find("Letter"));
        Assert.Equal("Unknown paper. Use A4, A3, A2 or A1.", PrintTemplate.UnknownPaper);
    }
}
