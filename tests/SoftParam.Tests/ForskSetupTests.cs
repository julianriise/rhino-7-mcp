using System.Diagnostics;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Settings → Set up Forsk without Terminal: uv's release for this Mac's
/// processor, checked against its published .sha256, unpacked into a bin
/// folder and run once. The downloads are fakes served from a temp folder,
/// so nothing here touches the network, ~/.forsk or ~/.local.
/// </summary>
public class ForskSetupTests
{
    const string Version = "uv 0.0.0-test (fake 2026-10-06)";

    [Fact]
    public void Target_IsUvsBuildName_ForAppleSiliconAndIntel()
    {
        Assert.Equal("aarch64-apple-darwin", ForskUvInstall.Target("arm64"));
        Assert.Equal("aarch64-apple-darwin", ForskUvInstall.Target("arm64\n"));
        Assert.Equal("aarch64-apple-darwin", ForskUvInstall.Target("aarch64"));
        Assert.Equal("x86_64-apple-darwin", ForskUvInstall.Target("x86_64"));
        Assert.Null(ForskUvInstall.Target("i386"));
        Assert.Null(ForskUvInstall.Target(""));
        Assert.Null(ForskUvInstall.Target(null));
    }

    [Fact]
    public void Urls_AreTheLatestReleaseAndItsChecksum()
    {
        Assert.Equal("https://github.com/astral-sh/uv/releases/latest/download/uv-aarch64-apple-darwin.tar.gz",
            ForskUvInstall.Url("aarch64-apple-darwin"));
        Assert.Equal("https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-apple-darwin.tar.gz.sha256",
            ForskUvInstall.ShaUrl("x86_64-apple-darwin"));
    }

    /// <summary>The published file is one sha256sum line (read from the release, 2026-10-06).</summary>
    [Fact]
    public void ParseSha_ReadsTheHex_ForThisArchiveOnly()
    {
        const string hex = "50487ae565ccd96e499056b4674d438f4c53170202617b4c759defe0c6a1b544";
        const string archive = "uv-aarch64-apple-darwin.tar.gz";
        Assert.Equal(hex, ForskUvInstall.ParseSha(hex + "  uv-aarch64-apple-darwin.tar.gz\n", archive));
        Assert.Equal(hex, ForskUvInstall.ParseSha(hex.ToUpperInvariant() + " *uv-aarch64-apple-darwin.tar.gz", archive));
        Assert.Equal(hex, ForskUvInstall.ParseSha("\n" + hex + "\n", archive));
        Assert.Null(ForskUvInstall.ParseSha(hex + "  uv-x86_64-apple-darwin.tar.gz\n", archive));
        Assert.Null(ForskUvInstall.ParseSha(hex.Substring(1) + "  " + archive, archive));
        Assert.Null(ForskUvInstall.ParseSha("<html>Not Found</html>", archive));
        Assert.Null(ForskUvInstall.ParseSha("", archive));
        Assert.Null(ForskUvInstall.ParseSha(null, archive));
    }

    [Fact]
    public void SetupState_StartsStepsAndEnds_AndASecondStartChangesNothing()
    {
        var none = SetupState.From(uvFound: false);
        Assert.Equal(SetupPhase.NotSetUp, none.Phase);
        Assert.True(none.NeedsAction);
        Assert.Equal(SetupPhase.Ready, SetupState.From(uvFound: true).Phase);

        var running = none.Start();
        Assert.Equal(SetupPhase.Running, running.Phase);
        Assert.Equal("Downloading uv", running.Text);
        Assert.Same(running, running.Start());
        Assert.False(running.NeedsAction);
        var step = running.Step("Unpacking uv");
        Assert.Equal("Unpacking uv", step.Text);
        // A look at the disk mid-install keeps the install's state.
        Assert.Same(step, step.Seen(uvFound: true));
        Assert.Equal(SetupPhase.Ready, step.Done().Phase);

        var failed = step.Fail("No internet connection: try again when online.");
        Assert.Equal(SetupPhase.Failed, failed.Phase);
        Assert.Equal("No internet connection: try again when online.", failed.Text);
        Assert.True(failed.NeedsAction);
        Assert.Same(failed, failed.Seen(uvFound: false));
        Assert.Same(failed, failed.Done());
        Assert.Same(failed, failed.Step("Unpacking uv"));
        Assert.Equal(SetupPhase.Ready, failed.Seen(uvFound: true).Phase);
        Assert.Equal(SetupPhase.Running, failed.Start().Phase);
        // uv removed by hand: the row says so again.
        Assert.Equal(SetupPhase.NotSetUp, SetupState.From(true).Seen(uvFound: false).Phase);
    }

