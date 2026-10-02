using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The page channel: the window's page posts each action to a local HTTP
    /// endpoint on 127.0.0.1 with a per-page token, and C# answers by running
    /// script in the page. Every action carries a sequence number. WebKit can
    /// deliver concurrent posts out of order, so actions are handed on strictly
    /// in sequence, once each: a gap waits, a repeat is dropped. Nothing is cut
    /// short below <see cref="MaxBody"/>. No RhinoCommon, so it tests headless.
    /// </summary>
    public sealed class PageChannel : IDisposable
    {
        /// <summary>The largest action body accepted, in bytes.</summary>
        public const int MaxBody = 256 * 1024;

        readonly Action<JObject> _deliver;
        readonly object _gate = new object();
        readonly SortedDictionary<long, JObject> _waiting = new SortedDictionary<long, JObject>();
        HttpListener _listener;
        long _delivered;
        string _token;

        /// <param name="deliver">Called once per action, in sequence order, under the channel's lock. Keep it short: post to the UI thread.</param>
        public PageChannel(Action<JObject> deliver)
        {
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
            _token = NewToken();
        }

        public string Token
        {
            get { lock (_gate) return _token; }
        }

        public int Port { get; private set; }

        /// <summary>The page's base URL. The page posts to "action" under it, so the post is same-origin.</summary>
        public string Origin => "http://127.0.0.1:" + Port + "/";

        public void Start()
        {
            if (_listener != null) return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    continue;
                }
                _listener = listener;
                Port = port;
                listener.BeginGetContext(OnContext, listener);
                return;
            }
            throw new InvalidOperationException("The page channel found no free port on 127.0.0.1.");
        }

        /// <summary>A new page: a new token, and the numbering starts again at 1.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _token = NewToken();
                _delivered = 0;
                _waiting.Clear();
            }
        }

        /// <summary>
        /// One action with its sequence number. Delivers it, and any that were
        /// waiting on it, in order. False for a number already delivered or
        /// already waiting: a repeat after a lost answer.
        /// </summary>
        public bool Accept(long seq, JObject message)
        {
            if (seq < 1 || message == null) return false;
            lock (_gate)
            {
                if (seq <= _delivered || _waiting.ContainsKey(seq)) return false;
                _waiting[seq] = message;
                while (_waiting.TryGetValue(_delivered + 1, out var next))
                {
                    _waiting.Remove(_delivered + 1);
                    _delivered++;
                    _deliver(next);
                }
                return true;
            }
        }

        /// <summary>The last sequence number handed on.</summary>
        public long Delivered
        {
            get { lock (_gate) return _delivered; }
        }

        /// <summary>
        /// Close only. Stop then Close removes the listener twice, and the second
        /// removal binds the port again: when another process has taken it, that
        /// throws "Address already in use". Closing never throws.
        /// </summary>
        public void Dispose()
        {
            var listener = _listener;
            _listener = null;
            if (listener == null) return;
            try
            {
                listener.Close();
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                // Already closed, or the port went away first. Nothing is left listening.
            }
        }

        void OnContext(IAsyncResult result)
        {
            var listener = (HttpListener)result.AsyncState;
            HttpListenerContext context;
            try
            {
                context = listener.EndGetContext(result);
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                return;
            }
            try
            {
                listener.BeginGetContext(OnContext, listener);
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                // Stopped while this request was in flight. Answer it anyway.
            }
            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                try { context.Response.Abort(); }
                catch (Exception) { }
            }
        }

        void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            response.AddHeader("Access-Control-Allow-Origin", "*");
            if (request.HttpMethod == "OPTIONS")
            {
                response.AddHeader("Access-Control-Allow-Methods", "POST");
                Answer(response, 204, null);
                return;
            }
            if (request.HttpMethod != "POST" || request.Url == null || request.Url.AbsolutePath != "/action")
            {
                Answer(response, 404, new JObject { ["ok"] = false, ["error"] = "not found" });
                return;
            }
            if (request.ContentLength64 > MaxBody)
            {
                Answer(response, 413, new JObject { ["ok"] = false, ["error"] = "too large" });
                return;
            }
            var body = ReadBody(request.InputStream);
            if (body == null)
            {
                Answer(response, 413, new JObject { ["ok"] = false, ["error"] = "too large" });
                return;
            }
            JObject message;
            try
            {
                message = JObject.Parse(body);
            }
            catch (JsonException)
            {
                Answer(response, 400, new JObject { ["ok"] = false, ["error"] = "not json" });
                return;
            }
            if (!SameToken(message["t"]?.ToString(), Token))
            {
                Answer(response, 403, new JObject { ["ok"] = false, ["error"] = "token" });
                return;
            }
            var seq = message["seq"]?.Type == JTokenType.Integer ? message["seq"].Value<long>() : 0;
            if (seq < 1)
            {
                Answer(response, 400, new JObject { ["ok"] = false, ["error"] = "seq" });
                return;
            }
            message.Remove("t");
            var fresh = Accept(seq, message);
            Answer(response, 200, new JObject { ["ok"] = true, ["seq"] = seq, ["repeat"] = !fresh });
        }

        static string ReadBody(Stream input)
        {
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > MaxBody) return null;
                }
                return Encoding.UTF8.GetString(copy.ToArray());
            }
        }

        static void Answer(HttpListenerResponse response, int status, JObject body)
        {
            response.StatusCode = status;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            response.Close();
        }

        static bool SameToken(string given, string expected)
        {
            if (given == null || expected == null || given.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++)
                diff |= given[i] ^ expected[i];
            return diff == 0;
        }

        static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }
}
