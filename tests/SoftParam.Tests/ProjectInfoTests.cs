using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// v3 N1: the project info is one key list in ProjectInfo, read by the title
/// block, the PDF's Info, the CSV header and the IFC. The window's card and
/// the file facts read the same list; no copy of it is left.
/// </summary>
public class ProjectInfoTests
{
    /// <summary>The garage smoke's values, as the morning checklist types them.</summary>
    public static readonly Dictionary<string, string> Smoke = new()
    {
        ["project"] = "Garage",
        ["project_no"] = "2026-07",
        ["client"] = "Ola Nordmann",
        ["address"] = "Storgata 1, 0150 Oslo",
        ["architect"] = "Riise Arkitekter",
        ["date"] = "2026-10-04",
        ["revision"] = "B"
    };

    public static ProjectInfo.Record Read(Dictionary<string, string> values) =>
        ProjectInfo.Read(key => values.TryGetValue(key, out var v) ? v : null);

    [Fact]
    public void TheKeys_AreTheSevenInOrder_WithoutDuplicates()
    {
        Assert.Equal(new[] { "project", "project_no", "client", "address", "architect", "date", "revision" }, ProjectInfo.Keys);
        Assert.Equal(ProjectInfo.Keys.Count, ProjectInfo.Keys.Distinct().Count());
    }

    [Fact]
    public void Read_TakesEveryKey_Trimmed()
    {
        var record = Read(Smoke.ToDictionary(p => p.Key, p => "  " + p.Value + " "));
        foreach (var pair in Smoke)
            Assert.Equal(pair.Value, record[pair.Key]);
    }

    [Fact]
    public void Read_WithOnlyAProject_LeavesTheRestEmpty()
    {
        var record = Read(new() { ["project"] = "Garage" });
        Assert.Equal("Garage", record["project"]);
        foreach (var key in ProjectInfo.Keys.Skip(1))
            Assert.Equal("", record[key]);
    }

    [Fact]
    public void Missing_IsFalse_OnlyWithAProjectName()
    {
        Assert.True(ProjectInfo.Missing(Read(new())));
        Assert.True(ProjectInfo.Missing(Read(new() { ["project"] = "   " })));
        Assert.True(ProjectInfo.Missing(Read(Smoke.Where(p => p.Key != "project").ToDictionary(p => p.Key, p => p.Value))));
        Assert.False(ProjectInfo.Missing(Read(new() { ["project"] = "Garage" })));
        Assert.True(ProjectInfo.Missing(null));
    }

    [Fact]
    public void Captions_AreEnglish_AndBokmal()
    {
        Assert.Equal(new[] { "Project", "Project no.", "Client", "Address", "Architect", "Date", "Rev." },
            ProjectInfo.Keys.Select(k => ProjectInfo.Caption(k)));
        Assert.Equal(new[] { "Prosjekt", "Prosjektnr.", "Byggherre", "Adresse", "Arkitekt", "Dato", "Rev." },
            ProjectInfo.Keys.Select(k => ProjectInfo.Caption(k, true)));
    }

    [Fact]
    public void TheSheetDate_IsTheStoredOne_ElseThePrintDay()
    {
        var today = new DateTime(2026, 10, 5);
        Assert.Equal("2026-10-04", ProjectInfo.SheetDate(Read(Smoke), today));
        Assert.Equal("2026-10-05", ProjectInfo.SheetDate(Read(new() { ["project"] = "Garage" }), today));
    }

    [Fact]
    public void PdfInfo_HasTitleAuthorSubjectKeywordsAndCreator()
    {
        var pdf = ProjectInfo.PdfInfo(Read(Smoke), "A-00-001", "A-50-003");
        Assert.Equal("Garage — A-00-001–A-50-003", pdf.Title);
        Assert.Equal("Riise Arkitekter", pdf.Author);
        Assert.Equal("Storgata 1, 0150 Oslo", pdf.Subject);
        Assert.Equal("2026-07; Ola Nordmann; rev. B", pdf.Keywords);
        Assert.Equal("Forsk", pdf.Creator);
    }

