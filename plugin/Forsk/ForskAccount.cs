using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    public enum AccountPhase
    {
        NotConnected,
        Waiting,
        Connected,
        Failed
    }

    /// <summary>
    /// Settings → Set up Forsk's Account row: Connect Rhino links this Mac to
    /// the user's dashboard.forsk.app account, the way Apple TV signs in.
    /// Rhino shows a short code, the browser confirms it, and the plugin keeps
    /// the device token in ~/.forsk/account.json (600). Nothing is enforced:
    /// Forsk works the same connected or not. No RhinoCommon, so it tests headless.
    ///
    ///   NotConnected / Failed --Start--> Waiting(code) --Connected--> Connected
    ///   NotConnected / Waiting --Fail--> Failed (Connected stays)
    ///   Connected --Disconnected--> NotConnected
    /// </summary>
    public sealed class AccountState
    {
        public readonly AccountPhase Phase;
        /// <summary>Waiting: the code on screen. Failed: the reason.</summary>
        public readonly string Text;
        public readonly string Email;
        public readonly string Plan;

        AccountState(AccountPhase phase, string text = null, string email = null, string plan = null)
        {
            Phase = phase;
            Text = text;
            Email = email;
            Plan = plan;
        }

        /// <summary>What the account file says at start-up: Connected when it holds a token.</summary>
        public static AccountState From(AccountFile file) =>
            file == null ? new AccountState(AccountPhase.NotConnected) : new AccountState(AccountPhase.Connected, null, file.Email, file.Plan);

        public AccountState Start(string code) => new AccountState(AccountPhase.Waiting, code);

        public AccountState Connected(string email, string plan) => new AccountState(AccountPhase.Connected, null, email, plan);

        /// <summary>A failed link. A Mac already connected stays connected.</summary>
        public AccountState Fail(string reason) => Phase == AccountPhase.Connected ? this : new AccountState(AccountPhase.Failed, reason);

        public static AccountState Disconnected() => new AccountState(AccountPhase.NotConnected);

        public bool CanConnect => Phase == AccountPhase.NotConnected || Phase == AccountPhase.Failed;
    }

    /// <summary>What ~/.forsk/account.json holds: the device token and what the dashboard last said.</summary>
    public sealed class AccountFile
    {
        public string Token;
        public string Email;
        public string Plan;

        /// <summary>~/.forsk/account.json</summary>
        public static string DefaultPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".forsk", "account.json");
            }
        }

        /// <summary>The file's contents, or null when there is no file, no token, or it is not JSON.</summary>
        public static AccountFile Parse(string text)
        {
            try
            {
                var o = JObject.Parse(text ?? "");
                var token = o.Value<string>("token");
                if (string.IsNullOrWhiteSpace(token)) return null;
                return new AccountFile { Token = token, Email = o.Value<string>("email") ?? "", Plan = o.Value<string>("plan") ?? "" };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string ToJson() => new JObject { ["token"] = Token, ["email"] = Email ?? "", ["plan"] = Plan ?? "" }.ToString();

        public static AccountFile Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

        /// <summary>Owner-only, like the Grok key.</summary>
        public void Save(string path) => ForskKeyFile.Write(path, ToJson());

        public static void Remove(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The dashboard's answers, read without the network so they test headless.</summary>
    public static class ForskAccount
    {
        public const string Base = "https://dashboard.forsk.app";
        public const string StartUrl = Base + "/api/device/start";
        public const string PollUrl = Base + "/api/device/poll";
        public const string LicenceUrl = Base + "/api/licence";
        /// <summary>The server's codes live 10 minutes; the plugin stops a little after.</summary>
        public static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(11);

        public sealed class Started
        {
            public string Code;
            public string Poll;
            public string Url;
            public int Interval;
        }

        /// <summary>200 from /api/device/start, or null for anything else.</summary>
        public static Started ParseStart(int status, string body)
        {
            var o = Json(status, body);
            var code = o?.Value<string>("code");
            var poll = o?.Value<string>("poll");
            var url = o?.Value<string>("url");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(poll) || !IsDashboardUrl(url)) return null;
            var interval = o.Value<int?>("interval") ?? 3;
            return new Started { Code = code, Poll = poll, Url = url, Interval = Math.Max(2, Math.Min(interval, 30)) };
        }

        /// <summary>Only the dashboard's own Connect page is opened in the browser.</summary>
        public static bool IsDashboardUrl(string url) =>
            url != null && url.StartsWith(Base + "/connect?code=", StringComparison.Ordinal);

        /// <summary>A poll answer: "pending", "expired", or "connected" with the file to save. Null: try again (network, 5xx, 429).</summary>
        public static string ParsePoll(int status, string body, out AccountFile connected)
        {
            connected = null;
            var o = Json(status, body);
            var state = o?.Value<string>("status");
            if (state == "connected")
            {
                connected = new AccountFile { Token = o.Value<string>("token"), Email = o.Value<string>("email") ?? "", Plan = o.Value<string>("plan_label") ?? "" };
                return string.IsNullOrEmpty(connected.Token) ? null : "connected";
            }
            return state == "pending" || state == "expired" ? state : null;
        }

        /// <summary>The licence check: true with the new plan, false when the dashboard disconnected this Mac (401), null when unknown (offline, 5xx).</summary>
        public static bool? ParseLicence(int status, string body, out string email, out string plan)
        {
            email = plan = null;
            if (status == 401) return false;
            var o = Json(status, body);
            if (o == null) return null;
            email = o.Value<string>("email");
            plan = o.Value<string>("plan_label");
            return true;
        }

        static JObject Json(int status, string body)
        {
            if (status != 200) return null;
            try
            {
                var o = JObject.Parse(body ?? "");
                return o.Value<bool?>("ok") == true ? o : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The body for /api/device/start: the Mac's name and versions, shown on the Connect page.</summary>
        public static string StartBody(string name, string rhino, string forsk) =>
            new JObject { ["name"] = string.IsNullOrWhiteSpace(name) ? "Mac" : name.Trim(), ["rhino"] = rhino ?? "", ["forsk"] = forsk ?? "" }.ToString(Newtonsoft.Json.Formatting.None);

        public static string PollBody(string poll) => new JObject { ["poll"] = poll }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
