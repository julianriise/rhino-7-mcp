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
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The Forsk window: a floating WebKit page with no owner, above Rhino's
    /// windows and hidden when Rhino is not the active app. The page posts
    /// actions over the page channel (HTTP on 127.0.0.1, see PageChannel) and
    /// C# answers with Forsk.render. One window serves every open file; the
    /// thread is per document, keyed by RuntimeSerialNumber, and kept on disk
    /// by file path. The bar is the registry's read of the file, recomputed on
    /// idle after a document event marks it dirty. Log: /tmp/forsk-web.log.
    /// </summary>
    sealed partial class ForskWindow : FloatingForm
    {
        const string LogPath = "/tmp/forsk-web.log";
        /// <summary>Above this, the page could not repaint during a job: the shimmer was frozen.</summary>
        const long FrozenMs = 250;
        /// <summary>How often the listener and the chat key are looked at again.</summary>
        static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

        static ForskWindow _open;
        static bool _docsHooked;
        static readonly WindowModels Models = new WindowModels(new ThreadStore(ThreadStore.DefaultRoot()));
        static readonly LastActionTracker Tracker = new LastActionTracker();
        static bool _dirty = true;
        /// <summary>Object events inside Forsk's own calls while the current job runs: its record holds a change.</summary>
        static int _jobChanges;
        static bool _listenerUp = true;
        static bool _keyPresent;
        static DateTime _polled = DateTime.MinValue;

        readonly WebView _web;
        readonly PageChannel _channel;
        readonly UITimer _boundsTimer;
        bool _ready;
        FileFacts _facts;
        uint _factsDoc;

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
            Log("shown · " + _open.NativeClass());
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
            _channel.Note = Log;
            _channel.Start();
            _web.DocumentLoaded += (s, e) => Log("page document loaded · " + (_web.Url == null ? "no url" : _web.Url.ToString()));
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
                _channel.Dispose();
                Log("closed");
            };
            Log("open · channel " + _channel.Origin + " · window "
                + (ForskAqua.Pin(ControlObject) ? "pinned to Aqua" : "appearance not pinned"));
            ForskAqua.Pin(_web.ControlObject);
            Poll(force: true);
            LoadPage();
        }

        void LoadPage()
        {
            _ready = false;
            _channel.Reset();
            var html = ForskPage.Html(_channel.Token, _channel.Origin);
            _channel.Page = html;
            try
            {
                _web.LoadHtml(html, new Uri(_channel.Origin));
                Log("page load · " + _channel.Origin);
            }
            catch (Exception e)
            {
                Log("load " + e.GetType().Name + ": " + e.Message);
            }
        }

        void OnAction(JObject message)
        {
            var kind = message["kind"]?.ToString() ?? "";
            Log("action seq=" + message["seq"] + " kind=" + kind + (message["id"] != null ? " id=" + message["id"] : ""));
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
                        Log("send received");
                        Send(message["text"]?.ToString());
                        return;
                    case "action":
                        Fire(message["id"]?.ToString(), message["from"]?.ToString() == "card");
                        return;
                    case "slot":
                        Slot(message["slot"]?.Type == JTokenType.Integer ? message["slot"].Value<int>() : 0);
                        return;
                    case "help":
                        _helpOpen = !_helpOpen;
                        Render();
                        return;
                    case "card":
                        Answer(message["card"]?.ToString(), message["pill"]?.ToString(), message["values"] as JObject);
                        return;
                    case "card.close":
                        CloseCard(message["card"]?.ToString());
                        return;
                    case "role":
                        PickRole(message["role"]?.ToString());
                        return;
                }
            }
            catch (Exception e)
            {
                Log("action " + kind + " · " + e.GetType().Name + ": " + e.Message);
            }
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

        /// <summary>The classifier's read of the document, with the key, the listener and Undo. Fresh every call.</summary>
        static FileFacts ReadFacts(RhinoDoc doc)
        {
            var input = RhinoMCPFunctions.ReadDocInput(doc);
            input.KeyPresent = _keyPresent;
            input.ListenerUp = _listenerUp;
            input.UndoNewest = Tracker.UndoNewest(doc.RuntimeSerialNumber);
            return FileClassifier.Read(input);
        }

        /// <summary>The facts the bar was drawn from. Recomputed when a document event marked them dirty, never mid-job.</summary>
        FileFacts Facts(RhinoDoc doc)
        {
            if (_facts == null || _factsDoc != doc.RuntimeSerialNumber || (_dirty && !_busy))
            {
                _facts = ReadFacts(doc);
                _factsDoc = doc.RuntimeSerialNumber;
                _dirty = false;
                // An unanswered card turns grey once what it asked about has changed.
                var stale = Active()?.StaleCards(_facts) ?? 0;
                if (stale > 0) Log("cards: " + stale + " went stale");
            }
            return _facts;
        }

        void Render()
        {
            if (!_ready) return;
            var doc = RhinoDoc.ActiveDoc;
            var thread = Active();
            JObject model;
            if (doc == null || thread == null)
                model = new JObject { ["file"] = ForskText.Get("window.nofile"), ["thread"] = new JArray() };
            else
                model = WindowView.Build(thread, Facts(doc), _helpOpen);
            var count = model["thread"] is JArray items ? items.Count : 0;
            Log("render · " + count + " items");
            Script("Forsk.render", model);
            if (thread != null) thread.Prefill = null;
        }

        /// <summary>
        /// Eto's ExecuteScript waits by running the event loop (RunIteration until
        /// the task completes), so a page action or a job's post could re-enter
        /// mid-render. Scripts go asynchronously instead: WebKit runs them in the
        /// order they were sent, and nothing here needs their result.
        /// </summary>
        void Script(string function, JToken argument)
        {
            RunScript(ForskPage.Call(function, argument), function);
        }

        void RunScript(string script, string what)
        {
            try
            {
                _web.ExecuteScriptAsync(script).ContinueWith(
                    t => Log("script " + what + " · " + t.Exception?.GetBaseException().Message),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception e)
            {
                Log("script " + what + " · " + e.Message);
            }
        }

        // ------------------------------------------------------------ documents

        static void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>The listener and the chat key have no events. Look again every couple of seconds on idle.</summary>
        static void Poll(bool force)
        {
            if (!force && DateTime.UtcNow - _polled < PollEvery) return;
            _polled = DateTime.UtcNow;
            bool up;
            try { up = RhinoMCPServerController.IsServerRunning(); }
            catch (Exception) { up = false; }
            var key = !string.IsNullOrEmpty(ForskKeys.Load());
            if (up == _listenerUp && key == _keyPresent) return;
            _listenerUp = up;
            _keyPresent = key;
            MarkDirty();
        }

        static void HookDocs()
        {
            if (_docsHooked) return;
            _docsHooked = true;
            RhinoDoc.ActiveDocumentChanged += (s, e) =>
            {
                Tracker.ActiveDocumentChanged();
                MarkDirty();
                _open?.Render();
            };
            RhinoDoc.EndOpenDocument += (s, e) =>
            {
                if (e.Merge || e.Reference || e.Document == null) return;
                Tracker.DocumentOpened();
                try { RhinoMCPFunctions.HideOpeningMarkers(e.Document); }
                catch (Exception ex) { Log("markers: " + ex.Message); }
                var thread = Models.For(e.Document.RuntimeSerialNumber, FileName(e.Document), e.Document.Path);
                if (thread.Items.Count > 0) thread.Add("line", ForskText.Get("line.reopened"));
                MarkDirty();
                _open?.Render();
            };
            RhinoDoc.EndSaveDocument += (s, e) =>
            {
                if (e.ExportSelected || e.Document == null) return;
                Models.Persist(Models.For(e.Document.RuntimeSerialNumber, FileName(e.Document), e.Document.Path));
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
            RhinoDoc.AddRhinoObject += (s, e) => ObjectChanged();
            RhinoDoc.DeleteRhinoObject += (s, e) => ObjectChanged();
            RhinoDoc.ReplaceRhinoObject += (s, e) => ObjectChanged();
            RhinoDoc.UndeleteRhinoObject += (s, e) => ObjectChanged();
            RhinoDoc.ModifyObjectAttributes += (s, e) => ObjectChanged();
            Rhino.Commands.Command.UndoRedo += (s, e) =>
            {
                Tracker.UndoRedo();
                MarkDirty();
            };
            RhinoDoc.SelectObjects += (s, e) => Selected();
            RhinoDoc.DeselectObjects += (s, e) => Selected();
            RhinoDoc.DeselectAllObjects += (s, e) => Selected();
            RhinoApp.Idle += (s, e) =>
            {
                Poll(force: false);
                var open = _open;
                if (open == null || !_dirty || open._busy) return;
                open.Render();
            };
        }

        /// <summary>An object came, went or changed. Outside a Forsk call it was the user: the last action is gone.</summary>
        static void ObjectChanged()
        {
            var inside = ForskCalls.Depth > 0;
            if (inside) _jobChanges++;
            Tracker.ObjectChanged(inside);
            MarkDirty();
        }

        static void Selected()
        {
            MarkDirty();
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
                if (_ready) RunScript("Forsk.focus()", "focus");
            }
            catch (Exception e)
            {
                Log("focus " + e.Message);
            }
        }

        /// <summary>A viewport pick starts: Rhino becomes the key window, so the clicks and keys go to it.</summary>
        static void HandToRhino()
        {
            try
            {
                // Otherwise the first viewport click only activates Rhino and the get never sees it.
                RhinoApp.SetFocusToMainWindow();
                RhinoEtoApp.MainWindow?.Focus();
            }
            catch (Exception e)
            {
                Log("rhino focus " + e.Message);
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

        public static string Html(string token, string origin)
        {
            if (_html == null) _html = WithAvatars(Resource("window.html"));
            if (_script == null) _script = Resource("window.js");
            return PageChannel.ComposePage(_html, _script, token, origin);
        }

        /// <summary>
        /// The faces ship as page resources and are inlined. An img of the
        /// SVG is painted at CSS pixels, so on Retina the face is soft while the
        /// type stays sharp. LoadHtml has no base URL for a separate file. The
        /// page renames each copy's mask and gradient; the sources' ids already differ.
        /// Another role adds a face the same way.
        /// </summary>
        static string WithAvatars(string html)
        {
            foreach (var name in new[] { "planner", "modeller", "plotter", "analyser", "support", "render" })
                html = html.Replace("%%AVATAR_" + name.ToUpperInvariant() + "%%", Resource("avatar-" + name + ".svg"));
            return html;
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
