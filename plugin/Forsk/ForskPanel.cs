using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace RhinoMCPPlugin.Forsk
{
    [Guid("c4a7e2b1-9f63-4d18-a5e0-7b2c8d1f6a44")]
    public sealed class ForskPanel : Panel, IPanel
    {
        ForskHeader _header;
        ForskButton _chip;
        ForskComposer _composer;
        Scrollable _scroll;
        StackLayout _thread;
        readonly List<ForskLine> _lines = new List<ForskLine>();
        readonly List<JObject> _history = new List<JObject>();
        UITimer _timer;
        ForskMode _mode = ForskMode.Build;
        BakeChip _chipState = new BakeChip();
        bool _busy;
        bool _warnedPrompt;
        bool _hooked;
        bool _laying;

        public ForskPanel()
        {
            try
            {
                BuildUi();
            }
            catch (Exception e)
            {
                Content = new Label
                {
                    Text = "Forsk panel failed to open.\n" + e.Message,
                    Wrap = WrapMode.Word,
                    TextColor = ForskPaint.Ink
                };
                try
                {
                    System.IO.File.WriteAllText("/tmp/forsk-panel.log", e.ToString());
                }
                catch
                {
                    // The label is the failure surface.
                }
            }
        }

        void BuildUi()
        {
            ForskType.Load();
            BackgroundColor = ForskPaint.Paper;
            MinimumSize = new Size(300, 460);

            _header = new ForskHeader();
            _chip = new ForskButton { Text = "Generate 3D model", Visible = false };
            _composer = new ForskComposer();

            _chip.Click += (s, e) =>
            {
                if (_mode == ForskMode.Sheets) PrintPdf();
                else Bake();
            };
            _composer.ModePick.Picked += (s, e) => SetMode(_composer.ModePick.Mode);
            _composer.Send.Click += (s, e) => Send();
            _composer.Input.LoadComplete += (s, e) => ForskField.Style(_composer.Input);
            _composer.Input.GotFocus += (s, e) =>
            {
                ForskField.Style(_composer.Input);
                Application.Instance.AsyncInvoke(() => ForskField.Style(_composer.Input));
            };
            _composer.Input.KeyDown += (s, e) =>
            {
                if (e.Key != Keys.Enter || e.Shift) return;
                e.Handled = true;
                Send();
                // The text view may insert the return after this handler.
                Application.Instance.AsyncInvoke(() =>
                {
                    var left = _composer.Input.Text ?? "";
                    if (left.Trim().Length == 0 && left.Length > 0)
                        _composer.Input.Text = "";
                });
            };

            var chrome = new StackLayout
            {
                Spacing = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                AlignLabels = false,
                BackgroundColor = ForskPaint.Paper,
                Items = { _header, _chip }
            };

            _thread = new StackLayout
            {
                Padding = new Padding(0, 2, 0, 8),
                Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                AlignLabels = false,
                BackgroundColor = ForskPaint.Paper
            };
            _scroll = new Scrollable
            {
                Content = _thread,
                ExpandContentWidth = true,
                Border = BorderType.None,
                BackgroundColor = ForskPaint.Paper
            };

            var root = new TableLayout
            {
                Padding = new Padding(16, 16, 16, 14),
                Spacing = new Size(12, 12),
                BackgroundColor = ForskPaint.Paper
            };
            root.Rows.Add(new TableRow(new TableCell(chrome, true)));
            root.Rows.Add(new TableRow(new TableCell(_scroll, true)) { ScaleHeight = true });
            root.Rows.Add(new TableRow(new TableCell(_composer, true)));
            Content = root;

            _timer = new UITimer { Interval = 2 };
            _timer.Elapsed += (s, e) =>
            {
                if (_busy) return;
                RefreshChrome();
            };

            SizeChanged += (s, e) => Relayout();
            SetMode(ForskMode.Build);
            Hook();
            RefreshChrome();
            Relayout();
            _timer.Start();
        }

        public static void FocusAfterCommand(RhinoDoc doc)
        {
            // The command line takes focus back when the command returns.
            // Focus on the next turn, then once more so the caret wins.
            Application.Instance.AsyncInvoke(() =>
            {
                FocusComposer(doc);
                Application.Instance.AsyncInvoke(() => FocusComposer(doc));
            });
        }

        static void FocusComposer(RhinoDoc doc)
        {
            var active = doc ?? RhinoDoc.ActiveDoc;
            if (active == null) return;
            var found = Panels.GetPanel(typeof(ForskPanel).GUID, active.RuntimeSerialNumber) as ForskPanel;
            if (found == null) return;
            found._composer.Input.Focus();
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
            Hook();
            ForskField.Style(_composer.Input);
            Application.Instance.AsyncInvoke(() => ForskField.Style(_composer.Input));
            RefreshChrome();
            if (!_warnedPrompt)
            {
                ForskPrompts.Load(_mode);
                if (!string.IsNullOrEmpty(ForskPrompts.Warning))
                {
                    _warnedPrompt = true;
                    AddLine("receipt", ForskPrompts.Warning);
                }
            }
            if (!_timer.Started) _timer.Start();
            Relayout();
        }

        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
        {
            _timer.Stop();
        }

        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
        {
            _timer.Stop();
            Unhook();
        }

        void Hook()
        {
            if (_hooked) return;
            RhinoDoc.SelectObjects += OnSelection;
            RhinoDoc.DeselectObjects += OnSelection;
            RhinoDoc.DeselectAllObjects += OnDeselectAll;
            _hooked = true;
        }

        void Unhook()
        {
            if (!_hooked) return;
            RhinoDoc.SelectObjects -= OnSelection;
            RhinoDoc.DeselectObjects -= OnSelection;
            RhinoDoc.DeselectAllObjects -= OnDeselectAll;
            _hooked = false;
        }

        void OnSelection(object sender, RhinoObjectSelectionEventArgs e)
        {
            RefreshTarget();
        }

        void OnDeselectAll(object sender, RhinoDeselectAllObjectsEventArgs e)
        {
            RefreshTarget();
        }

        void SetMode(ForskMode mode)
        {
            _mode = mode;
            _composer.ModePick.Mode = mode;
            _composer.ModePick.Invalidate();
            RefreshChrome();
        }

        void RefreshChrome()
        {
            bool connected = RhinoMCPServerController.IsServerRunning();
            _header.SetStatus(connected, connected ? "Connected" : "Type mcpstart");
            _chipState = ForskBake.Detect();
            if (_mode == ForskMode.Sheets)
            {
                _chip.Visible = true;
                _chip.Text = "Print PDF";
            }
            else
            {
                _chip.Visible = _chipState.Visible;
                _chip.Text = _chipState.Label;
            }
            RefreshTarget();
        }

        void RefreshTarget()
        {
            _header.SetTarget(ForskTarget.Read());
        }

        void Send()
        {
            if (_busy) return;
            var text = (_composer.Input.Text ?? "").Trim();
            if (text.Length == 0) return;
            _composer.Input.Text = "";
            _busy = true;
            _composer.Send.EnabledClick = false;
            AddLine("user", text);
            var mode = _mode;
            if (mode == ForskMode.Sheets && ForskPrint.IsRequest(text))
            {
                QueuePrint(text);
                return;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ForskGrok.RunTurn(text, mode, _history, (role, line) =>
                    {
                        Application.Instance.AsyncInvoke(() => AddLine(role, line));
                    });
                }
                catch (Exception e)
                {
                    var message = ForskTools.Clip(e.Message);
                    Application.Instance.AsyncInvoke(() => AddLine("assistant", message));
                }
                finally
                {
                    Application.Instance.AsyncInvoke(() =>
                    {
                        _busy = false;
                        _composer.Send.EnabledClick = true;
                        RefreshChrome();
                    });
                }
            });
        }

        void PrintPdf()
        {
            if (_busy || _mode != ForskMode.Sheets) return;
            _busy = true;
            _composer.Send.EnabledClick = false;
            _chip.EnabledClick = false;
            AddLine("user", "Print PDF");
            QueuePrint("Print PDF");
        }

        void QueuePrint(string historyUser)
        {
            _chip.EnabledClick = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string line;
                try
                {
                    line = ForskPrint.Run();
                }
                catch (Exception e)
                {
                    line = "Print PDF · error · " + ForskTools.Clip(e.Message);
                }
                Application.Instance.AsyncInvoke(() =>
                {
                    AddLine("receipt", line);
                    _history.Add(new JObject { ["role"] = "user", ["content"] = historyUser });
                    _history.Add(new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = line
                    });
                    _busy = false;
                    _composer.Send.EnabledClick = true;
                    _chip.EnabledClick = true;
                    RefreshChrome();
                });
            });
        }

        void Bake()
        {
            if (_busy || !_chipState.Visible) return;
            _busy = true;
            _composer.Send.EnabledClick = false;
            _chip.EnabledClick = false;
            var rebuild = _chipState.HasWalls;
            var label = _chipState.Label;
            AddLine("user", label);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<string> lines;
                try
                {
                    lines = ForskBake.Run(rebuild);
                }
                catch (Exception e)
                {
                    lines = new List<string> { "Generate 3D · error · " + ForskTools.Clip(e.Message) };
                }
                Application.Instance.AsyncInvoke(() =>
                {
                    foreach (var line in lines)
                        AddLine("receipt", line);
                    _history.Add(new JObject { ["role"] = "user", ["content"] = label });
                    _history.Add(new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Join("\n", lines.ToArray())
                    });
                    _busy = false;
                    _composer.Send.EnabledClick = true;
                    _chip.EnabledClick = true;
                    RefreshChrome();
                });
            });
        }

        void AddLine(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var message = new ForskMessage(role, text.Trim());
            var row = new TableLayout
            {
                Spacing = new Size(0, 0),
                BackgroundColor = ForskPaint.Paper
            };
            if (role == "user")
            {
                var spacer = new Panel { BackgroundColor = ForskPaint.Paper };
                row.Rows.Add(new TableRow(new TableCell(spacer, true), new TableCell(message, false)));
            }
            else
            {
                row.Rows.Add(new TableRow(new TableCell(message, true)));
            }
            _lines.Add(new ForskLine { Row = row, Message = message });
            _thread.Items.Add(row);
            Relayout();
            _scroll.ScrollPosition = new Point(0, 100000);
        }

        void Relayout()
        {
            if (_laying) return;
            _laying = true;
            try
            {
                RelayoutCore();
            }
            finally
            {
                _laying = false;
            }
        }

        void RelayoutCore()
        {
            int inner = Math.Max(200, Width - 32);
            _header.Reflow(inner);
            _chip.Width = inner;
            _composer.Width = inner;
            _composer.Place();
            int threadW = _scroll.Width > 100 ? _scroll.Width - 20 : inner;
            foreach (var line in _lines)
            {
                line.Row.Width = threadW;
                line.Message.Reflow(threadW);
            }
        }

        sealed class ForskLine
        {
            public TableLayout Row;
            public ForskMessage Message;
        }
    }
}
