using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Settings → Set up Forsk's daylight and AI detection row: uv from its
/// GitHub release, checked against the release's own .sha256, into
/// ~/.forsk/bin, then a warm-up Python so the first daylight run is not the
/// slow one. No Terminal. The download and the unpacking happen in a folder
/// beside bin, so bin never holds a half-written or unchecked uv. No
/// RhinoCommon, so it tests headless with a fake download.
/// </summary>
public static class ForskUvInstall
{
    public const string Releases = "https://github.com/astral-sh/uv/releases/latest/download/";
    public const int DownloadSeconds = 300;
    const int ToolMs = 60 * 1000;
    const int PythonMs = 10 * 60 * 1000;

    /// <summary>The warm-up: Python into uv's own folder, so the first daylight run does not download it.</summary>
    public const string PythonArgs = "python install --no-bin";

    /// <summary>Why the install stopped, as the one line the card shows.</summary>
    public sealed class Failure : Exception
    {
        public Failure(string reason) : base(reason) { }
    }

    public sealed class Options
    {
        /// <summary>Where uv and uvx go. The probe passes a temporary folder.</summary>
        public string Bin = ForskUv.ForskBin();
        /// <summary>uname -m. Null asks this Mac.</summary>
        public string Machine;
        /// <summary>Writes a URL's bytes to a file, or throws a Failure. The tests pass a fake.</summary>
        public Action<string, string> Fetch = HttpFetch;
        /// <summary>Added to uv's own runs (the probe's temporary Python folder).</summary>
        public Dictionary<string, string> Env = new Dictionary<string, string>();
        /// <summary>Run PythonArgs after uv.</summary>
        public bool Python = true;
    }

    public sealed class Installed
    {
        public string Uv;
        /// <summary>uv --version's line.</summary>
        public string Version;
        /// <summary>The archive's SHA-256, equal to the published one.</summary>
        public string Sha;
    }

    /// <summary>uv's build name for uname -m: arm64 is aarch64-apple-darwin, x86_64 the Intel one. Null for anything else.</summary>
    public static string Target(string machine)
    {
        switch ((machine ?? "").Trim().ToLowerInvariant())
        {
            case "arm64":
            case "aarch64":
                return "aarch64-apple-darwin";
            case "x86_64":
            case "amd64":
                return "x86_64-apple-darwin";
            default:
                return null;
        }
    }

    public static string Archive(string target) => "uv-" + target + ".tar.gz";

    public static string Url(string target) => Releases + Archive(target);

    /// <summary>The checksum file published next to the archive.</summary>
    public static string ShaUrl(string target) => Url(target) + ".sha256";

