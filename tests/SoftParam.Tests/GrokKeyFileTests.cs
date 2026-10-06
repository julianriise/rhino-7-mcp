using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The user's own Grok key in ~/.forsk/grok.env: one FORSK_GROK_API_KEY line
/// among the file's other lines. Pasted, saved, changed and removed from the
/// window's Settings menu.
/// </summary>
public class GrokKeyFileTests
{
    [Fact]
    public void Read_TakesTheNamedLine_ThenABareLine()
    {
        Assert.Equal("xai-one", ForskKeyFile.Read("# mine\nFORSK_GROK_API_KEY=xai-one\n"));
        Assert.Equal("xai-two", ForskKeyFile.Read("export FORSK_GROK_API_KEY=\"xai-two\"\n"));
        Assert.Equal("xai-bare", ForskKeyFile.Read("OTHER=1\nxai-bare\n"));
        Assert.Equal("xai-named", ForskKeyFile.Read("xai-bare\nFORSK_GROK_API_KEY=xai-named\n"));
        Assert.Null(ForskKeyFile.Read("FORSK_GROK_API_KEY=\nOTHER=1\n"));
        Assert.Null(ForskKeyFile.Read(""));
        Assert.Null(ForskKeyFile.Read(null));
    }

    [Fact]
    public void Set_ReplacesTheKeyLineInPlace_AndKeepsTheOtherLines()
    {
        Assert.Equal("FORSK_GROK_API_KEY=xai-new\n", ForskKeyFile.Set("", "xai-new"));
        Assert.Equal("FORSK_GROK_API_KEY=xai-new\n", ForskKeyFile.Set(null, "xai-new"));
        Assert.Equal("# mine\nFORSK_GROK_API_KEY=xai-new\nOTHER=1\n",
            ForskKeyFile.Set("# mine\nFORSK_GROK_API_KEY=xai-old\nOTHER=1\n", "xai-new"));
        Assert.Equal("OTHER=1\nFORSK_GROK_API_KEY=xai-new\n", ForskKeyFile.Set("OTHER=1", "xai-new"));
        // A bare key and a second key line are the same key: one line is left.
        Assert.Equal("FORSK_GROK_API_KEY=xai-new\nOTHER=1\n",
            ForskKeyFile.Set("xai-bare\nOTHER=1\nexport FORSK_GROK_API_KEY=xai-old\n", "xai-new"));
    }

    [Fact]
    public void Unset_RemovesEveryKeyLine_AndKeepsTheOtherLines()
    {
        Assert.Equal("# mine\nOTHER=1\n", ForskKeyFile.Unset("# mine\nFORSK_GROK_API_KEY=xai-old\nOTHER=1\n"));
        Assert.Equal("OTHER=1\n", ForskKeyFile.Unset("xai-bare\nOTHER=1\nexport FORSK_GROK_API_KEY=xai-old\n"));
        Assert.Equal("", ForskKeyFile.Unset("FORSK_GROK_API_KEY=xai-old\n"));
        Assert.Equal("", ForskKeyFile.Unset(""));
        Assert.Null(ForskKeyFile.Read(ForskKeyFile.Unset("xai-bare\nFORSK_GROK_API_KEY=xai-old\n")));
    }

    [Fact]
    public void Tail_IsTheLastFourCharacters()
    {
        Assert.Equal("9xYz", ForskKeyFile.Tail("xai-abcdefgh9xYz"));
        Assert.Equal("ab", ForskKeyFile.Tail("ab"));
        Assert.Equal("", ForskKeyFile.Tail(null));
    }

    [Fact]
    public void Clean_TrimsThePaste_AndRefusesAnEmptyOrSpacedKey()
    {
        Assert.Equal("xai-abc", ForskKeyFile.Clean("  xai-abc \n", out var reason));
        Assert.Null(reason);
        Assert.Null(ForskKeyFile.Clean("   ", out reason));
        Assert.Equal("Paste a key first.", reason);
        Assert.Null(ForskKeyFile.Clean(null, out reason));
        Assert.Equal("Paste a key first.", reason);
        Assert.Null(ForskKeyFile.Clean("xai abc", out reason));
        Assert.Equal("A key has no spaces. Paste it again.", reason);
    }

    [Fact]
    public void SaveAndRemove_WriteTheFile_OwnerOnly_AndKeepItsOtherLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forsk-key-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, ".forsk", "grok.env");
        try
        {
            Assert.Null(ForskKeyFile.Load(path));
            Assert.False(ForskKeyFile.Stored(path));
            ForskKeyFile.Save(path, "xai-first");
            Assert.Equal("FORSK_GROK_API_KEY=xai-first\n", File.ReadAllText(path));
            Assert.True(ForskKeyFile.Stored(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            File.WriteAllText(path, "# mine\nFORSK_GROK_API_KEY=xai-first\nOTHER=1\n");
            ForskKeyFile.Save(path, "xai-second");
            Assert.Equal("# mine\nFORSK_GROK_API_KEY=xai-second\nOTHER=1\n", File.ReadAllText(path));
            Assert.Equal("xai-second", ForskKeyFile.Load(path));

            ForskKeyFile.Remove(path);
            Assert.Equal("# mine\nOTHER=1\n", File.ReadAllText(path));
            Assert.False(ForskKeyFile.Stored(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
