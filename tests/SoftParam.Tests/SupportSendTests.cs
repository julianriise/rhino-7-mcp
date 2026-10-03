using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The outbox and the send. The POST is a stand-in. Nothing calls forsk.app.</summary>
public class SupportSendTests
{
    static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    sealed class FakePost : ISupportPost
    {
        public readonly List<string> Bodies = new List<string>();
        public Func<string, SupportHttpResult> Next = _ => Ok();
        public SupportHttpResult Post(string json)
        {
            Bodies.Add(json);
            return Next(json);
        }
    }

    static SupportHttpResult Ok()
    {
        return new SupportHttpResult { Status = 200, Body = "{\"ok\":true,\"id\":\"FS-1234ABCD\"}" };
    }

    static JObject Payload(string message)
    {
        var body = ForskSupport.Build("Bug", message, "ada@example.com", "0.3.1", "7.34", "macOS", null);
        Assert.True(body.Ok);
        return body.Json;
    }

    static ForskOutbox Box()
    {
        return new ForskOutbox(Path.Combine(Path.GetTempPath(), "forsk-outbox-" + Guid.NewGuid().ToString("n")));
    }

    static void Delete(ForskOutbox box)
    {
        if (box != null && Directory.Exists(box.Root)) Directory.Delete(box.Root, true);
    }

    static int JsonFiles(ForskOutbox box)
    {
        if (!Directory.Exists(box.Root)) return 0;
        return Directory.GetFiles(box.Root).Count(path => path.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public void Pending_IsOldestFirst_AndDropsExpiredOrUnreadable()
    {
        var box = Box();
        try
        {
            Directory.CreateDirectory(box.Root);
            Write(box, "aaa.json", Now.AddMinutes(5), Payload("newer"));
            Write(box, "zzz.json", Now, Payload("older"));
            File.WriteAllText(Path.Combine(box.Root, "bad.json"), "{");
            File.WriteAllText(Path.Combine(box.Root, "empty.json"), "{}");
            box.Enqueue(Payload("ancient"), Now.AddDays(-7));
            box.Enqueue(Payload("kept"), Now.AddDays(-6));

            var pending = box.Pending(Now);
            Assert.Equal(new[] { "kept", "older", "newer" }, pending.Select(item => Message(item.Body)));
            Assert.False(File.Exists(Path.Combine(box.Root, "bad.json")));
            Assert.False(File.Exists(Path.Combine(box.Root, "empty.json")));
            Assert.Equal(3, JsonFiles(box));
            Assert.EndsWith(Path.Combine(".forsk", "outbox"), ForskOutbox.DefaultRoot());
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void AClaim_IsExclusive_AndACrash_PutsItBackOnce()
    {
        var box = Box();
        try
        {
            var item = box.Enqueue(Payload("the print is blank"), Now);
            Assert.True(box.TryClaim(item));
            Assert.False(box.TryClaim(new OutboxItem { JsonPath = item.JsonPath }));
            Assert.Empty(box.Pending(Now));

            var post = new FakePost();
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now, "en"));
            Assert.Empty(post.Bodies);

            var revived = new ForskOutbox(box.Root);
            var sent = ForskSupportSend.Flush(post, revived, Now, "en");
            Assert.Equal(1, sent);
            Assert.Single(post.Bodies);
            Assert.Equal(0, ForskSupportSend.Flush(post, revived, Now, "en"));
            Assert.Single(post.Bodies);
            Assert.Equal(0, JsonFiles(revived));
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void Deliver_PostsOnce_AndA200_IsNotSentAgain()
    {
        var box = Box();
        try
        {
            var post = new FakePost();
            var payload = Payload("the print is blank");
            var outcome = ForskSupportSend.Deliver(payload, post, box, Now, "en");
            Assert.True(outcome.Accepted);
            Assert.False(outcome.Retry);
            Assert.Equal("FS-1234ABCD", outcome.Id);
            Assert.Equal("Sent. Reference FS-1234ABCD — we'll reply by email.", outcome.Text);
            Assert.Equal(payload.ToString(Formatting.None), post.Bodies[0]);
            Assert.DoesNotContain("website", post.Bodies[0]);
            Assert.Equal(0, JsonFiles(box));

            var again = ForskSupportSend.Deliver(payload, post, box, Now.AddMinutes(1), "nb");
            Assert.Equal("Sendt. Referanse FS-1234ABCD — vi svarer på e-post.", again.Text);
            Assert.Equal(2, post.Bodies.Count);
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now.AddMinutes(2), "en"));
            Assert.Equal(2, post.Bodies.Count);
        }
        finally
        {
            Delete(box);
        }
    }

    [Theory]
    [InlineData("storage_unavailable", "Couldn't reach Forsk support right now — your report is saved and will be sent later.")]
    [InlineData("email_not_configured", "Support email isn't set up yet — your report is saved and will be sent later.")]
    public void A503_StaysQueued_AndTheNextPassSendsItOnce(string code, string sentence)
    {
        var box = Box();
        try
        {
            var post = new FakePost();
            var n = 0;
            post.Next = _ =>
            {
                n++;
                return n == 1
                    ? new SupportHttpResult { Status = 503, Body = "{\"ok\":false,\"error\":\"" + code + "\"}" }
                    : Ok();
            };
            var first = ForskSupportSend.Deliver(Payload("the print is blank"), post, box, Now, "en");
            Assert.True(first.Retry);
            Assert.False(first.Accepted);
            Assert.Equal(code, first.Code);
            Assert.Equal(sentence, first.Text);
            Assert.Equal(1, JsonFiles(box));

            Assert.Equal(1, ForskSupportSend.Flush(post, box, Now.AddMinutes(1), "en"));
            Assert.Equal(2, post.Bodies.Count);
            Assert.Equal(post.Bodies[0], post.Bodies[1]);
            Assert.Equal(0, JsonFiles(box));
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now.AddMinutes(2), "en"));
            Assert.Equal(2, post.Bodies.Count);
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void AReportLeftInTheOutbox_IsSentWhenRhinoOpens_AndOnTheNextSend()
    {
        var box = Box();
        try
        {
            box.Enqueue(Payload("left behind"), Now);
            var post = new FakePost();
            Assert.Equal(1, ForskSupportSend.Flush(post, box, Now.AddMinutes(1), "en"));
            Assert.Equal(new[] { "left behind" }, post.Bodies.Select(Message));
            Assert.Equal(0, JsonFiles(box));
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now.AddMinutes(2), "en"));
            Assert.Single(post.Bodies);

            box.Enqueue(Payload("still waiting"), Now.AddMinutes(3));
            post.Bodies.Clear();
            var outcome = ForskSupportSend.Deliver(Payload("the new one"), post, box, Now.AddMinutes(4), "en");
            Assert.True(outcome.Accepted);
            Assert.Equal("FS-1234ABCD", outcome.Id);
            Assert.Equal("Sent. Reference FS-1234ABCD — we'll reply by email.", outcome.Text);
            Assert.Equal(new[] { "still waiting", "the new one" }, post.Bodies.Select(Message));
            Assert.Equal(0, JsonFiles(box));
        }
        finally
        {
            Delete(box);
        }
    }