    [Fact]
    public void Rows_SayEachState_InOneLine()
    {
        Assert.Equal("Chat · Needs your Grok key", ForskSetup.ChatRow(null));
        Assert.Equal("Chat · Ready · key ending in 9xYz", ForskSetup.ChatRow("xai-abcdefgh9xYz"));
        Assert.Equal("Daylight & AI detection · Not set up", ForskSetup.ToolsRow(SetupState.From(false)));
        Assert.Equal("Daylight & AI detection · Ready", ForskSetup.ToolsRow(SetupState.From(true)));
        Assert.Equal("Daylight & AI detection · Setting up… Installing Python, about a minute",
            ForskSetup.ToolsRow(SetupState.From(false).Start().Step("Installing Python, about a minute")));
        Assert.Equal("Daylight & AI detection · Failed: The download did not match its checksum: try again.",
            ForskSetup.ToolsRow(SetupState.From(false).Start().Fail("The download did not match its checksum: try again.")));
    }

    /// <summary>The installed copy wins over PATH, so the card's uv is the one daylight runs. UV still overrides both.</summary>
    [Fact]
    public void Find_TakesForskBin_BeforePath()
    {
        using var temp = new Temp();
        var home = Directory.CreateDirectory(Path.Combine(temp.Dir, "home")).FullName;
        var path = Directory.CreateDirectory(Path.Combine(temp.Dir, "path")).FullName;
        File.WriteAllText(Path.Combine(path, "uv"), "");
        Assert.Equal(Path.Combine(path, "uv"), ForskUv.Find(null, home, path));
        var forsk = Directory.CreateDirectory(Path.Combine(home, ".forsk", "bin")).FullName;
        File.WriteAllText(Path.Combine(forsk, "uv"), "");
        Assert.Equal(Path.Combine(forsk, "uv"), ForskUv.Find(null, home, path));
        var set = Path.Combine(temp.Dir, "own-uv");
        File.WriteAllText(set, "");
        Assert.Equal(set, ForskUv.Find(set, home, path));
    }

    [Fact]
    public void Install_ChecksUnpacksAndRunsUv_StepByStep_AndLeavesOnlyBin()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        using var temp = new Temp();
        var served = Serve(temp, "exit 0");
        var steps = new List<string>();
        var bin = Path.Combine(temp.Dir, "forsk", "bin");

        var installed = ForskUvInstall.Install(Options(temp, bin, served), steps.Add);

