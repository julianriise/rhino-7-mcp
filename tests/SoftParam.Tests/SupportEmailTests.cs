using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The reply address. Rhino 7 does not give one, so the card has a field,
/// and the address is remembered under ~/.forsk. No network.
/// </summary>
public class SupportEmailTests
{
    [Fact]
    public void Rhino7_DoesNotGiveAnAccountEmail_SoTheCardAsks()
    {
        Assert.False(ForskReplyEmail.RhinoGivesReplyEmail);
        var card = ForskReports.Card("this is broken", "office.3dm");
        var email = card.Fields.Single(f => f.Key == "email");
        Assert.Equal("", email.Value);
        Assert.Equal("Email", email.Label);
        Assert.False(email.Long);
        Assert.False(email.Check);
        Assert.Null(email.Options);
        Assert.DoesNotContain(card.Fields, f => f.Key == "file");
    }

    [Fact]
    public void ARememberedAddress_IsPrefilled_AndABadOneIsLeftBlank()
    {
        var filled = ForskReports.Card("this is broken", "a.3dm", "ada@example.com");
        Assert.Equal("ada@example.com", filled.Fields.Single(f => f.Key == "email").Value);
        var junk = ForskReports.Card("this is broken", "a.3dm", "not-an-email");
        Assert.Equal("", junk.Fields.Single(f => f.Key == "email").Value);
    }

    [Fact]
    public void AnEmptyEmail_HoldsTheCard_AndNamesTheField()
    {
        Assert.Equal("Type the email we should reply to.", ForskSupport.Hold("Bug", "the print is blank", "", "en"));
        Assert.Equal("Skriv e-postadressen vi skal svare til.", ForskSupport.Hold("Bug", "utskriften er blank", "ada", "nb"));
        Assert.Null(ForskSupport.Hold("Bug", "the print is blank", "ada@example.com", "en"));
        Assert.Equal("Write what happened, then send.", ForskSupport.Hold("Bug", "  ", "ada@example.com", "en"));

        var card = new JObject
        {
            ["fields"] = new JArray
            {
                new JObject { ["key"] = "type", ["value"] = "Bug" },
                new JObject { ["key"] = "description", ["value"] = "broken" },
                new JObject { ["key"] = "attach", ["value"] = "1", ["check"] = true }
            }
        };
        Assert.True(ForskReports.EnsureEmailField(card));
        Assert.Equal(new[] { "type", "description", "email", "attach" }, card["fields"]!.Select(f => f["key"]!.ToString()));
        Assert.Equal("", card["fields"]![2]!["value"]!.ToString());
        Assert.False(ForskReports.EnsureEmailField(card));
        Assert.Equal(4, card["fields"]!.Count());
    }

    [Fact]
    public void Remember_WritesOnlyAValidAddress_AndReadsItBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forsk-email-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "reply-email");
        try
        {
            Assert.False(ForskReplyEmail.Remember(path, "not-an-email"));
            Assert.False(File.Exists(path));
            Assert.Equal("", ForskReplyEmail.Read(path));
            Assert.True(ForskReplyEmail.Remember(path, "  Ada@Example.com  "));
            Assert.Equal("Ada@Example.com", File.ReadAllText(path).Trim());
            Assert.Equal("Ada@Example.com", ForskReplyEmail.Read(path));
            File.WriteAllText(path, "nope\n");
            Assert.Equal("", ForskReplyEmail.Read(path));
            Assert.False(ForskReplyEmail.Remember(path, ""));
            Assert.Equal("nope", File.ReadAllText(path).Trim());

            var home = ForskReplyEmail.DefaultPath;
            Assert.EndsWith(Path.Combine(".forsk", "reply-email"), home);
            Assert.DoesNotContain(".3dm", home);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheWindow_PrefillsFromTheFile_AndHoldsSendUntilTheEmailIsThere()
    {
        var window = Source("ForskWindowActions.cs");
        Assert.Contains("ForskReplyEmail.Read(ForskReplyEmail.DefaultPath)", window);
        Assert.Contains("ForskReplyEmail.Remember(ForskReplyEmail.DefaultPath", window);
        var answer = window.Substring(window.IndexOf("void Answer(", StringComparison.Ordinal));
        answer = answer.Substring(0, answer.IndexOf("void Send(", StringComparison.Ordinal));
        var hold = answer.IndexOf("ForskSupport.Hold", StringComparison.Ordinal);
        var close = answer.IndexOf("thread.Answer(", StringComparison.Ordinal);
        Assert.True(hold >= 0 && close > hold);
        Assert.Contains("EnsureEmailField", answer);
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