    [Theory]
    [InlineData(502, "email_send_failed")]
    [InlineData(0, null)]
    public void AFailedSend_IsKeptForTheNextPass(int status, string code)
    {
        var box = Box();
        try
        {
            var post = new FakePost
            {
                Next = _ => status == 0
                    ? throw new InvalidOperationException("down")
                    : new SupportHttpResult { Status = status, Body = "{\"ok\":false,\"error\":\"" + code + "\"}" }
            };
            var outcome = ForskSupportSend.Deliver(Payload("the print is blank"), post, box, Now, "nb");
            Assert.True(outcome.Retry);
            Assert.Equal("Rapporten kom ikke frem — den er lagret og sendes senere.", outcome.Text);
            Assert.Equal(1, JsonFiles(box));
            Assert.Single(post.Bodies);
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void TheQueue_GoesOldestFirst_AndStopsWhenTheServerAsksForAWait()
    {
        var box = Box();
        try
        {
            box.Enqueue(Payload("second"), Now.AddMinutes(2));
            box.Enqueue(Payload("first"), Now);
            var post = new FakePost();
            Assert.Equal(2, ForskSupportSend.Flush(post, box, Now.AddHours(1), "en"));
            Assert.Equal(new[] { "first", "second" }, post.Bodies.Select(Message));

            box.Enqueue(Payload("old"), Now.AddMinutes(3));
            box.Enqueue(Payload("next"), Now.AddMinutes(4));
            post.Bodies.Clear();
            post.Next = _ => new SupportHttpResult { Status = 429, Body = "{\"ok\":false,\"error\":\"rate_limited\"}", RetryAfter = "120" };
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now.AddHours(1), "en"));
            Assert.Equal(new[] { "old" }, post.Bodies.Select(Message));
            Assert.Equal(2, JsonFiles(box));
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void ARejectedReport_IsDropped_AndNotRetried()
    {
        var box = Box();
        try
        {
            box.Enqueue(Payload("nope"), Now);
            var post = new FakePost
            {
                Next = _ => new SupportHttpResult { Status = 400, Body = "{\"ok\":false,\"error\":\"invalid_reply_email\"}" }
            };
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now, "en"));
            Assert.Equal(0, JsonFiles(box));
            Assert.Equal(0, ForskSupportSend.Flush(post, box, Now.AddMinutes(1), "en"));
            Assert.Single(post.Bodies);
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void AnEarlierMiss_LeavesTheNewReportQueued()
    {
        var box = Box();
        try
        {
            box.Enqueue(Payload("already waiting"), Now);
            var post = new FakePost
            {
                Next = _ => new SupportHttpResult { Network = true }
            };
            var outcome = ForskSupportSend.Deliver(Payload("the new one"), post, box, Now.AddMinutes(1), "en");
            Assert.True(outcome.Retry);
            Assert.Equal(new[] { "already waiting" }, post.Bodies.Select(Message));
            Assert.Equal(2, JsonFiles(box));
        }
        finally
        {
            Delete(box);
        }
    }

    [Fact]
    public void TheLivePost_TargetsTheBareEndpoint_WithAFifteenSecondTimeout()
    {
        Assert.Equal("https://forsk.app/api/support", ForskSupport.Endpoint);
        var http = Source("plugin", "Forsk", "ForskSupportHttp.cs");
        Assert.Contains("TimeoutSeconds = 15", http);
        Assert.Contains("ForskSupport.Endpoint", http);
        Assert.Contains("RetryOnStartup", http);
        Assert.DoesNotContain("Authorization", http);
        Assert.DoesNotContain("website", http);
        var plugin = Source("plugin", "RhinoMCPPlugin.cs");
        Assert.Contains("ForskSupportHttp.RetryOnStartup()", plugin);
    }

    static void Write(ForskOutbox box, string name, DateTimeOffset when, JObject payload)
    {
        var stored = new JObject
        {
            ["id"] = name,
            ["queued_at"] = when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["body"] = payload.ToString(Formatting.None)
        };
        File.WriteAllText(Path.Combine(box.Root, name), stored.ToString(Formatting.None));
    }

    static string Message(string json)
    {
        return JObject.Parse(json)["message"]!.ToString();
    }

    static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException(string.Join("/", parts));
    }
}
