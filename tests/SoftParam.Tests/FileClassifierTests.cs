using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F1.1: the pure classifier over object rows, and slot 1 from the file
/// state alone. The cases are the brief's list.
/// </summary>
public class FileClassifierTests
{
    static string[] Bar(FileFacts f) => ForskRegistry.Bar(f).Slots.Select(a => a.Label).ToArray();

    [Fact]
    public void AGeneratedWallOnAHiddenLayer_StillCountsAsAWall()
    {
        var f = Docs.Facts("hidden wall");
        Assert.Equal(FileKind.Model, f.Kind);
        Assert.Equal("file.print", ForskRegistry.Slot1(f).Id);
        Assert.False(ForskRegistry.Find("file.generate").When(f));
    }

    [Fact]
    public void AGeneratedFloorAndNoWall_IsPartial_AndSlot1IsRebuild()
    {
        var f = Docs.Facts("partial");
        Assert.Equal(FileKind.Partial, f.Kind);
        Assert.Equal("file.rebuild", ForskRegistry.Slot1(f).Id);
        Assert.Equal("The model is only partly built.", ForskRegistry.Bar(f).Reason);
        Assert.DoesNotContain("Generate 3D", Bar(f));
    }

    [Theory]
    [InlineData("aia only")]
    [InlineData("hand made")]
    public void AiaLayerNamesOnly_OrHandMadeGeometryOnly_AreForeign(string name)
    {
        var f = Docs.Facts(name);
        Assert.Equal(FileKind.Foreign, f.Kind);
        Assert.Equal("file.use_curves", ForskRegistry.Slot1(f).Id);
        Assert.Equal("There is geometry here that Forsk did not make.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void AnEmptyDocument_ImportAPlan_DrawAWall_ThenHelp()
    {
        var f = Docs.Facts("empty");
        Assert.Equal(FileKind.Empty, f.Kind);
        Assert.Equal(new[] { "Import a plan", "Draw a wall" }, Bar(f));
        Assert.Equal("Nothing is in this file yet.", ForskRegistry.Bar(f).Reason);
        Assert.Equal("?", ForskRegistry.Bar(f).ToJson()["help"]!["label"]!.ToString());
    }

    [Fact]
    public void PlanCurvesAndNoGeneratedObject_Slot1IsGenerate()
    {
        var f = Docs.Facts("plan curves");
        Assert.Equal(FileKind.Plan, f.Kind);
        Assert.Equal("file.generate", ForskRegistry.Slot1(f).Id);
        Assert.Equal("The plan is 2D. Scale is settled.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void ASourceCurveOnAPlanLayer_IsNotAWallSelection_GenerateStaysSlot1()
    {
        var f = Docs.Facts("plan curve selected");
        Assert.Equal(Picked.PlanCurve, f.Picked);
        Assert.Equal("file.generate", ForskRegistry.Slot1(f).Id);
        Assert.DoesNotContain("Move", Bar(f));
    }

    [Fact]
    public void AnUnderlayWhoseScaleIsNotUser_Slot1IsSetScale_GenerateBesideIt()
    {
        var f = Docs.Facts("unscaled");
        Assert.Equal(FileKind.Unscaled, f.Kind);
        Assert.Equal(new[] { "Set scale", "Generate 3D", "Draw a wall" }, Bar(f));
        Assert.Equal("The scale is not confirmed.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void AnUnscaledUnderlayWithNoCurves_HasNoGenerateBesideSetScale()
    {
        var f = Docs.Facts("unscaled, no curves");
        Assert.Equal(new[] { "Set scale", "Draw a wall" }, Bar(f));
    }

    [Fact]
    public void TheBlockInstanceAlone_PicksAsOneOpening_WithItsKind()
    {
        // S2: a click on any part of a door picks its one instance on A-OPEN::Block.
        var f = FileClassifier.Read(Docs.Of(Row.Wall(), Row.Door(), Row.DoorFrame(selected: true)));
        Assert.Equal(Picked.Opening, f.Picked);
        Assert.Equal(1, f.PickedCount);
        Assert.Equal("door", f.PickedOpeningKind);
    }

    [Fact]
    public void AGeneratedWallWithADoorSelected_Slot1IsStillPrint()
    {
        var f = Docs.Facts("house, door selected");
        Assert.Equal(Picked.Opening, f.Picked);
        Assert.Equal("door", f.PickedOpeningKind);
        var bar = ForskRegistry.Bar(f);
        Assert.Equal(new[] { "file.print", "opening.move", "opening.resize" }, bar.Slots.Select(a => a.Id));
        Assert.Equal(Runs.Prefill, bar.Context[0].Runs);
        Assert.Equal(Runs.Prefill, bar.Context[1].Runs);
    }

    [Fact]
    public void AWallEditThatHidesTheMap_SetsStale()
    {
        var shown = Row.Map(visible: true);
        Assert.Equal(MapState.Shown, FileClassifier.Read(Docs.Of(Docs.House().Append(shown).ToArray())).Map);

        var visible = shown.Visible;
        var stale = shown.Stale;
        DaylightMap.AfterEdit(MapEdit.Wall, ref visible, ref stale);
        var f = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Map(visible, stale)).ToArray()));

        Assert.False(visible);
        Assert.True(stale);
        Assert.Equal(MapState.Stale, f.Map);
        Assert.Equal("daylight.again", ForskRegistry.DaylightAction(f)!.Id);
        Assert.False(ForskRegistry.Find("daylight.show").When(f));
        Assert.Equal("The map is out of date.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void AnOpeningEdit_LeavesTheMapVisible_AndSetsStale()
    {
        var visible = true;
        var stale = false;
        DaylightMap.AfterEdit(MapEdit.Opening, ref visible, ref stale);
        var f = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Map(visible, stale)).ToArray()));

        Assert.True(visible);
        Assert.True(stale);
        Assert.Equal(MapState.Stale, f.Map);
        Assert.Equal("daylight.again", ForskRegistry.DaylightAction(f)!.Id);
        Assert.False(ForskRegistry.Find("daylight.hide").When(f));
    }

    [Fact]
    public void AnOutsideEdit_ClearsUndo()
    {
        var tracker = new LastActionTracker();
        tracker.Record(new LastAction { Kind = "answer", Doc = 4, Turn = 1, Record = "Forsk: move the north wall", Undoable = true });
        tracker.ObjectChanged(insideForskCall: true);
        Assert.True(tracker.UndoNewest(4));
        var withUndo = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.UndoNewest = tracker.UndoNewest(4)));
        Assert.Equal("edit.undo", ForskRegistry.Bar(withUndo).Context[0].Id);

        tracker.ObjectChanged(insideForskCall: false);

        Assert.False(tracker.UndoNewest(4));
        var after = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.UndoNewest = tracker.UndoNewest(4)));
        Assert.DoesNotContain(ForskRegistry.Bar(after).Slots, a => a.Id == "edit.undo");
        Assert.DoesNotContain(ForskRegistry.Card(after).Actions, a => a.Id == "edit.undo");
    }

    [Fact]
    public void TheExistingHouseOnly_IsNotForeign_AndAsksForAPlan()
    {
        var f = Docs.Facts("existing only");
        Assert.Equal(FileKind.NoPlan, f.Kind);
        Assert.Equal("file.import", ForskRegistry.Slot1(f).Id);
        Assert.Equal("There is no plan in this file yet.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void StoredReviewRows_AreRead_AndCheckThePlanIsOffered()
    {
        var f = Docs.Facts("scaled, reviewed");
        Assert.Equal(new[] { "2 doors without a swing", "the kitchen is open" }, f.Review);
        Assert.Contains("Check the plan", Bar(f));
        Assert.False(Docs.Facts("scaled, no curves").ReviewStored);
    }

    [Fact]
    public void SheetsOlderThanTheModel_KeepPrintInSlot1_AndSayWhy()
    {
        var f = Docs.Facts("printed, then edited");
        Assert.True(f.SheetsStale);
        Assert.Equal("file.print", ForskRegistry.Slot1(f).Id);
        Assert.Equal("Sheets are older than the model.", ForskRegistry.Bar(f).Reason);
    }

    [Fact]
    public void ASelectedWall_PutsMoveInTheBar_AndTheReasonNamesTheRecord()
    {
        var f = Docs.Facts("house, wall selected");
        var bar = ForskRegistry.Bar(f);
        Assert.Equal(new[] { "file.print", "wall.move", "opening.add_door" }, bar.Slots.Select(a => a.Id));
        Assert.Equal("The click selects the whole wall record.", bar.Reason);
    }

    [Fact]
    public void ASelectedWallWithNoKey_LeavesTheSlotEmpty_NotAFileLevelAction()
    {
        var bar = ForskRegistry.Bar(Docs.Facts("house, wall selected, no key"));
        Assert.Equal(new[] { "file.print", "wall.move" }, bar.Slots.Select(a => a.Id));
    }

    [Fact]
    public void ASelectedRoom_ShowsBothRoomActions_PrintStaysSlot1()
    {
        var bar = ForskRegistry.Bar(Docs.Facts("house, room selected"));
        Assert.Equal(new[] { "file.print", "daylight.room", "section.room" }, bar.Slots.Select(a => a.Id));
    }

    [Theory]
    [InlineData("walls only", "daylight.rooms")]
    [InlineData("rooms, no window", "daylight.window")]
    [InlineData("house", "daylight.run")]
    [InlineData("map shown", "daylight.hide")]
    [InlineData("map hidden", "daylight.show")]
    [InlineData("map stale", "daylight.again")]
    public void TheOneDaylightAction_FollowsRoomsWindowsAndTheMap(string name, string expected)
    {
        Assert.Equal(expected, ForskRegistry.DaylightAction(Docs.Facts(name))!.Id);
    }

    [Fact]
    public void AMapOnScreen_PrintsReasonIsThatTheSheetLeavesItOff()
    {
        Assert.Equal("The sheet leaves the map off.", ForskRegistry.Bar(Docs.Facts("map shown")).Reason);
    }

    [Fact]
    public void ALooseClosedCurve_OffersTreatThisAsTheExistingHouse()
    {
        var f = Docs.Facts("loose selected");
        Assert.Equal(Picked.Loose, f.Picked);
        Assert.Contains("exist.mark", ForskRegistry.Bar(f).Slots.Select(a => a.Id));

        var onForskLayer = FileClassifier.Read(Docs.Of(Row.Wall(), Row.Curve("A-ANNO", closed: true, selected: true)));
        Assert.Equal(Picked.Other, onForskLayer.Picked);
        Assert.False(ForskRegistry.Find("exist.mark").When(onForskLayer));
    }

    [Fact]
    public void TheStatusLine_SaysListenerDownAndANonDefaultInk()
    {
        Assert.Equal("Bridge off · ink: grey", ForskRegistry.Status(Docs.Facts("bridge down, grey ink")));
        Assert.Equal("", ForskRegistry.Status(Docs.Facts("house")));
    }

    [Theory]
    [InlineData("empty", "This file is empty.")]
    [InlineData("plan curves", "This file has a 2D plan and no 3D model yet.")]
    [InlineData("house", "This file has a Forsk 3D model.")]
    [InlineData("inches", "This file is not in millimetres. Switch the .3dm to millimetres, then Forsk can work on it.")]
    public void TheLocalSentence_NamesTheFileState(string name, string sentence)
    {
        Assert.Equal(sentence, ForskRegistry.StateSentence(Docs.Facts(name)));
    }
}
