using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The user's notes layer: A-NOTE and the layers under it, exported with the sheet's text.</summary>
public class PlanNotesTests
{
    [Theory]
    [InlineData("A-NOTE", true)]
    [InlineData("a-note", true)]
    [InlineData("A-NOTE::Dims", true)]
    [InlineData("A-NOTES", false)]
    [InlineData("Notes", false)]
    [InlineData("S-DRAW::Plan", false)]
    [InlineData("", false)]
    public void TheNotesLayer_IsANoteAndItsSublayers(string path, bool notes)
    {
        Assert.Equal(notes, PlanNotes.IsNoteLayer(path));
    }

    [Fact]
    public void ANotesCopy_ExportsWithTheSheetsText()
    {
        Assert.Equal("A-ANNO-TEXT", SheetFlat.LayerFor(PlanNotes.Role));
    }
}
