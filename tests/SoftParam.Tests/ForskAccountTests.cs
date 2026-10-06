using System.IO;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Connect Rhino: the dashboard's answers (forsk-web src/app/api/device/*,
/// src/app/api/licence) read headless, and the account file. Nothing here
/// touches the network or ~/.forsk.
/// </summary>
public class ForskAccountTests
{
    [Fact]
    public void ParseStart_TakesTheCodeAndOpensOnlyTheDashboardsConnectPage()
    {
        var ok = ForskAccount.ParseStart(200, "{\"ok\":true,\"code\":\"K7M2-QX4P\",\"poll\":\"secret\",\"url\":\"https://dashboard.forsk.app/connect?code=K7M2-QX4P\",\"expires_in\":600,\"interval\":3}");
        Assert.NotNull(ok);
        Assert.Equal("K7M2-QX4P", ok!.Code);
        Assert.Equal("secret", ok.Poll);
        Assert.Equal(3, ok.Interval);
        Assert.Null(ForskAccount.ParseStart(200, "{\"ok\":true,\"code\":\"K7M2-QX4P\",\"poll\":\"s\",\"url\":\"https://evil.example/connect?code=K\"}"));
        Assert.Null(ForskAccount.ParseStart(429, "{\"ok\":false,\"error\":\"rate_limited\"}"));
        Assert.Null(ForskAccount.ParseStart(200, "<html>"));
        Assert.Null(ForskAccount.ParseStart(0, null));
    }

    [Fact]
    public void ParsePoll_PendingExpiredConnected_AndRetriesOnAnythingElse()
    {
        Assert.Equal("pending", ForskAccount.ParsePoll(200, "{\"ok\":true,\"status\":\"pending\"}", out var none));
        Assert.Null(none);
        Assert.Equal("expired", ForskAccount.ParsePoll(200, "{\"ok\":true,\"status\":\"expired\"}", out _));
        Assert.Equal("connected", ForskAccount.ParsePoll(200,
            "{\"ok\":true,\"status\":\"connected\",\"token\":\"tok\",\"email\":\"julian@forsk.app\",\"plan\":\"early_tester\",\"plan_label\":\"Early tester\"}", out var file));
        Assert.Equal("tok", file!.Token);
        Assert.Equal("julian@forsk.app", file.Email);
        Assert.Equal("Early tester", file.Plan);
        Assert.Null(ForskAccount.ParsePoll(200, "{\"ok\":true,\"status\":\"connected\"}", out _));
        Assert.Null(ForskAccount.ParsePoll(503, "", out _));
        Assert.Null(ForskAccount.ParsePoll(0, null, out _));
    }

    [Fact]
    public void ParseLicence_401IsDisconnected_OfflineIsUnknown()
    {
        Assert.True(ForskAccount.ParseLicence(200, "{\"ok\":true,\"email\":\"a@b.c\",\"plan\":\"pro\",\"plan_label\":\"Pro\",\"status\":\"active\",\"ends_at\":null}", out var email, out var plan));
        Assert.Equal("a@b.c", email);
        Assert.Equal("Pro", plan);
        Assert.False(ForskAccount.ParseLicence(401, "{\"ok\":false,\"error\":\"disconnected\"}", out _, out _));
        Assert.Null(ForskAccount.ParseLicence(0, null, out _, out _));
        Assert.Null(ForskAccount.ParseLicence(500, "", out _, out _));
    }

    [Fact]
    public void StartBody_NamesTheMac_AndFallsBackToMac()
    {
        Assert.Equal("{\"name\":\"Julian's MacBook Air\",\"rhino\":\"7.38\",\"forsk\":\"1.1.0\"}", ForskAccount.StartBody(" Julian's MacBook Air ", "7.38", "1.1.0"));
        Assert.Equal("{\"name\":\"Mac\",\"rhino\":\"\",\"forsk\":\"\"}", ForskAccount.StartBody("", null, null));
        Assert.Equal("{\"poll\":\"p\"}", ForskAccount.PollBody("p"));
    }

    [Fact]
    public void AccountState_ConnectsFailsAndDisconnects_AndAFailureKeepsAConnection()
    {
        var none = AccountState.From(null);
        Assert.Equal(AccountPhase.NotConnected, none.Phase);
        Assert.True(none.CanConnect);
        var waiting = none.Start("K7M2-QX4P");
        Assert.Equal(AccountPhase.Waiting, waiting.Phase);
        Assert.False(waiting.CanConnect);
        var failed = waiting.Fail("the code expired, click Connect again.");
        Assert.Equal(AccountPhase.Failed, failed.Phase);
        Assert.True(failed.CanConnect);
        var connected = waiting.Connected("a@b.c", "Early tester");
        Assert.Equal(AccountPhase.Connected, connected.Phase);
        Assert.Same(connected, connected.Fail("offline"));
        Assert.Equal(AccountPhase.NotConnected, AccountState.Disconnected().Phase);
        Assert.Equal("Account · Not connected", ForskSetup.AccountRow(null));
    }

    [Fact]
    public void AccountFile_RoundTrips_OwnerOnly_AndIgnoresJunk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forsk-account-" + System.Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "account.json");
        try
        {
            Assert.Null(AccountFile.Load(path));
            new AccountFile { Token = "tok", Email = "a@b.c", Plan = "Early tester" }.Save(path);
            var back = AccountFile.Load(path)!;
            Assert.Equal("tok", back.Token);
            Assert.Equal("a@b.c", back.Email);
            if (!System.OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            AccountFile.Remove(path);
            Assert.False(File.Exists(path));
            Assert.Null(AccountFile.Parse("{\"email\":\"a@b.c\"}"));
            Assert.Null(AccountFile.Parse("not json"));
            Assert.Null(AccountFile.Parse(null));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
