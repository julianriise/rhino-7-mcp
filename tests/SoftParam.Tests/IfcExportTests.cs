using GeometryGym.Ifc;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// R4: the garage as IFC4, headless. The records go in as Forsk keeps them
/// (each wall's path, thickness and height; each opening's host, centre,
/// width, sill, head, type and hand; the slab, the flat roof, the room) and
/// the file is read back with the same library. scripts/ifc_check.py then
/// checks /tmp/forsk-ifc-garage.ifc with ifcopenshell.
/// </summary>
public class IfcExportTests
{
    public const string GaragePath = "/tmp/forsk-ifc-garage.ifc";

    static List<List<Pt>> Box(double x0, double y0, double x1, double y1) =>
        new() { new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } };

    static IfcExport.Wall Wall(string id, List<List<Pt>> rings) =>
        new() { Id = id, Rings = rings, Thickness = 200, Height = 3000 };

    /// <summary>The garage smoke's garage: 8 × 4 m outside, 200 mm walls 3 m high, a door and a window in the south wall.</summary>
    public static IfcExport.Model Garage() => new()
    {
        Info = ProjectInfo.IfcInfo(null, "Garage"),
        Walls =
        {
            Wall("w01", Box(0, 0, 8000, 200)),
            Wall("w02", Box(0, 3800, 8000, 4000)),
            Wall("w03", Box(0, 200, 200, 3800)),
            Wall("w04", Box(7800, 200, 8000, 3800)),
        },
        Openings =
        {
            new() { Id = "o-door", Host = "w01", Kind = "door", Type = "door.hinged_single", Hand = "left", Mark = "D01", Centre = new Pt(1200, 100), Along = new Pt(1, 0), Width = 900, Sill = 0, Head = 2100 },
            new() { Id = "o-window", Host = "w01", Kind = "window", Type = "window.fixed", Mark = "V01", Centre = new Pt(4800, 100), Along = new Pt(1, 0), Width = 1200, Sill = 900, Head = 2100 },
        },
        Slabs = { new() { Id = "floor", Rings = Box(0, 0, 8000, 4000), Thickness = 200, Base = -200 } },
        Roofs = { new() { Id = "roof", Rings = Box(0, 0, 8000, 4000), Thickness = 200, Base = 3000 } },
        Spaces = { new() { Id = "rd-01", Name = "Garage", Ring = Box(200, 200, 7800, 3800)[0], AreaM2 = 27.36, Height = 3000 } },
    };

    static DatabaseIfc WriteAndRead(IfcExport.Model model, string path)
    {
        var db = IfcExport.Build(model);
        Assert.True(db.WriteFile(path), "write " + path);
        return new DatabaseIfc(path);
    }

    /// <summary>N1: the project info names the IfcProject, the IfcSite with its postal address, and Forsk_ProjectInfo on the building.</summary>
    [Fact]
    public void TheProjectInfo_NamesTheProjectSiteAndBuilding()
    {
        var model = Garage();
        model.Info = ProjectInfo.IfcInfo(ProjectInfoTests.Read(ProjectInfoTests.Smoke), "garage-file");
        var db = WriteAndRead(model, "/tmp/forsk-ifc-garage-info.ifc");
        var project = db.OfType<IfcProject>().Single();
        Assert.Equal("2026-07", project.Name);
        Assert.Equal("Garage", project.LongName);
        var site = db.OfType<IfcSite>().Single();
        Assert.Equal("Storgata 1, 0150 Oslo", site.Name);
#pragma warning disable CS0618 // IFC4 has SiteAddress; IFC4X3 deprecates it.
        Assert.Equal(new[] { "Storgata 1, 0150 Oslo" }, site.SiteAddress.AddressLines.ToArray());
#pragma warning restore CS0618
        var building = db.OfType<IfcBuilding>().Single();
        Assert.Equal("Garage", building.Name);
        string Value(string name) => ((building.FindProperty(name) as IfcPropertySingleValue)?.NominalValue as IfcLabel)?.Value?.ToString();
        Assert.NotNull(building.FindPropertySet("Forsk_ProjectInfo"));
        Assert.Equal("Ola Nordmann", Value("Client"));
        Assert.Equal("Riise Arkitekter", Value("Architect"));
        Assert.Equal("2026-07", Value("ProjectNumber"));
        Assert.Equal("B", Value("Revision"));
        Assert.Equal("2026-10-04", Value("Date"));
    }

    [Fact]
    public void NoProjectInfo_KeepsTheFileName_AndAPlainSite()
    {
        var db = IfcExport.Build(Garage());
        Assert.Equal("Garage", db.OfType<IfcProject>().Single().Name);
        Assert.Equal("Site", db.OfType<IfcSite>().Single().Name);
#pragma warning disable CS0618
        Assert.Null(db.OfType<IfcSite>().Single().SiteAddress);
#pragma warning restore CS0618
        Assert.Null(db.OfType<IfcBuilding>().Single().FindPropertySet("Forsk_ProjectInfo"));
    }

    [Fact]
    public void Garage_CountsPerClass_AreTheRecords()
    {
        var db = WriteAndRead(Garage(), GaragePath);
        Assert.Single(db.OfType<IfcProject>());
        Assert.Single(db.OfType<IfcBuildingStorey>());
        Assert.Equal(4, db.OfType<IfcWall>().Count(w => w.PredefinedType == IfcWallTypeEnum.STANDARD));
        Assert.Single(db.OfType<IfcDoor>());
        Assert.Single(db.OfType<IfcWindow>());
        Assert.Equal(2, db.OfType<IfcOpeningElement>().Count());
        Assert.Single(db.OfType<IfcRoof>());
        Assert.Equal(new[] { IfcSlabTypeEnum.FLOOR, IfcSlabTypeEnum.ROOF }, db.OfType<IfcSlab>().Select(s => s.PredefinedType).OrderBy(t => t));
        Assert.Single(db.OfType<IfcSpace>());
        Assert.Equal(new[] { "w01", "w02", "w03", "w04" }, db.OfType<IfcWall>().Select(w => w.Tag).OrderBy(t => t));
    }

    [Fact]
    public void EveryDoorAndWindow_FillsAnOpening_ThatVoidsItsOwnHost()
    {
        var db = WriteAndRead(Garage(), "/tmp/forsk-ifc-fills.ifc");
        var filled = db.OfType<IfcElement>().Where(e => e is IfcDoor || e is IfcWindow).ToList();
        Assert.Equal(2, filled.Count);
        foreach (var element in filled)
        {
            var opening = element.FillsVoids?.RelatingOpeningElement;
            Assert.NotNull(opening);
            Assert.Equal("w01", opening.VoidsElement?.RelatingBuildingElement?.Tag);
        }
        var door = db.OfType<IfcDoor>().Single();
        Assert.Equal("D01", door.Name);
        Assert.Equal(900, door.OverallWidth, 3);
        Assert.Equal(2100, door.OverallHeight, 3);
        Assert.Equal(IfcDoorTypeOperationEnum.SINGLE_SWING_LEFT, door.OperationType);
        var window = db.OfType<IfcWindow>().Single();
        Assert.Equal("V01", window.Name);
        Assert.Equal(1200, window.OverallHeight, 3);
        Assert.Equal(IfcWindowTypePartitioningEnum.SINGLE_PANEL, window.PartitioningType);
    }

    [Fact]
    public void TheSpace_HasItsIdNameAndArea()
    {
        var db = WriteAndRead(Garage(), "/tmp/forsk-ifc-space.ifc");
        var space = db.OfType<IfcSpace>().Single();
        Assert.Equal("rd-01", space.Name);
        Assert.Equal("Garage", space.LongName);
        Assert.Equal("unassigned", space.ObjectType);
        var area = (space.FindQuantity("NetFloorArea") as IfcQuantityArea)?.AreaValue ?? 0;
        Assert.InRange(area, 27.36 * 0.99, 27.36 * 1.01);
    }

    /// <summary>
    /// A one-run record is a standard wall: IFC4 deprecates
    /// IfcWallStandardCase, so it is an IfcWall, PredefinedType STANDARD,
    /// with its axis and a material layer set usage of its thickness.
    /// </summary>
    [Fact]
    public void AOneRunRecord_IsAStandardWall_WithAxisAndLayerSetUsage_OthersAreNot()
    {
        var model = Garage();
        // An L of two runs in one record is a plain extruded IfcWall.
        model.Walls.Add(Wall("w05", new() { new() { new(2000, 200), new(2100, 200), new(2100, 2000), new(3000, 2000), new(3000, 2100), new(2000, 2100) } }));
        var db = WriteAndRead(model, "/tmp/forsk-ifc-kinds.ifc");
        var standard = db.OfType<IfcWall>().Where(w => w.PredefinedType == IfcWallTypeEnum.STANDARD).ToList();
        Assert.Equal(new[] { "w01", "w02", "w03", "w04" }, standard.Select(w => w.Tag).OrderBy(t => t));
        foreach (var wall in standard)
        {
            var usage = wall.HasAssociations.OfType<IfcRelAssociatesMaterial>().Select(r => r.RelatingMaterial).OfType<IfcMaterialLayerSetUsage>().SingleOrDefault();
            Assert.NotNull(usage);
            Assert.Equal(200, usage.ForLayerSet.MaterialLayers.Sum(l => l.LayerThickness), 3);
            Assert.Contains(wall.Representation.Representations, r => r.RepresentationIdentifier == "Axis");
        }
        var other = db.OfType<IfcWall>().Single(w => w.Tag == "w05");
        Assert.NotEqual(IfcWallTypeEnum.STANDARD, other.PredefinedType);
    }

    [Fact]
    public void OuterWalls_AreExternal_AWallInside_IsNot_AnExistingOne_SaysSo()
    {
        var model = Garage();
        model.Walls.Add(Wall("w05", Box(4000, 200, 4100, 3800)));
        model.Walls[0].Existing = true;
        var db = WriteAndRead(model, "/tmp/forsk-ifc-pset.ifc");
        bool External(string tag) =>
            ((db.OfType<IfcWall>().Single(w => w.Tag == tag).FindProperty("IsExternal") as IfcPropertySingleValue)?.NominalValue as IfcBoolean)?.Boolean == true;
        Assert.True(External("w01"));
        Assert.True(External("w03"));
        Assert.False(External("w05"));
        var status = db.OfType<IfcWall>().Single(w => w.Tag == "w01").FindProperty("Status") as IfcPropertyEnumeratedValue;
        Assert.Equal("EXISTING", status?.EnumerationValues.FirstOrDefault()?.ValueString);
        Assert.Null(db.OfType<IfcWall>().Single(w => w.Tag == "w02").FindProperty("Status"));
    }

    [Theory]
    [InlineData("door.hinged_single", "right", "SINGLE_SWING_RIGHT")]
    [InlineData("door.hinged_double", null, "DOUBLE_PANEL_SINGLE_SWING")]
    [InlineData("door.sliding", "left", "SLIDING_TO_LEFT")]
    [InlineData("door.pocket", "right", "SLIDING_TO_RIGHT")]
    [InlineData("window.fixed", null, "FIXEDCASEMENT")]
    [InlineData("window.side_hung", "left", "SIDEHUNGLEFTHAND")]
    [InlineData("window.top_hung", null, "TOPHUNG")]
    public void EachForskType_HasItsOperation(string type, string hand, string operation)
    {
        Assert.Equal(operation, IfcExport.Operation(type, hand));
    }

    [Fact]
    public void TheReceipt_CountsWhatWasWritten()
    {
        Assert.Equal("✓ Exported IFC · 4 walls, 1 door, 1 window, 1 space · Garage.ifc",
            IfcExport.Receipt(Garage(), "/Users/jr/Desktop/Garage.ifc"));
    }
}

