using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The dashboard calls behind Connect Rhino (see ForskAccount), with a 15
    /// second cap. Background threads only: the UI thread never blocks on it.
    /// Status 0 means the network failed.
    /// </summary>
    public static class ForskAccountHttp
    {
        static readonly HttpClient Http = Create();

        static HttpClient Create()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { /* Rhino's Mono already speaks TLS 1.2. */ }
            return new HttpClient { Timeout = TimeSpan.FromSeconds(ForskSupportHttp.TimeoutSeconds) };
        }

        public static int Post(string url, string json, out string body) => Send(HttpMethod.Post, url, json, null, null, out body);

        public static int Licence(string token, string forskVersion, out string body) =>
            Send(HttpMethod.Get, ForskAccount.LicenceUrl, null, token, forskVersion, out body);

        static int Send(HttpMethod method, string url, string json, string token, string forskVersion, out string body)
        {
            body = null;
            var request = new HttpRequestMessage(method, url);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Forsk");
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(forskVersion)) request.Headers.TryAddWithoutValidation("X-Forsk-Version", forskVersion);
            try
            {
                var response = Http.SendAsync(request).ConfigureAwait(false).GetAwaiter().GetResult();
                body = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                return (int)response.StatusCode;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
