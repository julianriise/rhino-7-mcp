using System.Text;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The support payload and the one sentence each response code becomes. No network.</summary>
public class SupportPayloadTests
{
    static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    static ForskSupport.Body Bug(string message = "the print is blank", string email = "ada@example.com", string debug = null)
    {
        return ForskSupport.Build("Bug", message, email, "0.3.1-r7", "7.34", "macOS", debug);
    }

    [Theory]
    [InlineData("Bug", "bug")]
    [InlineData("bug", "bug")]
    [InlineData("Question", "question")]
    [InlineData("question", "question")]
    [InlineData("Feature request", "feature")]
    [InlineData("feature", "feature")]
    [InlineData("  Bug  ", "bug")]
    public void TypeDropdown_MapsToTheApiWord(string label, string api)
    {
        Assert.Equal(api, ForskSupport.MapType(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("Bugs")]
    public void AnUnknownType_DoesNotMap(string label)
    {
        Assert.Null(ForskSupport.MapType(label));
    }

    [Fact]
    public void Build_PostsTheContractFields_AndNeverTheHoneypot()
    {
        var body = Bug();
        Assert.True(body.Ok);
        Assert.False(body.DebugTruncated);
        Assert.Equal("bug", body.Json["type"]!.ToString());
        Assert.Equal("the print is blank", body.Json["message"]!.ToString());
        Assert.Equal("ada@example.com", body.Json["reply_email"]!.ToString());
        Assert.Equal("0.3.1-r7", body.Json["plugin_version"]!.ToString());
        Assert.Equal("7.34", body.Json["rhino_version"]!.ToString());
        Assert.Equal("macOS", body.Json["os"]!.ToString());
        Assert.Null(body.Json["debug_report"]);
        Assert.Null(body.Json["website"]);
        Assert.DoesNotContain("website", body.Json.Properties().Select(p => p.Name));
    }

    [Fact]
    public void Build_TrimsTheMessageAndTheEmail_AndDropsBlankMeta()
    {
        var body = ForskSupport.Build("Question", "  how do I print  ", "  ada@example.com  ", "  ", null, "", null);
        Assert.True(body.Ok);
        Assert.Equal("question", body.Json["type"]!.ToString());
        Assert.Equal("how do I print", body.Json["message"]!.ToString());
        Assert.Equal("ada@example.com", body.Json["reply_email"]!.ToString());
        Assert.Null(body.Json["plugin_version"]);
        Assert.Null(body.Json["rhino_version"]);
        Assert.Null(body.Json["os"]);
    }

    [Fact]
    public void Build_CapsMetaAt200Characters()
    {
        var longMeta = new string('v', 250);
        var body = ForskSupport.Build("Bug", "broken", "ada@example.com", longMeta, longMeta, longMeta, null);
        Assert.True(body.Ok);
        Assert.Equal(200, body.Json["plugin_version"]!.ToString().Length);
        Assert.Equal(200, body.Json["rhino_version"]!.ToString().Length);
        Assert.Equal(200, body.Json["os"]!.ToString().Length);
    }

    [Theory]
    [InlineData("", "invalid_reply_email")]
    [InlineData("ada", "invalid_reply_email")]
    [InlineData("ada@example", "invalid_reply_email")]
    [InlineData("ada example@example.com", "invalid_reply_email")]
    public void ABadEmail_IsRejected(string email, string code)
    {
        var body = Bug(email: email);
        Assert.False(body.Ok);
        Assert.Equal(code, body.Error);
        Assert.Null(body.Json);
    }

    [Fact]
    public void AnEmailOver254Characters_IsRejected()
    {
        var email = new string('a', 250) + "@b.co";
        Assert.True(email.Length > 254);
        var body = Bug(email: email);
        Assert.False(body.Ok);
        Assert.Equal("invalid_reply_email", body.Error);
    }

    [Fact]
    public void AnEmptyOrHugeMessage_IsRejected()
    {
        Assert.Equal("missing_message", Bug("   ").Error);
        Assert.Equal("invalid_type", ForskSupport.Build("nope", "broken", "ada@example.com", null, null, null, null).Error);
        var huge = new string('a', ForskSupport.MessageMaxChars + 1);
        Assert.Equal("message_too_long", Bug(huge).Error);
        var edge = Bug(new string('a', ForskSupport.MessageMaxChars));
        Assert.True(edge.Ok);
        Assert.Equal(ForskSupport.MessageMaxChars, edge.Json["message"]!.ToString().Length);
    }

    [Fact]
    public void Debug_RidesAlongOnlyWhenItWasGiven()
    {
        var with = Bug(debug: "plugin line");
        Assert.Equal("plugin line", with.Json["debug_report"]!.ToString());
        Assert.False(with.DebugTruncated);
        var empty = Bug(debug: "");
        Assert.Equal("", empty.Json["debug_report"]!.ToString());
    }

    [Fact]
    public void ALongDebugReport_IsCutOnAUtf8Boundary_WithANote()
    {
        var emoji = "🙂";
        var debug = string.Concat(Enumerable.Repeat(emoji, 80000));
        Assert.True(Encoding.UTF8.GetByteCount(debug) > ForskSupport.DebugMaxBytes);
        var body = Bug(debug: debug);
        Assert.True(body.Ok);
        Assert.True(body.DebugTruncated);
        var attached = body.Json["debug_report"]!.ToString();
        Assert.EndsWith(ForskSupport.TruncationNote, attached);
        Assert.True(Encoding.UTF8.GetByteCount(attached) <= ForskSupport.DebugMaxBytes);
        Assert.Equal(attached, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(attached)));
        Assert.DoesNotContain("\uFFFD", attached);
        var json = Encoding.UTF8.GetByteCount(body.Json.ToString(Newtonsoft.Json.Formatting.None));
        Assert.True(json <= ForskSupport.BodyMaxBytes);
    }

    [Fact]
    public void ADebugReportThatEscapesPastTheBodyCap_IsShrunk()
    {
        var debug = new string('\\', 180000);
        Assert.True(Encoding.UTF8.GetByteCount(debug) <= ForskSupport.DebugMaxBytes);
        var body = Bug(debug: debug);
        Assert.True(body.Ok);
        Assert.True(body.DebugTruncated);
        var attached = body.Json["debug_report"]!.ToString();
        Assert.EndsWith(ForskSupport.TruncationNote, attached);
        Assert.True(Encoding.UTF8.GetByteCount(attached) <= ForskSupport.DebugMaxBytes);
        var json = Encoding.UTF8.GetByteCount(body.Json.ToString(Newtonsoft.Json.Formatting.None));
        Assert.True(json <= ForskSupport.BodyMaxBytes);
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"id\":\"FS-1234ABCD\"}", null, "en", true, false, "Sent. Reference FS-1234ABCD — we'll reply by email.")]
    [InlineData(200, "{\"ok\":true,\"id\":\"fs-7k2m9qxd\"}", null, "nb", true, false, "Sendt. Referanse FS-7K2M9QXD — vi svarer på e-post.")]
    [InlineData(503, "{\"ok\":false,\"error\":\"storage_unavailable\"}", null, "en", false, true, "Couldn't reach Forsk support right now — your report is saved and will be sent later.")]
    [InlineData(503, "{\"ok\":false,\"error\":\"storage_unavailable\"}", null, "nb", false, true, "Fikk ikke kontakt med Forsk support akkurat nå — rapporten er lagret og sendes senere.")]
    [InlineData(503, "{\"ok\":false}", null, "en", false, true, "Couldn't reach Forsk support right now — your report is saved and will be sent later.")]
    [InlineData(503, "{\"ok\":false,\"error\":\"email_not_configured\"}", null, "en", false, true, "Support email isn't set up yet — your report is saved and will be sent later.")]
    [InlineData(503, "{\"ok\":false,\"error\":\"email_not_configured\"}", null, "nb", false, true, "E-post til support er ikke satt opp ennå — rapporten er lagret og sendes senere.")]
    [InlineData(502, "{\"ok\":false,\"error\":\"email_send_failed\"}", null, "en", false, true, "The report didn't go through — it's saved and will be sent later.")]
    [InlineData(502, "{\"ok\":false,\"error\":\"email_send_failed\"}", null, "nb", false, true, "Rapporten kom ikke frem — den er lagret og sendes senere.")]
    [InlineData(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "120", "en", false, false, "Too many reports just now — try again in 2 minutes.")]
    [InlineData(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "120", "nb", false, false, "For mange rapporter akkurat nå — prøv igjen om 2 minutter.")]
    [InlineData(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "60", "en", false, false, "Too many reports just now — try again in 1 minute.")]
    [InlineData(429, "{\"ok\":false,\"error\":\"rate_limited\"}", "61", "nb", false, false, "For mange rapporter akkurat nå — prøv igjen om 2 minutter.")]
    [InlineData(429, "{\"ok\":false,\"error\":\"rate_limited\"}", null, "en", false, false, "Too many reports just now — try again in a few minutes.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"invalid_reply_email\"}", null, "en", false, false, "Type the email we should reply to.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"invalid_reply_email\"}", null, "nb", false, false, "Skriv e-postadressen vi skal svare til.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"missing_message\"}", null, "en", false, false, "Write what happened, then send.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"missing_message\"}", null, "nb", false, false, "Skriv hva som skjedde, og send.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"message_too_long\"}", null, "nb", false, false, "Beskrivelsen er for lang. Korte den ned, og send.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"invalid_type\"}", null, "en", false, false, "Choose Bug, Question, or Feature request.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"invalid_json\"}", null, "en", false, false, "That report couldn't be sent. Check it and try again.")]
    [InlineData(400, "{\"ok\":false,\"error\":\"invalid_plugin_version\"}", null, "nb", false, false, "Rapporten ble ikke sendt. Se over den og prøv igjen.")]
    [InlineData(413, "{\"ok\":false,\"error\":\"payload_too_large\"}", null, "en", false, false, "That report is too large to send.")]
    [InlineData(413, "{\"ok\":false,\"error\":\"debug_report_too_large\"}", null, "nb", false, false, "Feilsøkingsrapporten er for stor. Fjern haken og send på nytt.")]
    public void EachCode_IsOneSentence(int status, string json, string retryAfter, string language, bool accepted, bool retry, string sentence)
    {
        var outcome = ForskSupport.Parse(status, json, retryAfter, language, Now);
        Assert.Equal(accepted, outcome.Accepted);
        Assert.Equal(retry, outcome.Retry);
        Assert.Equal(sentence, outcome.Text);
        Assert.DoesNotContain("{", outcome.Text);
        Assert.DoesNotContain("ok", outcome.Text);
    }