/// <summary>R5: the garage with a straight stair along its north wall, as IfcStair and its flight.</summary>
public class IfcStairTests
{
    public const string StairPath = "/tmp/forsk-ifc-stair.ifc";

    public static IfcExport.Model GarageWithStair()
    {
        var model = IfcExportTests.Garage();
        model.Stairs.Add(new IfcExport.Stair { Id = "S01", X = 1000, Y = 3350, Base = 0, Dx = 1, Dy = 0, Flight = Stairs.Plan(2750, 180, 260, 900) });
        return model;
    }

    [Fact]
    public void Stair_IsAStraightRun_AggregatingItsFlight_WithTheFigures()
    {
        var db = IfcExport.Build(GarageWithStair());
        Assert.True(db.WriteFile(StairPath));
        var read = new DatabaseIfc(StairPath);
        var stair = Assert.Single(read.OfType<IfcStair>());
        Assert.Equal(IfcStairTypeEnum.STRAIGHT_RUN_STAIR, stair.PredefinedType);
        Assert.Equal("S01", stair.Tag);
        var flight = Assert.Single(read.OfType<IfcStairFlight>());
        Assert.Equal(IfcStairFlightTypeEnum.STRAIGHT, flight.PredefinedType);
        Assert.Equal(16, flight.NumberOfRiser);
        Assert.Equal(16, flight.NumberOfTreads);
        Assert.Equal(171.875, flight.RiserHeight, 6);
        Assert.Equal(260, flight.TreadLength, 6);
        Assert.Same(stair, flight.Decomposes?.RelatingObject);
        Assert.NotNull(flight.Representation);
        Assert.Contains("1 stair", IfcExport.Receipt(GarageWithStair(), StairPath));
    }
}
