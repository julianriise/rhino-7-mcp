using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// POST https://forsk.app/api/support with a 15 second cap. No auth header.
    /// Runs on a background thread: the UI thread never blocks on it.
    /// </summary>
    public sealed class ForskSupportHttp : ISupportPost
    {
        public const int TimeoutSeconds = 15;
        public static readonly ForskSupportHttp Shared = new ForskSupportHttp();

        readonly HttpClient _http;

        public ForskSupportHttp()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
            }
            catch
            {
                // Rhino's Mono already speaks TLS 1.2.
            }
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        }

        public SupportHttpResult Post(string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ForskSupport.Endpoint);
            request.Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Forsk");
            try
            {
                var response = _http.SendAsync(request).ConfigureAwait(false).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                string retry = null;
                if (response.Headers.TryGetValues("Retry-After", out var values))
                    retry = values.FirstOrDefault();
                return new SupportHttpResult
                {
                    Status = (int)response.StatusCode,
                    Body = body,
                    RetryAfter = retry
                };
            }
            catch (Exception)
            {
                return new SupportHttpResult { Network = true };
            }
        }

        /// <summary>The outbox, oldest first, off the UI thread. A miss leaves the files for the next Send.</summary>
        public static void RetryOnStartup()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ForskSupportSend.Flush(Shared, ForskOutbox.Shared, DateTimeOffset.UtcNow, "en");
                }
                catch (Exception)
                {
                    // The files stay. The next Send tries again.
                }
            });
        }
    }
}