        Assert.Equal(new[] { "Downloading uv", "Checking the download", "Unpacking uv", "Checking uv runs", "Installing Python, about a minute" }, steps);
        Assert.Equal(Version, installed.Version);
        Assert.Equal(Path.Combine(bin, "uv"), installed.Uv);
        Assert.Equal(ForskUvInstall.Sha256(served[ForskUvInstall.Url("aarch64-apple-darwin")]), installed.Sha);
        Assert.Equal(new[] { "uv", "uvx" }, Directory.GetFiles(bin).Select(Path.GetFileName).OrderBy(n => n));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, File.GetUnixFileMode(Path.Combine(bin, "uv")));
        // The download folder beside bin is gone.
        Assert.Equal(new[] { "bin" }, Directory.GetDirectories(Path.Combine(temp.Dir, "forsk")).Select(Path.GetFileName));
        // The probe's own Python folder reaches uv's runs.
        Assert.Equal("python install --no-bin", File.ReadAllText(Path.Combine(temp.Dir, "python-args")).Trim());
    }

    [Fact]
    public void Install_StopsOnAWrongChecksum_WithNoUvInBin()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        using var temp = new Temp();
        var served = Serve(temp, "exit 0");
        File.WriteAllText(served[ForskUvInstall.ShaUrl("aarch64-apple-darwin")], new string('0', 64) + "  uv-aarch64-apple-darwin.tar.gz\n");
        var bin = Path.Combine(temp.Dir, "forsk", "bin");

        var failure = Assert.Throws<ForskUvInstall.Failure>(() => ForskUvInstall.Install(Options(temp, bin, served), null));

        Assert.Equal("The download did not match its checksum: try again.", failure.Message);
        Assert.False(File.Exists(Path.Combine(bin, "uv")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(temp.Dir, "forsk")));
    }

    [Fact]
    public void Install_Offline_SaysSoInOneLine()
    {
        using var temp = new Temp();
        var options = Options(temp, Path.Combine(temp.Dir, "bin"), new Dictionary<string, string>());
        options.Fetch = (url, file) => throw new ForskUvInstall.Failure("No internet connection: try again when online.");

        var failure = Assert.Throws<ForskUvInstall.Failure>(() => ForskUvInstall.Install(options, null));

        Assert.Equal("No internet connection: try again when online.", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(temp.Dir, "bin")));
        Assert.Empty(Directory.GetDirectories(temp.Dir));
    }

    [Fact]
    public void Install_NamesTheMachine_WhenUvHasNoBuildForIt()
    {
        using var temp = new Temp();
        var options = Options(temp, Path.Combine(temp.Dir, "bin"), new Dictionary<string, string>());
        options.Machine = "ppc";
        Assert.Equal("This Mac's processor (ppc) has no uv build.",
            Assert.Throws<ForskUvInstall.Failure>(() => ForskUvInstall.Install(options, null)).Message);
    }

    /// <summary>uv installed but Python did not: the row fails with uv's last line, and Try again starts over.</summary>
    [Fact]
    public void Install_PythonFailing_IsAOneLineFailure()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        using var temp = new Temp();
        var served = Serve(temp, "echo 'error: no network for Python' >&2; exit 2");

        var failure = Assert.Throws<ForskUvInstall.Failure>(() => ForskUvInstall.Install(Options(temp, Path.Combine(temp.Dir, "bin"), served), null));

        Assert.Equal("Python did not install: error: no network for Python", failure.Message);
    }

    static ForskUvInstall.Options Options(Temp temp, string bin, Dictionary<string, string> served) => new ForskUvInstall.Options
    {
        Bin = bin,
        Machine = "arm64",
        Fetch = (url, file) => File.Copy(served[url], file),
        Env = new Dictionary<string, string> { ["FAKE_UV_LOG"] = Path.Combine(temp.Dir, "python-args") }
    };

    /// <summary>
    /// A fake release in temp: uv-aarch64-apple-darwin/{uv,uvx} as a tarball,
    /// and its sha256sum line. The fake uv prints a version, and on python
    /// install logs its arguments and runs python.
    /// </summary>
    static Dictionary<string, string> Serve(Temp temp, string python)
    {
        var release = Directory.CreateDirectory(Path.Combine(temp.Dir, "release", "uv-aarch64-apple-darwin")).FullName;
        File.WriteAllText(Path.Combine(release, "uv"),
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo '" + Version + "'; exit 0; fi\n"
            + "echo \"$@\" > \"$FAKE_UV_LOG\"\n" + python + "\n");
        File.WriteAllText(Path.Combine(release, "uvx"), "#!/bin/sh\nexit 0\n");
        var archive = Path.Combine(temp.Dir, "release", "uv-aarch64-apple-darwin.tar.gz");
        Run("/usr/bin/tar", "-czf " + ForskUv.Quote(archive) + " -C " + ForskUv.Quote(Path.GetDirectoryName(release)!) + " uv-aarch64-apple-darwin");
        var sha = archive + ".sha256";
        File.WriteAllText(sha, ForskUvInstall.Sha256(archive) + "  uv-aarch64-apple-darwin.tar.gz\n");
        return new Dictionary<string, string>
        {
            [ForskUvInstall.Url("aarch64-apple-darwin")] = archive,
            [ForskUvInstall.ShaUrl("aarch64-apple-darwin")] = sha
        };
    }

    static void Run(string exe, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    sealed class Temp : IDisposable
    {
        public readonly string Dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "forsk-setup-test-" + Guid.NewGuid().ToString("n"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); }
            catch (IOException) { }
        }
    }
}
