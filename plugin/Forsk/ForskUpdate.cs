using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Settings → Update available. Rhino 7 never tells anyone a package has a
    /// new version, so Forsk asks forsk.app once per Rhino session, off the UI
    /// thread. A newer version dots the gear and adds one Settings row. No
    /// answer, a slow answer or an odd answer shows nothing. The parsing and the
    /// comparison are pure, so they test headless.
    /// </summary>
    public static class ForskUpdate
    {
        /// <summary>The Settings menu row and the card's kind.</summary>
        public const string MenuId = "update.available";
        public const string Endpoint = "https://forsk.app/api/plugin/latest";
        /// <summary>The docs' Update steps, for the card's How to update pill.</summary>
        public const string HowTo = "https://docs.forsk.app/install#update";
        public const int TimeoutSeconds = 10;

        static string _latest;
        static int _started;

        /// <summary>The newer version forsk.app named this session, or null.</summary>
        public static string Latest => Volatile.Read(ref _latest);

        /// <summary>This plug-in's own version, as the support report sends it: 1.0.0.</summary>
        public static string Current
        {
            get
            {
                var assembly = typeof(ForskUpdate).Assembly;
                var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                return assembly.GetName().Version?.ToString() ?? "";
            }
        }

        /// <summary>The version in forsk.app's answer ({ok:true, version:"1.0.1", …}), or null.</summary>
        public static string Version(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var body = JObject.Parse(json);
                if (body["ok"]?.Type != JTokenType.Boolean || !body["ok"].Value<bool>()) return null;
                var version = body["version"]?.Type == JTokenType.String ? body["version"].ToString().Trim() : null;
                return Parse(version) == null ? null : version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when latest is a higher version than current. Anything that doesn't read as a version is not newer.</summary>
        public static bool IsNewer(string latest, string current)
        {
            var a = Parse(latest);
            var b = Parse(current);
            return a != null && b != null && a > b;
        }

        /// <summary>The newer version in the answer, or null when there is none to show.</summary>
        public static string Newer(string json, string current)
        {
            var version = Version(json);
            return IsNewer(version, current) ? version : null;
        }

        /// <summary>1.0.1 and 1.0.1+abc read as 1.0.1. Pre-release tags and junk do not read.</summary>
        static System.Version Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);
            return System.Version.TryParse(text.Trim(), out var v) ? v : null;
        }

        /// <summary>The one ask per Rhino session, off the UI thread. The window's poll sees the answer.</summary>
        public static void CheckOnStartup()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var newer = Newer(Fetch(), Current);
                    if (newer != null) Volatile.Write(ref _latest, newer);
                }
                catch (Exception)
                {
                    // Offline or forsk.app down: no row, no dot.
                }
            });
        }

        static string Fetch()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { /* Rhino's Mono already speaks TLS 1.2. */ }
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                request.Headers.TryAddWithoutValidation("User-Agent", "Forsk/" + Current);
                var response = http.SendAsync(request).ConfigureAwait(false).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return null;
                return response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }
    }
}