    /// <summary>
    /// The hex SHA-256 in a sha256sum line ("hex  name", the name maybe
    /// starred), lower case. Null when the line has no 64-digit hex, or names
    /// another file than archive.
    /// </summary>
    public static string ParseSha(string text, string archive)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) return null;
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var hex = parts[0].ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
        if (parts.Length > 1 && parts[1].TrimStart('*') != archive) return null;
        return hex;
    }

    /// <summary>A file's SHA-256 as lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// uv into options.Bin, step naming each stage as it starts. Throws a
    /// Failure with the one-line reason; nothing unchecked is left in Bin.
    /// </summary>
    public static Installed Install(Options options, Action<string> step)
    {
        options = options ?? new Options();
        step = step ?? (_ => { });
        var machine = options.Machine ?? Machine();
        var target = Target(machine);
        if (target == null) throw new Failure(ForskText.Format("setup.fail.arch", "machine", machine));
        var stage = Path.GetFullPath(options.Bin).TrimEnd(Path.DirectorySeparatorChar) + ".part-" + Guid.NewGuid().ToString("n");
        try
        {
            Directory.CreateDirectory(stage);
            step(ForskText.Get("setup.step.download"));
            var archive = Path.Combine(stage, Archive(target));
            options.Fetch(ShaUrl(target), archive + ".sha256");
            var published = ParseSha(File.ReadAllText(archive + ".sha256"), Archive(target))
                ?? throw new Failure(ForskText.Get("setup.fail.shafile"));
            options.Fetch(Url(target), archive);

            step(ForskText.Get("setup.step.check"));
            var sha = Sha256(archive);
            if (sha != published) throw new Failure(ForskText.Get("setup.fail.checksum"));

            step(ForskText.Get("setup.step.unpack"));
            var tar = ForskUv.Run("/usr/bin/tar", "-xzf " + ForskUv.Quote(archive) + " -C " + ForskUv.Quote(stage), stage, "Unpacking uv", ToolMs);
            if (tar.Code != 0) throw new Failure(ForskText.Format("setup.fail.unpack", "reason", ForskUv.LastLine(tar.Stderr)));
            var unpacked = Path.Combine(stage, "uv-" + target);
            if (!File.Exists(Path.Combine(unpacked, "uv")))
                throw new Failure(ForskText.Format("setup.fail.unpack", "reason", "no uv in the download"));
            Directory.CreateDirectory(options.Bin);
            foreach (var name in new[] { "uv", "uvx" })
            {
                var from = Path.Combine(unpacked, name);
                if (!File.Exists(from)) continue;
                ForskUv.Run("/bin/chmod", "755 " + ForskUv.Quote(from), stage, "chmod", ToolMs);
                // A rename on the same disk: bin has the old uv or the new one, never part of one.
                var to = Path.Combine(options.Bin, name);
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }

            var uv = Path.Combine(options.Bin, "uv");
            step(ForskText.Get("setup.step.run"));
            var version = ForskUv.Run(uv, "--version", options.Bin, "uv", ToolMs, options.Env);
            if (version.Code != 0) throw new Failure(ForskText.Format("setup.fail.run", "reason", ForskUv.LastLine(version.Stderr)));
            if (options.Python)
            {
                step(ForskText.Get("setup.step.python"));
                // uv's own Python only: --no-bin puts no python3.x into the user's ~/.local/bin.
                var python = ForskUv.Run(uv, PythonArgs, options.Bin, "Installing Python", PythonMs, options.Env);
                if (python.Code != 0) throw new Failure(ForskText.Format("setup.fail.python", "reason", ForskUv.LastLine(python.Stderr)));
            }
            return new Installed { Uv = uv, Version = ForskUv.LastLine(version.Stdout), Sha = sha };
        }
        catch (Failure)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new Failure(ForskUv.LastLine(e.Message));
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception) { /* a temp folder beside bin; the next install makes its own */ }
        }
    }

    /// <summary>uname -m: arm64 on Apple silicon, x86_64 on Intel and under Rosetta. Empty when it does not run.</summary>
    public static string Machine()
    {
        try
        {
            return ForskUv.Run("/usr/bin/uname", "-m", "/", "uname", 5000).Stdout.Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // Rhino's Mono already speaks TLS 1.2.
        }
        return new HttpClient { Timeout = TimeSpan.FromSeconds(DownloadSeconds) };
    });

    /// <summary>The URL's bytes into file. Offline, a time-out and an HTTP error are each a Failure with their own line.</summary>
    public static void HttpFetch(string url, string file)
    {
        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            response = Http.Value.GetAsync(url).GetAwaiter().GetResult();
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new Failure(ForskText.Format("setup.fail.http", "code", ((int)response.StatusCode).ToString()));
                bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }
        catch (TaskCanceledException)
        {
            throw new Failure(ForskText.Format("setup.fail.timeout", "s", DownloadSeconds.ToString()));
        }
        catch (Failure)
        {
            throw;
        }
        catch (Exception)
        {
            // No route, no DNS, or the connection dropped: Rhino cannot reach GitHub now.
            throw new Failure(ForskText.Get("setup.fail.offline"));
        }
        File.WriteAllBytes(file, bytes);
    }
}
