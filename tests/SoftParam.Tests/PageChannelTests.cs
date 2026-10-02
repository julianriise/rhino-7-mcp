using Jint;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D0: the page channel. The old window passed messages through
/// document.title, dropped one that came while busy, and cut text at 500
/// characters. Here the page posts to a real HTTP endpoint on 127.0.0.1,
/// and WebKit may send concurrent posts out of order (the D0 probe saw it).
/// </summary>
public class PageChannelTests
{
    static readonly HttpClient Http = new HttpClient();

    static string LongText(int i)
    {
        // Past the old 500-character cut, with Norwegian letters and a line separator.
        var sb = new StringBuilder();
        sb.Append("action ").Append(i).Append(": ");
        while (sb.Length < 1800) sb.Append("Flytt vinduet på sørveggen, æ ø å Æ Ø Å\u2028");
        return sb.ToString();
    }

    static Task<HttpResponseMessage> Post(PageChannel channel, JObject body)
    {
        return Http.PostAsync(channel.Origin + "action", new StringContent(body.ToString(), Encoding.UTF8, "text/plain"));
    }

    static JObject Action(PageChannel channel, long seq, string text, string token = null)
    {
        return new JObject { ["seq"] = seq, ["t"] = token ?? channel.Token, ["kind"] = "send", ["text"] = text };
    }

    [Fact]
    public async Task FiftyActionsIn_FiftyCallsOut_InOrder_NoneDropped_NoneCut()
    {
        var calls = new List<JObject>();
        using var channel = new PageChannel(m => calls.Add(m));
        channel.Start();

        var order = Enumerable.Range(1, 50).OrderBy(_ => Guid.NewGuid()).ToList();
        var posts = order.Select(i => Post(channel, Action(channel, i, LongText(i)))).ToArray();
        var answers = await Task.WhenAll(posts);

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Assert.Equal(50, calls.Count);
        for (var i = 1; i <= 50; i++)
        {
            Assert.Equal(i, calls[i - 1]["seq"]!.Value<long>());
            Assert.Equal(LongText(i), calls[i - 1]["text"]!.ToString());
        }
        Assert.True(calls.All(c => c["text"]!.ToString().Length > 500));
        Assert.True(calls.All(c => c["t"] == null), "The token stays in the channel.");
    }

