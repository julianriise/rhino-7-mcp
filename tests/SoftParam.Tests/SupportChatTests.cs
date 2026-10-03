using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>Send's step line and the receipt. No network, no Rhino.</summary>
public class SupportChatTests
{
    static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASentReport_IsAReceiptWithTheReference()
    {
        var thread = new DocThread { Serial = 1 };
        thread.Busy = "Sending…";
        thread.TurnMark = "Support";
        var outcome = ForskSupport.Parse(200, "{\"ok\":true,\"id\":\"FS-1234ABCD\"}", null, "en", Now);
        Assert.Equal("Sending…", ForskSupportChat.Sending("en"));
        Assert.Equal("Sent", ForskSupportChat.CardAnswer(outcome, "en"));
        ForskSupportChat.Show(thread, outcome);
        Assert.Null(thread.Busy);
        var receipt = Assert.Single(thread.Items);
        Assert.Equal("receipt", receipt["role"]!.ToString());
        Assert.Equal("Support", receipt["mark"]!.ToString());
        Assert.True(receipt.Value<bool>("ok"));
        Assert.Equal("Sent. Reference FS-1234ABCD — we'll reply by email.", receipt["text"]!.ToString());
        Assert.DoesNotContain("{", receipt["text"]!.ToString());
        Assert.DoesNotContain("reply_email", receipt["text"]!.ToString());
    }

    [Fact]
    public void AQueuedReport_IsADash_AndARejectionIsACross()
    {
        var queued = new DocThread { Serial = 1 };
        var later = ForskSupport.Parse(503, "{\"ok\":false,\"error\":\"storage_unavailable\"}", null, "nb", Now);
        Assert.Equal("Sender…", ForskSupportChat.Sending("nb"));
        Assert.Equal("Lagret", ForskSupportChat.CardAnswer(later, "nb"));
        ForskSupportChat.Show(queued, later);
        var saved = Assert.Single(queued.Items);
        Assert.Null(saved["ok"]);
        Assert.Equal("Fikk ikke kontakt med Forsk support akkurat nå — rapporten er lagret og sendes senere.", saved["text"]!.ToString());

        var rejected = new DocThread { Serial = 1 };
        var bad = ForskSupport.Parse(400, "{\"ok\":false,\"error\":\"invalid_reply_email\"}", null, "en", Now);
        Assert.Equal("Not sent", ForskSupportChat.CardAnswer(bad, "en"));
        ForskSupportChat.Show(rejected, bad);
        var cross = Assert.Single(rejected.Items);
        Assert.False(cross.Value<bool>("ok"));
        Assert.Equal("Type the email we should reply to.", cross["text"]!.ToString());
    }

    [Fact]
    public void TheCard_SendsOffTheUiThread_AndKeepsItsFields()
    {
        var window = Source("ForskWindowActions.cs");
        var send = window.Substring(window.IndexOf("void SendReport(", StringComparison.Ordinal));
        send = send.Substring(0, send.IndexOf("static ForskSupport.Outcome PostReport", StringComparison.Ordinal));
        Assert.Contains("thread.Busy = sending", send);
        Assert.Contains("ThreadPool.QueueUserWorkItem", send);
        Assert.Contains("ForskSupportChat.Show", send);
        var post = window.Substring(window.IndexOf("static ForskSupport.Outcome PostReport", StringComparison.Ordinal));
        post = post.Substring(0, post.IndexOf("static string PluginVersion", StringComparison.Ordinal));
        Assert.Contains("ForskSupportSend.Deliver", post);
        Assert.Contains("ForskSupportHttp.Shared", post);
        Assert.DoesNotContain("_reports", window);
        Assert.DoesNotContain("support.saved", window);
        Assert.DoesNotContain("website", post);

        var card = ForskReports.Card("this is broken", "office.3dm", "ada@example.com");
        Assert.Equal(new[] { "type", "description", "email", "attach" }, card.Fields.Select(f => f.Key));
        Assert.DoesNotContain(card.Fields, f => f.Key == "file");
    }

    static string Source(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Forsk", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException(file);
    }
}