    [Fact]
    public void A200WithoutAnId_StillSaysSent_AndANetworkMiss_IsKept()
    {
        var bare = ForskSupport.Parse(200, "{\"ok\":true}", null, "en", Now);
        Assert.True(bare.Accepted);
        Assert.False(bare.Retry);
        Assert.Null(bare.Id);
        Assert.Equal("Sent. We'll reply by email.", bare.Text);

        var down = ForskSupport.Network("nb");
        Assert.False(down.Accepted);
        Assert.True(down.Retry);
        Assert.Equal(0, down.Status);
        Assert.Equal("Rapporten kom ikke frem — den er lagret og sendes senere.", down.Text);
    }

    [Fact]
    public void RetryAfter_ReadsSecondsAndAnHttpDate()
    {
        Assert.Equal(90, ForskSupport.RetrySeconds("90", Now));
        Assert.Null(ForskSupport.RetrySeconds("  ", Now));
        var later = Now.AddMinutes(3).ToString("r");
        Assert.Equal(180, ForskSupport.RetrySeconds(later, Now));
        var outcome = ForskSupport.Parse(429, "{\"ok\":false,\"error\":\"rate_limited\"}", later, "en", Now);
        Assert.Equal("Too many reports just now — try again in 3 minutes.", outcome.Text);
        Assert.Equal("FS-1234ABCD", ForskSupport.Parse(200, "{\"ok\":true,\"id\":\"FS-1234ABCD\"}", null, "en", Now).Id);
    }

    [Fact]
    public void Sentences_DoNotCarryTheResponseBody()
    {
        var raw = "{\"ok\":false,\"error\":\"storage_unavailable\",\"detail\":\"nope\"}";
        var outcome = ForskSupport.Parse(503, raw, null, "en", Now);
        Assert.Equal("storage_unavailable", outcome.Code);
        Assert.True(outcome.Retry);
        Assert.DoesNotContain("detail", outcome.Text);
        Assert.DoesNotContain("nope", outcome.Text);
        var legacy = ForskSupport.Parse(503, "{\"ok\":false,\"error\":\"email_not_configured\",\"detail\":\"nope\"}", null, "en", Now);
        Assert.Equal("email_not_configured", legacy.Code);
        Assert.True(legacy.Retry);
        Assert.DoesNotContain("nope", legacy.Text);
        Assert.Null(JObject.Parse(raw)["website"]);
    }
}
