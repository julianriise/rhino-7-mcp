using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D0: one window serves every open file. The thread is keyed by the
/// document's RuntimeSerialNumber and kept on disk by file path, so it comes
/// back when the window or the file is closed and opened again. An options
/// card is answered once; a closed or stale card does nothing.
/// </summary>
public class WindowModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "forsk-threads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void TwoOpenFiles_KeepTwoThreads()
    {
        var models = new WindowModels(new ThreadStore(_root));
        models.For(1, "a.3dm", "/p/a.3dm").Add("user", "print");
        models.For(2, "b.3dm", "/p/b.3dm").Add("user", "dagslys");

        Assert.Equal("print", models.For(1, null, null).Items.Single()["text"]!.ToString());
        Assert.Equal("dagslys", models.For(2, null, null).Items.Single()["text"]!.ToString());
        Assert.Equal("a.3dm", models.For(1, null, null).ToJson()["file"]!.ToString());
    }

    [Fact]
    public void ReopenedFile_GetsItsThreadBack_UnderTheNewSerial()
    {
        var store = new ThreadStore(_root);
        var first = new WindowModels(store);
        var thread = first.For(7, "house.3dm", "/p/house.3dm");
        thread.Add("user", "project is Tilbygg Holmen");
        thread.History.Add(new JObject { ["role"] = "user", ["content"] = "project is Tilbygg Holmen" });
        first.Persist(thread);
        first.Forget(7);

        var reopened = new WindowModels(store).For(12, "house.3dm", "/p/house.3dm");

        Assert.Equal(12u, reopened.Serial);
        Assert.Equal("project is Tilbygg Holmen", reopened.Items.Single()["text"]!.ToString());
        Assert.Single(reopened.History);
    }

    [Fact]
    public void AnUntitledFile_IsKeptOnlyOnceItHasAPath()
    {
        var store = new ThreadStore(_root);
        var models = new WindowModels(store);
        var thread = models.For(3, "Untitled", null);
        thread.Add("user", "hello");
        models.Persist(thread);
        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root).Length > 0);

        models.For(3, "garage.3dm", "/p/garage.3dm");
        models.Persist(thread);
        Assert.Equal("hello", new WindowModels(store).For(9, null, "/p/garage.3dm").Items.Single()["text"]!.ToString());
    }

    [Fact]
    public void ANewRhinoSession_StartsTheFilesChatFresh_AndTheSameSessionGetsItBack()
    {
        var store = new ThreadStore(_root);
        WindowModels.NewSession();
        var first = new WindowModels(store).For(4, "house.3dm", "/p/house.3dm");
        first.Add("user", "earlier session");
        new WindowModels(store).Persist(first);

        // Rhino quits and opens again: the file's chat starts empty, so What's new sits on top.
        WindowModels.NewSession();
        Assert.Empty(new WindowModels(store).For(5, "house.3dm", "/p/house.3dm").Items);
        // Closed and opened again in the same session, it comes back from the store.
        Assert.Equal("earlier session", new WindowModels(store).For(6, null, "/p/house.3dm").Items.Single()["text"]!.ToString());
    }

    [Fact]
    public void WhatsNew_StaysOpen_WhenItsChatComesBack()
    {
        var thread = new DocThread();
        thread.AddCard(new CardSpec { Kind = ForskWhatsNew.Kind, Question = "What's new" }, null);
        thread.AddCard("print", "Print this file as a PDF?", new CardPill("print", "Print PDF"));
        var restored = new DocThread();
        restored.Restore(thread.Save());
        Assert.Equal("open", restored.Items[0]["state"]!.ToString());
        Assert.Equal("stale", restored.Items[1]["state"]!.ToString());
    }

    [Fact]
    public void ACard_IsAnsweredOnce()
    {
        var thread = new DocThread();
        var card = thread.AddCard("print", "Print this file as a PDF?", new CardPill("print", "Print PDF"), new CardPill("later", "Not now"));
        var id = card["id"]!.ToString();

        Assert.Equal("print", thread.Answer(id, "print")!.Id);
        Assert.Null(thread.Answer(id, "print"));
        Assert.Equal("answered", thread.Find(id)!["state"]!.ToString());
        Assert.Equal("Print PDF", thread.Find(id)!["answer"]!.ToString());
    }

    [Fact]
    public void EscClosesACard_AndAClosedCardDoesNothing()
    {
        var thread = new DocThread();
        var id = thread.AddCard("print", "Print?", new CardPill("print", "Print PDF"))["id"]!.ToString();
        Assert.Equal(id, thread.OpenCard());

        Assert.True(thread.Close(id));
        Assert.Null(thread.OpenCard());
        Assert.Null(thread.Answer(id, "print"));
        Assert.False(thread.Close(id));
    }

    [Fact]
    public void AStaleCard_TurnsGrey_AndTakesNoClick()
    {
        var thread = new DocThread();
        var id = thread.AddCard("print", "Print?", new CardPill("print", "Print PDF"))["id"]!.ToString();

        Assert.Equal(1, thread.StaleOpenCards());
        Assert.Equal("stale", thread.Find(id)!["state"]!.ToString());
        Assert.Null(thread.Answer(id, "print"));
    }

    [Fact]
    public void ACardLeftOpen_ComesBackStale()
    {
        var store = new ThreadStore(_root);
        var models = new WindowModels(store);
        var thread = models.For(1, "a.3dm", "/p/a.3dm");
        var id = thread.AddCard("print", "Print?", new CardPill("print", "Print PDF"))["id"]!.ToString();
        models.Persist(thread);

        var back = new WindowModels(store).For(2, "a.3dm", "/p/a.3dm");
        Assert.Equal("stale", back.Find(id)!["state"]!.ToString());
        Assert.Null(back.OpenCard());
    }

    [Fact]
    public void NewItemsAfterARestore_DoNotReuseIds()
    {
        var store = new ThreadStore(_root);
        var models = new WindowModels(store);
        var thread = models.For(1, "a.3dm", "/p/a.3dm");
        thread.Add("user", "one");
        thread.Add("user", "two");
        models.Persist(thread);

        var back = new WindowModels(store).For(2, "a.3dm", "/p/a.3dm");
        var third = back.Add("user", "three");
        Assert.Equal(3, back.Items.Select(i => i["id"]!.ToString()).Distinct().Count());
        Assert.Equal("m3", third!["id"]!.ToString());
    }

    [Fact]
    public void TheSameStaleNotice_IsNotAddedTwiceInARow()
    {
        var thread = new DocThread();
        var notice = ForskText.Format("bar.refused", "label", "Import a plan");

        thread.AddLine(notice);
        thread.AddLine(notice);

        Assert.Single(thread.Items);
        thread.Add("user", "Print PDF");
        thread.AddLine(notice);
        Assert.Equal(3, thread.Items.Count);
        Assert.Equal(notice, thread.Items[2]["text"]!.ToString());
    }

    [Fact]
    public void TheBusyLine_IsInTheModel_NotOnDisk()
    {
        var thread = new DocThread { Path = "/p/a.3dm", Busy = "Printing… step 1 of 2: laying out the sheets" };
        Assert.Equal(thread.Busy, thread.ToJson()["busy"]!["text"]!.ToString());
        Assert.Null(thread.Save()["busy"]);
    }
}