    [Fact]
    public async Task ARepeatAfterALostAnswer_IsDeliveredOnce()
    {
        var calls = new List<JObject>();
        using var channel = new PageChannel(m => calls.Add(m));
        channel.Start();

        var first = await Post(channel, Action(channel, 1, "print"));
        var again = await Post(channel, Action(channel, 1, "print"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(JObject.Parse(await again.Content.ReadAsStringAsync())["repeat"]!.Value<bool>());
        Assert.False(JObject.Parse(await first.Content.ReadAsStringAsync())["repeat"]!.Value<bool>());
        Assert.Single(calls);
    }

    [Fact]
    public async Task AGap_Waits_ThenBothGoInOrder()
    {
        var calls = new List<long>();
        using var channel = new PageChannel(m => calls.Add(m["seq"]!.Value<long>()));
        channel.Start();

        await Post(channel, Action(channel, 2, "second"));
        Assert.Empty(calls);
        await Post(channel, Action(channel, 1, "first"));
        Assert.Equal(new long[] { 1, 2 }, calls);
    }

    [Fact]
    public async Task AWrongToken_IsRefused_AndNothingIsCalled()
    {
        var calls = new List<JObject>();
        using var channel = new PageChannel(m => calls.Add(m));
        channel.Start();

        var answer = await Post(channel, Action(channel, 1, "print", token: new string('0', 32)));

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ANewPage_GetsANewToken_AndCountsFromOne()
    {
        var calls = new List<JObject>();
        using var channel = new PageChannel(m => calls.Add(m));
        channel.Start();
        await Post(channel, Action(channel, 1, "old page"));
        var oldToken = channel.Token;

        channel.Reset();
        var stale = await Post(channel, Action(channel, 2, "old page again", token: oldToken));
        var fresh = await Post(channel, Action(channel, 1, "new page"));

        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(new[] { "old page", "new page" }, calls.Select(c => c["text"]!.ToString()));
    }

    [Fact]
    public async Task NotAnAction_IsRefused()
    {
        using var channel = new PageChannel(_ => { });
        channel.Start();

        var noSeq = await Http.PostAsync(channel.Origin + "action", new StringContent("{\"t\":\"" + channel.Token + "\"}"));
        var notJson = await Http.PostAsync(channel.Origin + "action", new StringContent("forsk:print"));
        var elsewhere = await Http.GetAsync(channel.Origin + "anything");

        Assert.Equal(HttpStatusCode.BadRequest, noSeq.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public void TheChannelListensOnLoopbackOnly()
    {
        using var channel = new PageChannel(_ => { });
        channel.Start();

        Assert.StartsWith("http://127.0.0.1:", channel.Origin);
        Assert.Matches("^[0-9a-f]{32}$", channel.Token);
    }
}

/// <summary>
/// D0: the page half of the channel, in Jint. Every action gets the next
/// sequence number and waits in one queue; a failed post is tried again, so
/// none is dropped and the order holds.
/// </summary>
public class PageSenderTests
{
    static Jint.Engine Sender(string failRule)
    {
        var engine = PageScript.Load();
        engine.Execute(@"
            var server = [], attempts = 0, later = [];
            function post(body) {
              attempts++;
              var n = attempts, msg = JSON.parse(body);
              return new Promise(function (resolve, reject) {
                var verdict = (" + failRule + @")(n, msg);
                if (verdict === 'throw') { reject(new Error('network')); return; }
                if (verdict === 200) server.push(msg);
                resolve(verdict);
              });
            }
            var sender = Forsk.makeSender('tok', post, function (fn) { later.push(fn); });");
        return engine;
    }

    static void Drain(Jint.Engine engine)
    {
        for (var round = 0; round < 5000; round++)
        {
            engine.Advanced.ProcessTasks();
            if (engine.Evaluate("later.length").AsNumber() == 0) return;
            engine.Execute("later.shift()()");
        }
        throw new Xunit.Sdk.XunitException("The sender never settled.");
    }

    static List<JObject> Server(Jint.Engine engine)
    {
        return JArray.Parse(engine.Evaluate("JSON.stringify(server)").AsString()).Cast<JObject>().ToList();
    }

    [Fact]
    public void FiftyRapidActions_ThroughFailingPosts_ArriveOnceEach_InOrder()
    {
        // Every 7th post fails on the network, every 11th gets a 503.
        var engine = Sender("function (n) { return n % 7 === 0 ? 'throw' : n % 11 === 0 ? 503 : 200; }");
        engine.Execute("for (var i = 1; i <= 50; i++) sender.send({ kind: 'send', text: 'æøå'.repeat(400) + i });");
        Drain(engine);

        var got = Server(engine);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), got.Select(m => m["seq"]!.Value<long>()));
        Assert.All(got, m => Assert.Equal("tok", m["t"]!.ToString()));
        Assert.All(got, m => Assert.Equal(1200 + (m["seq"]!.Value<long>() < 10 ? 1 : 2), m["text"]!.ToString().Length));
        Assert.Equal(0, engine.Evaluate("sender.waiting()").AsNumber());
    }

    [Fact]
    public void OnePostInFlight_TheNextWaitsForItsAnswer()
    {
        var engine = PageScript.Load();
        engine.Execute(@"
            var open = 0, most = 0, done = [];
            function post(body) {
              open++; most = Math.max(most, open);
              return new Promise(function (resolve) { done.push(function () { open--; resolve(200); }); });
            }
            var sender = Forsk.makeSender('tok', post, function (fn) { fn(); });
            for (var i = 0; i < 5; i++) sender.send({ kind: 'slot', slot: 1 });");
        for (var i = 0; i < 5; i++)
        {
            engine.Advanced.ProcessTasks();
            engine.Execute("if (done.length) done.shift()();");
        }
        engine.Advanced.ProcessTasks();
        Assert.Equal(1, engine.Evaluate("most").AsNumber());
        Assert.Equal(0, engine.Evaluate("sender.waiting()").AsNumber());
    }

    [Fact]
    public void AFinal4xx_MovesOn_ARetryableAnswerDoesNot()
    {
        // The stale page's third action is refused (403); the rest go through.
        var engine = Sender("function (n, msg) { return msg.seq === 3 ? 403 : 200; }");
        engine.Execute("for (var i = 1; i <= 5; i++) sender.send({ kind: 'help' });");
        Drain(engine);

        Assert.Equal(new long[] { 1, 2, 4, 5 }, Server(engine).Select(m => m["seq"]!.Value<long>()));
    }
}
