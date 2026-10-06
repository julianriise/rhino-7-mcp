using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// AG.1: the type catalog (forsk.types.v1) reads, adds up and fails closed
/// with a reason. Every opening type in the registry has one catalog type,
/// and a wall from before the catalog is Generic 200.
/// </summary>
public class TypeCatalogTests
{
    static string Json() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "types", "forsk.types.v1.json"));

    static TypeCatalog.Catalog Load()
    {
        var catalog = TypeCatalog.Parse(Json(), out var errors);
        Assert.True(catalog != null, string.Join("\n", errors));
        return catalog!;
    }

    static List<string> Errors(Action<JObject> change)
    {
        var root = JObject.Parse(Json());
        change(root);
        Assert.Null(TypeCatalog.Parse(root.ToString(), out var errors));
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void TheShippedCatalog_IsValid_WithTheStartingSet()
    {
        var catalog = Load();
        Assert.Equal(new[] { "wall.generic_200", "wall.timber_198_ext", "wall.timber_98_int", "wall.timber_148_int_lb", "wall.concrete_200" },
            catalog.Walls.Select(w => w.Id));
        Assert.Equal(new[] { "floor.slab_on_ground", "floor.timber_joist_220" }, catalog.Floors.Select(f => f.Id));
        Assert.Equal(new[] { "roof.flat_compact", "roof.shed_ventilated" }, catalog.Roofs.Select(r => r.Id));
    }

    [Theory]
    [InlineData("wall.generic_200", 200)]
    [InlineData("wall.timber_198_ext", 301)]
    [InlineData("wall.timber_98_int", 124)]
    [InlineData("wall.timber_148_int_lb", 174)]
    [InlineData("wall.concrete_200", 200)]
    public void WallTotals_AreTheirLayers(string id, double total)
    {
        var wall = Load().Wall(id);
        Assert.Equal(total, wall.Thickness);
        Assert.Equal(total, wall.Layers.Sum(l => l.Thickness));
        Assert.Equal("structure", wall.Core.Function);
    }

    [Fact]
    public void EveryBuildUp_ButGeneric_IsMarkedPlaceholder()
    {
        var catalog = Load();
        var all = catalog.Walls.Concat(catalog.Floors).Concat(catalog.Roofs).ToList();
        Assert.All(all.Where(t => t.Id != "wall.generic_200"), t => Assert.True(t.Placeholder, t.Id));
        Assert.False(catalog.Wall("wall.generic_200").Placeholder);
        Assert.All(catalog.Openings, o => Assert.True(o.Placeholder, o.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wall.unknown")]
    public void AWallFromBeforeTheCatalog_IsGeneric200_SoNothingMoves(string? raw)
    {
        var wall = Load().WallFor(raw);
        Assert.Equal("wall.generic_200", wall.Id);
        Assert.Equal(200, wall.Thickness);
        Assert.Equal(WallDraw.DefaultThicknessMm, wall.Thickness);
    }

    [Fact]
    public void AWallRecordWithAType_GetsThatType()
    {
        Assert.Equal(301, Load().WallFor(" wall.timber_198_ext ").Thickness);
    }

    [Fact]
    public void EveryRegistryOpeningType_MapsToOneCatalogType_WithTheSameKindAndTodaysSize()
    {
        var catalog = Load();
        Assert.Equal(OpeningTypes.All.Select(d => d.Id).OrderBy(i => i), catalog.Openings.Select(o => o.Id).OrderBy(i => i));
        foreach (var def in OpeningTypes.All)
        {
            var type = catalog.Opening(def.Id);
            Assert.Equal(def.Kind, type.Kind);
            // ForskTags' defaults: a door 900 × 2100 (sill 0), a window 1200 wide from 900 to 2100.
            Assert.Equal(def.Kind == "door" ? 900 : 1200, type.DefaultWidth);
            Assert.Equal(def.Kind == "door" ? 2100 : 1200, type.DefaultHeight);
        }
    }

    [Fact]
    public void AnotherSchema_IsRejected()
    {
        var errors = Errors(r => r["schema"] = "forsk.types.v2");
        Assert.Equal("Schema is forsk.types.v2, expected forsk.types.v1.", Assert.Single(errors));
    }

    [Fact]
    public void NotJson_IsRejected()
    {
        Assert.Null(TypeCatalog.Parse("{ walls: [", out var errors));
        Assert.StartsWith("Not JSON", Assert.Single(errors));
    }

    [Fact]
    public void ATotalThatDoesNotAddUp_IsRejected()
    {
        var errors = Errors(r => r["walls"]![1]!["thickness"] = 300);
        Assert.Contains("wall.timber_198_ext thickness is 300 mm but its layers add up to 301 mm.", errors);
    }

    [Fact]
    public void AnUnknownMaterial_IsRejected()
    {
        var errors = Errors(r => r["walls"]![2]!["layers"]![0]!["material"] = "marble");
        Assert.Contains("wall.timber_98_int layer 1 (Gypsum): material marble is not in the catalog.", errors);
    }

    [Fact]
    public void NoCoreOrTwoCores_IsRejected()
    {
        Assert.Contains("wall.concrete_200 has 0 core layers, expected 1.", Errors(r => r["walls"]![4]!["layers"]![0]!["core"] = false));
        Assert.Contains("wall.timber_98_int has 2 core layers, expected 1.", Errors(r => r["walls"]![2]!["layers"]![0]!["core"] = true));
        Assert.Contains("wall.timber_98_int core layer is finish, not structure.", Errors(r =>
        {
            r["walls"]![2]!["layers"]![0]!["core"] = true;
            r["walls"]![2]!["layers"]![1]!["core"] = false;
        }));
    }

    [Fact]
    public void AZeroLayerThatIsNotAMembrane_IsRejected()
    {
        var errors = Errors(r =>
        {
            r["walls"]![2]!["layers"]![0]!["thickness"] = 0;
            r["walls"]![2]!["thickness"] = 111;
        });
        Assert.Equal("wall.timber_98_int layer 1 (Gypsum): only a membrane may be 0 thick.", Assert.Single(errors));
    }

    [Fact]
    public void ADuplicateId_AWrongOperation_AndAMissingDefault_AreRejected()
    {
        Assert.Contains("walls: wall.generic_200 is listed twice.", Errors(r => r["walls"]![1]!["id"] = "wall.generic_200"));
        Assert.Contains("Opening window.fixed operation sliding is not a window operation.", Errors(r => r["openings"]![4]!["operation"] = "sliding"));
        Assert.Contains("Opening window.fixed is a window with no glass.", Errors(r => r["openings"]![4]!["glazing"] = "none"));
        Assert.Contains("defaults.wall wall.missing is not a wall type.", Errors(r => r["defaults"]!["wall"] = "wall.missing"));
    }
}