    [Fact]
    public void PdfInfo_LeavesEmptyPartsOut()
    {
        var pdf = ProjectInfo.PdfInfo(Read(new() { ["project"] = "Garage", ["client"] = "Ola Nordmann" }));
        Assert.Equal("Garage drawings", pdf.Title);
        Assert.Equal("", pdf.Author);
        Assert.Equal("", pdf.Subject);
        Assert.Equal("Ola Nordmann", pdf.Keywords);
        Assert.Equal("Garage — A-20-001", ProjectInfo.PdfInfo(Read(new() { ["project"] = "Garage" }), "A-20-001", "A-20-001").Title);
    }

    [Fact]
    public void CsvHeader_IsOneCaptionAndValuePerFieldThatIsSet()
    {
        var rows = ProjectInfo.CsvHeader(Read(Smoke));
        Assert.Equal(new[] { "Project", "Project no.", "Client", "Address", "Architect", "Date", "Rev." }, rows.Select(r => r.Key));
        Assert.Equal("2026-07", rows[1].Value);
        Assert.Equal(new[] { "Project", "Architect" },
            ProjectInfo.CsvHeader(Read(new() { ["project"] = "Garage", ["architect"] = "Riise Arkitekter" })).Select(r => r.Key));
    }

    [Fact]
    public void IfcInfo_NamesTheProjectSiteAndBuilding()
    {
        var ifc = ProjectInfo.IfcInfo(Read(Smoke), "garage-file");
        Assert.Equal("2026-07", ifc.ProjectName);
        Assert.Equal("Garage", ifc.ProjectLongName);
        Assert.Equal("Storgata 1, 0150 Oslo", ifc.SiteName);
        Assert.Equal("Storgata 1, 0150 Oslo", ifc.Address);
        Assert.Equal("Garage", ifc.BuildingName);
        Assert.Equal(new[] { "Client", "Architect", "ProjectNumber", "Revision", "Date" }, ifc.Properties.Select(p => p.Key));

        var bare = ProjectInfo.IfcInfo(Read(new()), "garage-file");
        Assert.Equal("garage-file", bare.ProjectName);
        Assert.Equal("Site", bare.SiteName);
        Assert.Equal("", bare.Address);
        Assert.Empty(bare.Properties);
    }

    /// <summary>The copies of the key list that disagreed are gone: the card, the file facts, Print and the record read ProjectInfo.</summary>
    [Fact]
    public void NoOtherFile_HoldsItsOwnKeyList()
    {
        var plugin = PluginDir();
        string Source(string path) => File.ReadAllText(Path.Combine(plugin, path));
        var cards = Source("Forsk/ForskCards.cs");
        Assert.DoesNotContain("MetaKeys", cards);
        Assert.Contains("ProjectInfo.Keys", cards);
        var rows = Source("Functions/ForskFileRows.cs");
        Assert.DoesNotContain("\"client\", \"address\"", rows);
        Assert.Contains("ProjectInfo.Keys", rows);
        var chat = Source("Forsk/ForskChat.cs");
        Assert.DoesNotContain("MetaKeys", chat);
        Assert.DoesNotContain("KnownMeta", chat);
        var more = Source("Functions/LayoutPackMore.cs");
        Assert.DoesNotContain("MetaOrEmpty(doc, \"client\")", more);
        Assert.DoesNotContain("A date stored on the file is not used", more);
        var pack = Source("Functions/LayoutPack.cs");
        Assert.DoesNotContain("MaybeStoreMeta(doc, parameters, \"client\")", pack);
        Assert.DoesNotContain("[\"meta.client\"]", Source("Forsk/ForskText.cs"));
    }

    internal static string PluginDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin");
            if (Directory.Exists(Path.Combine(path, "Functions"))) return path;
        }
        throw new DirectoryNotFoundException("plugin above " + AppContext.BaseDirectory);
    }
}
