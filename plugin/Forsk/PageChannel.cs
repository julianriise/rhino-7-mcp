using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    /// <remarks>
    /// Rhino 7 on the Mac runs this on Mono. Mono's <c>HttpListener</c> accepts
    /// through a <c>SocketAsyncEventArgs</c> that <c>EndPointListener</c> keeps
    /// only as a constructor local. In Rhino that accept dies and the port
    /// closes, with no managed Dispose: curl then gets nothing. The MCP server
    /// in this plugin stays up because it blocks in <c>AcceptTcpClient</c> on a
    /// thread it holds. This channel does the same. <see cref="Start"/> also
    /// roots the channel until <see cref="Dispose"/>, so a collection after
    /// Start returns does not drop the socket.
    /// </remarks>
    public sealed class PageChannel : IDisposable
    {
        /// <summary>The largest action body accepted, in bytes.</summary>
        public const int MaxBody = 256 * 1024;

        static readonly List<PageChannel> Alive = new List<PageChannel>();

        readonly Action<JObject> _deliver;
        readonly object _gate = new object();
        readonly SortedDictionary<long, JObject> _waiting = new SortedDictionary<long, JObject>();
        TcpListener _listener;
        Thread _thread;
        long _delivered;
        string _token;
        string _page;
        volatile bool _running;

        /// <param name="deliver">Called once per action, in sequence order, under the channel's lock. Keep it short: post to the UI thread.</param>
        public PageChannel(Action<JObject> deliver)
        {
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
            _token = NewToken();
        }

        /// <summary>One line for /tmp/forsk-web.log. The window sets this. Tests leave it empty.</summary>
        public Action<string> Note { get; set; }

        public string Token
        {
            get { lock (_gate) return _token; }
        }

        public int Port { get; private set; }

        /// <summary>The page's base URL. The page posts to "action" under it.</summary>
        public string Origin => "http://127.0.0.1:" + Port + "/";

        /// <summary>The document GET / serves, so a navigation and a curl see the same page the web view loaded.</summary>
        public string Page
        {
            get { lock (_gate) return _page; }
            set { lock (_gate) _page = value; }
        }

        public void Start()
        {
            if (_listener != null) return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = FreePort();
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                }
                catch (SocketException e)
                {
                    NoteLine("channel bind " + port + " · " + e.GetType().Name + ": " + e.Message);
                    try { listener.Stop(); } catch (Exception) { }
                    continue;
                }
                _listener = listener;
                Port = port;
                _running = true;
                lock (Alive)
                {
                    if (!Alive.Contains(this)) Alive.Add(this);
                }
                _thread = new Thread(Loop) { IsBackground = true, Name = "Forsk page channel" };
                _thread.Start();
                NoteLine("channel listening " + Origin);
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

        /// <summary>Close the socket and the accept thread. Closing again does nothing and never throws.</summary>
        public void Dispose()
        {
            _running = false;
            var listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try { listener.Stop(); }
                catch (Exception e) { NoteLine("channel close · " + e.GetType().Name + ": " + e.Message); }
            }
            var thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try { thread.Join(1000); }
                catch (Exception) { }
            }
            lock (Alive) Alive.Remove(this);
        }

        /// <summary>
        /// Put the token and the origin into the page shell, then inline the script.
        /// The markers are <c>%%FORSK_TOKEN%%</c> and <c>%%FORSK_ORIGIN%%</c>.
        /// Replacing the bare word <c>FORSK_TOKEN</c> also rewrote the property
        /// <c>window.FORSK_TOKEN</c>, so the page posted with no token. The channel
        /// answered 403 and never reached the window, which is why a live send
        /// cleared the field and left no line in the log.
        /// </summary>
        public static string ComposePage(string html, string script, string token, string origin)
        {
            if (html == null) throw new ArgumentNullException(nameof(html));
            return html
                .Replace("%%FORSK_TOKEN%%", token ?? "")
                .Replace("%%FORSK_ORIGIN%%", origin ?? "")
                .Replace("FORSK_SCRIPT", script ?? "");
        }

        void Loop()
        {
            var listener = _listener;
            while (_running && listener != null)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception e)
                {
                    if (!_running) break;
                    NoteLine("channel accept · " + e.GetType().Name + ": " + e.Message);
                    break;
                }
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    using (var stream = client.GetStream())
                        Handle(stream);
                }
                catch (Exception e)
                {
                    NoteLine("channel error · " + e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    try { client.Close(); }
                    catch (Exception) { }
                }
            }
            NoteLine("channel stopped");
        }

        void Handle(NetworkStream stream)
        {
            var request = ReadRequest(stream);
            if (request == null)
            {
                NoteLine("channel request · unreadable");
                WriteResponse(stream, 400, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"bad request\"}"));
                return;
            }
            if (request.TooLarge)
            {
                NoteLine("channel " + request.Method + " " + request.Path + " · 413");
                WriteResponse(stream, 413, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"too large\"}"));
                return;
            }
            if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                NoteLine("channel OPTIONS " + request.Path + " · 204");
                WriteResponse(stream, 204, null, null);
                return;
            }
            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && request.Path == "/")
            {
                var page = Page;
                if (page == null)
                {
                    NoteLine("channel GET / · 404");
                    WriteResponse(stream, 404, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"not found\"}"));
                    return;
                }
                NoteLine("channel GET / · 200");
                WriteResponse(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page));
                return;
            }
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) || request.Path != "/action")
            {
                NoteLine("channel " + request.Method + " " + request.Path + " · 404");
                WriteResponse(stream, 404, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"not found\"}"));
                return;
            }
            JObject message;
            try
            {
                message = JObject.Parse(Encoding.UTF8.GetString(request.Body ?? new byte[0]));
            }
            catch (JsonException)
            {
                NoteLine("channel POST /action · 400 not json");
                WriteResponse(stream, 400, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"not json\"}"));
                return;
            }
            if (!SameToken(message["t"]?.ToString(), Token))
            {
                NoteLine("channel POST /action · 403 token");
                WriteResponse(stream, 403, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"token\"}"));
                return;
            }
            var seq = message["seq"]?.Type == JTokenType.Integer ? message["seq"].Value<long>() : 0;
            if (seq < 1)
            {
                NoteLine("channel POST /action · 400 seq");
                WriteResponse(stream, 400, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"seq\"}"));
                return;
            }
            message.Remove("t");
            bool fresh;
            try
            {
                fresh = Accept(seq, message);
            }
            catch (Exception e)
            {
                NoteLine("channel deliver · " + e.GetType().Name + ": " + e.Message);
                WriteResponse(stream, 500, "application/json; charset=utf-8", Bytes("{\"ok\":false,\"error\":\"deliver\"}"));
                return;
            }
            NoteLine("channel POST /action · 200 seq=" + seq + (fresh ? "" : " repeat"));
            var body = new JObject { ["ok"] = true, ["seq"] = seq, ["repeat"] = !fresh };
            WriteResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
        }

        static Request ReadRequest(NetworkStream stream)
        {
            var buf = new MemoryStream();
            var temp = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0 && buf.Length < 32768)
            {
                int n;
                try { n = stream.Read(temp, 0, temp.Length); }
                catch (IOException) { return null; }
                if (n <= 0) break;
                buf.Write(temp, 0, n);
                headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, new byte[] { 13, 10, 13, 10 });
            }
            if (headerEnd < 0) return null;
            var header = Encoding.ASCII.GetString(buf.GetBuffer(), 0, headerEnd);
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            long length = 0;
            for (var i = 1; i < lines.Length; i++)
            {
                const string key = "Content-Length:";
                if (lines[i].Length < key.Length || !lines[i].StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!long.TryParse(lines[i].Substring(key.Length).Trim(), out length) || length < 0) return null;
            }
            var request = new Request { Method = parts[0], Path = PathOnly(parts[1]) };
            if (length > MaxBody)
            {
                request.TooLarge = true;
                return request;
            }
            var body = new byte[length];
            var have = (int)buf.Length - (headerEnd + 4);
            var got = 0;
            if (have > 0)
            {
                var take = have > body.Length ? body.Length : have;
                Buffer.BlockCopy(buf.GetBuffer(), headerEnd + 4, body, 0, take);
                got = take;
            }
            while (got < body.Length)
            {
                int n;
                try { n = stream.Read(body, got, body.Length - got); }
                catch (IOException) { return null; }
                if (n <= 0) return null;
                got += n;
            }
            request.Body = body;
            return request;
        }

        static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] body)
        {
            var text = new StringBuilder();
            text.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            text.Append("Access-Control-Allow-Origin: *\r\n");
            text.Append("Access-Control-Allow-Private-Network: true\r\n");
            text.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            text.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            text.Append("Cache-Control: no-store\r\n");
            text.Append("Connection: close\r\n");
            if (contentType != null) text.Append("Content-Type: ").Append(contentType).Append("\r\n");
            var n = body == null ? 0 : body.Length;
            text.Append("Content-Length: ").Append(n).Append("\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(text.ToString());
            stream.Write(head, 0, head.Length);
            if (n > 0) stream.Write(body, 0, n);
            stream.Flush();
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                default: return "Error";
            }
        }

        static string PathOnly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "/";
            var q = raw.IndexOf('?');
            if (q >= 0) raw = raw.Substring(0, q);
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var scheme = raw.IndexOf("//", StringComparison.Ordinal);
                var slash = scheme < 0 ? -1 : raw.IndexOf('/', scheme + 2);
                raw = slash < 0 ? "/" : raw.Substring(slash);
            }
            if (raw.Length > 1 && raw[raw.Length - 1] == '/') raw = raw.Substring(0, raw.Length - 1);
            return raw.Length == 0 ? "/" : raw;
        }

        static int IndexOf(byte[] data, int length, byte[] needle)
        {
            var last = length - needle.Length;
            for (var i = 0; i <= last; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        static byte[] Bytes(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        void NoteLine(string line)
        {
            var note = Note;
            if (note == null || line == null) return;
            try { note(line); }
            catch (Exception) { }
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

        sealed class Request
        {
            public string Method;
            public string Path;
            public byte[] Body;
            public bool TooLarge;
        }
    }
}
