using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.UI;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The Forsk window: a floating WebKit page with no owner, above Rhino's
    /// windows and hidden when Rhino is not the active app. The page posts
    /// actions over the page channel (HTTP on 127.0.0.1, see PageChannel) and
    /// C# answers with Forsk.render. One window serves every open file; the
    /// thread is per document, keyed by RuntimeSerialNumber, and kept on disk
    /// by file path. The place on screen is remembered and clamped to a screen
    /// that is there on every open. Log: /tmp/forsk-web.log.
    /// </summary>
    sealed class ForskWindow : FloatingForm
    {
        const string LogPath = "/tmp/forsk-web.log";
        /// <summary>Above this, the page could not repaint during a job: the shimmer was frozen.</summary>
        const long FrozenMs = 250;

        static ForskWindow _open;
        static bool _docsHooked;
        static readonly WindowModels Models = new WindowModels(new ThreadStore(ThreadStore.DefaultRoot()));

        readonly WebView _web;
        readonly PageChannel _channel;
        readonly UITimer _boundsTimer;
        bool _ready;
        bool _busy;

        public static void Open(RhinoDoc doc)
        {
            HookDocs();
            if (_open == null)
            {
                _open = new ForskWindow();
                _open.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_open, s)) _open = null;
                };
            }
            _open.Place();
            _open.Show();
            _open.BringToFront();
            _open.FocusComposer();
        }

        ForskWindow()
        {
            Title = "Forsk";
            Resizable = true;
            Padding = new Padding(0);
            MinimumSize = new Size((int)WindowBounds.MinWidth, (int)WindowBounds.MinHeight);
            BackgroundColor = Colors.White;
            _web = new WebView();
            Content = _web;
            _channel = new PageChannel(message => Application.Instance.AsyncInvoke(() => OnAction(message)));
            _channel.Start();
            _boundsTimer = new UITimer { Interval = 0.5 };
            _boundsTimer.Elapsed += (s, e) =>
            {
                _boundsTimer.Stop();
                SaveBounds();
            };
            LocationChanged += (s, e) => RememberSoon();
            SizeChanged += (s, e) => RememberSoon();
            Closed += (s, e) =>
            {
                _boundsTimer.Stop();
                SaveBounds();
                RhinoDoc.SelectObjects -= OnSelection;
                RhinoDoc.DeselectObjects -= OnSelection;
                RhinoDoc.DeselectAllObjects -= OnDeselectAll;
                _channel.Dispose();
                Log("closed");
            };
            RhinoDoc.SelectObjects += OnSelection;
            RhinoDoc.DeselectObjects += OnSelection;
            RhinoDoc.DeselectAllObjects += OnDeselectAll;
            Log("open · channel " + _channel.Origin + " · window "
                + (ForskField.PinAqua(ControlObject) ? "pinned to Aqua" : "appearance not pinned"));
            ForskField.PinAqua(_web.ControlObject);
            LoadPage();
        }

        void LoadPage()
        {
            _ready = false;
            _channel.Reset();
            try
            {
                _web.LoadHtml(ForskPage.Html(_channel.Token), new Uri(_channel.Origin));
            }
            catch (Exception e)
            {
                Log("load " + e.Message);
            }
        }

        // ------------------------------------------------------------ actions

        void OnAction(JObject message)
        {
            var kind = message["kind"]?.ToString() ?? "";
            Log("action seq=" + message["seq"] + " kind=" + kind);
            try
            {
                switch (kind)
                {
                    case "ready":
                        _ready = true;
                        Log("page ready · " + NativeClass());
                        Render();
                        if (HasFocus) FocusComposer();
                        return;
                    case "send":
                        Send(message["text"]?.ToString());
                        return;
                    case "card":
                        Answer(message["card"]?.ToString(), message["pill"]?.ToString());
                        return;
                    case "card.close":
                        if (Active()?.Close(message["card"]?.ToString()) == true) Render();
                        return;
                    case "slot":
                    case "help":
                        // The bar arrives with the shell. The shortcut already stopped in the page.
                        return;
                }
            }
            catch (Exception e)
            {
                Log("action " + kind + " · " + e.GetType().Name + ": " + e.Message);
            }
        }

        void Send(string text)
        {
            var thread = Active();
            if (thread == null || string.IsNullOrWhiteSpace(text)) return;
            thread.Add("user", text);
            if (ForskPrint.IsRequest(text)) AskPrint(thread);
            Models.Persist(thread);
            Render();
        }

        static void AskPrint(DocThread thread)
        {
            thread.AddCard("print", "Print this file as a PDF?",
                new CardPill("print", "Print PDF"),
                new CardPill("later", "Not now"));
        }

        void Answer(string cardId, string pillId)
        {
            var thread = Active();
            if (thread == null) return;
            var card = thread.Find(cardId);
            var pill = thread.Answer(cardId, pillId);
            if (pill != null && card?["kind"]?.ToString() == "print" && pill.Id == "print")
                RunPrint(thread);
            Models.Persist(thread);
            Render();
        }

        /// <summary>
        /// Print on a worker thread, the save dialog parented to this window. A
        /// static step line stays on screen. layout_pack holds the UI thread, so
        /// the longest stall is logged: above FrozenMs the shimmer was frozen.
        /// </summary>
        void RunPrint(DocThread thread)
        {
            if (_busy)
            {
                thread.Add("line", "Forsk is still working. Try again when the step line is gone.");
                return;
            }
            _busy = true;
            thread.Busy = "Printing… step 1 of 2: laying out the sheets";
            var stall = StallWatch.Start();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // layout_pack takes the UI thread. Let the step line paint first.
                Thread.Sleep(50);
                string line;
                try
                {
                    line = ForskPrint.Run(status => Application.Instance.AsyncInvoke(() =>
                    {
                        thread.Busy = "Printing… step 2 of 2: " + status;
                        Render();
                    }), this);
                }
                catch (Exception e)
                {
                    line = "Print PDF · error · " + ForskTools.Clip(e.Message);
                }
                var longest = stall.Stop();
                Application.Instance.AsyncInvoke(() =>
                {
                    Log("print: longest UI stall " + longest + " ms"
                        + (longest >= FrozenMs ? " · the page could not repaint, the shimmer was frozen" : " · the shimmer kept moving"));
                    thread.Busy = null;
                    AddLegacyReceipt(thread, line);
                    _busy = false;
                    Models.Persist(thread);
                    Render();
                });
            });
        }

        /// <summary>A "Label · ok · rest" line as a receipt: the label in bold, then the rest.</summary>
        static void AddLegacyReceipt(DocThread thread, string line)
        {
            var parts = (line ?? "").Split(new[] { " · " }, 3, StringSplitOptions.None);
            if (parts.Length < 2)
            {
                thread.Add("line", line);
                return;
            }
            var ok = !string.Equals(parts[1], "error", StringComparison.OrdinalIgnoreCase);
            var rest = parts.Length > 2 ? parts[2] : parts[1];
            thread.AddReceipt(ok, parts[0], rest);
        }

        // ------------------------------------------------------------ model

        static DocThread Active()
        {
            var doc = RhinoDoc.ActiveDoc;
            return doc == null ? null : Models.For(doc.RuntimeSerialNumber, FileName(doc), doc.Path);
        }

        static string FileName(RhinoDoc doc)
        {
            var name = doc?.Name;
            return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
        }

        void Render()
        {
            if (!_ready) return;
            var thread = Active();
            JObject model;
            if (thread == null)
            {
                model = new JObject { ["file"] = "No file open", ["thread"] = new JArray() };
            }
            else
            {
                if (thread.Items.Count == 0) AskPrint(thread);
                model = thread.ToJson();
            }
            model["target"] = ForskTarget.Read();
            Script("Forsk.render", model);
        }

        void Script(string function, JToken argument)
        {
            try
            {
                _web.ExecuteScript(ForskPage.Call(function, argument));
            }
            catch (Exception e)
            {
                Log("script " + function + " · " + e.Message);
            }
        }

        // ------------------------------------------------------------ documents

        static void HookDocs()
        {
            if (_docsHooked) return;
            _docsHooked = true;
            RhinoDoc.ActiveDocumentChanged += (s, e) => _open?.Render();
            RhinoDoc.EndOpenDocument += (s, e) =>
            {
                if (e.Merge || e.Reference || e.Document == null) return;
                Models.For(e.Document.RuntimeSerialNumber, FileName(e.Document), e.Document.Path);
                _open?.Render();
            };
            RhinoDoc.EndSaveDocument += (s, e) =>
            {
                if (e.ExportSelected || e.Document == null) return;
                var thread = Models.For(e.Document.RuntimeSerialNumber, FileName(e.Document), e.Document.Path);
                Models.Persist(thread);
                _open?.Render();
            };
            RhinoDoc.CloseDocument += (s, e) =>
            {
                if (e.Document == null) return;
                var serial = e.Document.RuntimeSerialNumber;
                if (!Models.Has(serial)) return;
                Models.Persist(Models.For(serial, null, e.Document.Path));
                Models.Forget(serial);
            };
        }

        void OnSelection(object sender, Rhino.DocObjects.RhinoObjectSelectionEventArgs e)
        {
            Render();
        }

        void OnDeselectAll(object sender, Rhino.DocObjects.RhinoDeselectAllObjectsEventArgs e)
        {
            Render();
        }

        // ------------------------------------------------------------ focus and place

        /// <summary>The command line takes focus back when a command returns. Focus on the next turn, then once more.</summary>
        void FocusComposer()
        {
            Application.Instance.AsyncInvoke(() =>
            {
                FocusOnce();
                Application.Instance.AsyncInvoke(FocusOnce);
            });
        }

        void FocusOnce()
        {
            try
            {
                Focus();
                _web.Focus();
                if (_ready) _web.ExecuteScript("Forsk.focus()");
            }
            catch (Exception e)
            {
                Log("focus " + e.Message);
            }
        }

        /// <summary>The remembered place, clamped to a screen that is there now.</summary>
        void Place()
        {
            var screens = new List<ScreenRect>();
            try
            {
                var main = Screen.PrimaryScreen;
                if (main != null) screens.Add(Rect(main.WorkingArea));
                foreach (var screen in Screen.Screens)
                    if (screen != null && screen != main) screens.Add(Rect(screen.WorkingArea));
            }
            catch (Exception e)
            {
                Log("screens " + e.Message);
            }
            var saved = WindowBounds.Parse(ReadBounds());
            var place = WindowBounds.Clamp(saved, screens);
            Location = new Point((int)Math.Round(place.X), (int)Math.Round(place.Y));
            Size = new Size((int)Math.Round(place.Width), (int)Math.Round(place.Height));
            Log("place " + (saved.HasValue ? saved.Value.ToString() : "none") + " -> " + place + " on " + screens.Count + " screen(s)");
        }

        static ScreenRect Rect(RectangleF area)
        {
            return new ScreenRect(area.X, area.Y, area.Width, area.Height);
        }

        void RememberSoon()
        {
            _boundsTimer.Stop();
            _boundsTimer.Start();
        }

        void SaveBounds()
        {
            try
            {
                var rect = new ScreenRect(Location.X, Location.Y, Size.Width, Size.Height);
                if (rect.IsEmpty) return;
                var path = BoundsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, WindowBounds.Format(rect));
            }
            catch (Exception e)
            {
                Log("bounds " + e.Message);
            }
        }

        static string ReadBounds()
        {
            try
            {
                var path = BoundsPath();
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string BoundsPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".forsk", "window.txt");
        }

        string NativeClass()
        {
            var native = _web.ControlObject;
            if (native == null) return "web view: no native control";
            var type = native.GetType();
            var text = "web view: " + type.FullName;
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                if (b.Namespace != "WebKit") continue;
                text += " : " + b.FullName;
                break;
            }
            return text;
        }

        internal static void Log(string line)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n");
            }
            catch (Exception)
            {
                // The window still works if the log cannot be written.
            }
        }

        /// <summary>
        /// Measures how long the UI thread stays blocked while a job runs. A
        /// background thread posts an empty call to the UI thread every 100 ms
        /// and times the answer.
        /// </summary>
        sealed class StallWatch
        {
            volatile bool _stop;
            long _longest;

            public static StallWatch Start()
            {
                var watch = new StallWatch();
                new Thread(watch.Loop) { IsBackground = true, Name = "Forsk stall watch" }.Start();
                return watch;
            }

            public long Stop()
            {
                _stop = true;
                return Interlocked.Read(ref _longest);
            }

            void Loop()
            {
                while (!_stop)
                {
                    var clock = Stopwatch.StartNew();
                    var done = new ManualResetEventSlim(false);
                    Application.Instance.AsyncInvoke(done.Set);
                    while (!done.Wait(50))
                    {
                        Note(clock.ElapsedMilliseconds);
                        if (_stop) return;
                    }
                    Note(clock.ElapsedMilliseconds);
                    Thread.Sleep(100);
                }
            }

            void Note(long ms)
            {
                long seen;
                while (ms > (seen = Interlocked.Read(ref _longest)))
                    if (Interlocked.CompareExchange(ref _longest, ms, seen) == seen) break;
            }
        }
    }

    /// <summary>The page: window.html with window.js inlined and this page's token.</summary>
    static class ForskPage
    {
        const string Prefix = "RhinoMCPPlugin.Page.";
        static string _html;
        static string _script;

        public static string Html(string token)
        {
            if (_html == null) _html = Resource("window.html");
            if (_script == null) _script = Resource("window.js");
            return _html.Replace("FORSK_TOKEN", token).Replace("FORSK_SCRIPT", _script);
        }

        /// <summary>A call into the page. Non-ASCII is escaped, so æ ø å and line separators reach the page intact.</summary>
        public static string Call(string function, JToken argument)
        {
            var sb = new StringBuilder();
            using (var text = new StringWriter(sb))
            using (var json = new JsonTextWriter(text) { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii })
                argument.WriteTo(json);
            return function + "(" + sb + ")";
        }

        static string Resource(string name)
        {
            using (var stream = typeof(ForskPage).Assembly.GetManifestResourceStream(Prefix + name))
            {
                if (stream == null) throw new InvalidOperationException("The Forsk page is missing " + name + ".");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
