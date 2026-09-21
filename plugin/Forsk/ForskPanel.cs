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
        static readonly Color Ink = Color.FromArgb(20, 20, 20);
        static readonly Color Quiet = Color.FromArgb(120, 120, 120);
        static readonly Color Line = Color.FromArgb(230, 230, 230);
        static readonly Color UserBg = Color.FromArgb(245, 245, 245);

        readonly Label _bridge;
        readonly Label _target;
        readonly ForskButton _chip;
        readonly ForskButton _build;
        readonly ForskButton _edit;
        readonly ForskButton _sheets;
        readonly ForskButton _send;
        readonly TextBox _prompt;
        readonly Scrollable _scroll;
        readonly StackLayout _thread;
        readonly List<Label> _labels = new List<Label>();
        readonly List<JObject> _history = new List<JObject>();
        readonly UITimer _timer;
        ForskMode _mode = ForskMode.Build;
        BakeChip _chipState = new BakeChip();
        bool _busy;
        bool _welcomed;
        bool _warnedPrompt;
        bool _hooked;

        public ForskPanel()
        {
            BackgroundColor = Colors.White;
            MinimumSize = new Size(300, 420);

            _bridge = new Label
            {
                Text = "Type mcpstart",
                TextColor = Quiet,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Font = SystemFonts.Default(11)
            };
            _target = new Label
            {
                Text = "Click something in the model.",
                TextColor = Ink,
                Wrap = WrapMode.Word,
                Font = SystemFonts.Default(12)
            };
            _chip = new ForskButton { Text = "Generate 3D model", Filled = true, Visible = false, Width = 168 };
            _build = new ForskButton { Text = "Build", Width = 72 };
            _edit = new ForskButton { Text = "Edit", Width = 72 };
            _sheets = new ForskButton { Text = "Sheets", Width = 72 };
            _send = new ForskButton { Text = "↑", Filled = true, Width = 36 };
            _prompt = new TextBox { PlaceholderText = "Message" };

            _chip.Click += (s, e) => Bake();
            _build.Click += (s, e) => SetMode(ForskMode.Build);
            _edit.Click += (s, e) => SetMode(ForskMode.Edit);
            _sheets.Click += (s, e) => SetMode(ForskMode.Sheets);
            _send.Click += (s, e) => Send();
            _prompt.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter && !e.Shift)
                {
                    e.Handled = true;
                    Send();
                }
            };

            var modes = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Items = { _build, _edit, _sheets }
            };
            var top = new TableLayout { Spacing = new Size(8, 0) };
            top.Rows.Add(new TableRow(new TableCell(_chip, false), new TableCell(_bridge, true)));

            _thread = new StackLayout
            {
                Padding = new Padding(0, 4),
                Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            _scroll = new Scrollable
            {
                Content = _thread,
                ExpandContentWidth = true,
                Border = BorderType.None,
                BackgroundColor = Colors.White
            };

            var promptRow = new TableLayout { Spacing = new Size(8, 0) };
            promptRow.Rows.Add(new TableRow(new TableCell(_prompt, true), new TableCell(_send, false)));

            var rule = new Panel { Height = 1, BackgroundColor = Line };
            var root = new TableLayout
            {
                Padding = new Padding(14),
                Spacing = new Size(8, 8),
                BackgroundColor = Colors.White
            };
            root.Rows.Add(top);
            root.Rows.Add(_target);
            root.Rows.Add(rule);
            root.Rows.Add(modes);
            var threadRow = new TableRow(new TableCell(_scroll, true)) { ScaleHeight = true };
            root.Rows.Add(threadRow);
            root.Rows.Add(promptRow);
            Content = root;

            _timer = new UITimer { Interval = 2 };
            _timer.Elapsed += (s, e) =>
            {
                if (_busy) return;
                RefreshChrome();
            };

            SizeChanged += (s, e) => RelayoutThread();
            SetMode(ForskMode.Build);
            Hook();
            RefreshChrome();
            _timer.Start();
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
            Hook();
            RefreshChrome();
            if (!_welcomed)
            {
                _welcomed = true;
                AddLine("assistant", "Build bakes the plan. Edit changes the selection. Sheets draws the S-* views.");
            }
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
            _build.Filled = mode == ForskMode.Build;
            _edit.Filled = mode == ForskMode.Edit;
            _sheets.Filled = mode == ForskMode.Sheets;
            _build.Invalidate();
            _edit.Invalidate();
            _sheets.Invalidate();
        }

        void RefreshChrome()
        {
            _bridge.Text = RhinoMCPServerController.IsServerRunning() ? "Connected" : "Type mcpstart";
            _chipState = ForskBake.Detect();
            _chip.Visible = _chipState.Visible;
            _chip.Text = _chipState.Label;
            _chip.Invalidate();
            RefreshTarget();
        }

        void RefreshTarget()
        {
            _target.Text = ForskTarget.Read();
            _target.TextColor = _target.Text.StartsWith("Target:") ? Ink : Quiet;
        }

        void Send()
        {
            if (_busy) return;
            var text = (_prompt.Text ?? "").Trim();
            if (text.Length == 0) return;
            _prompt.Text = "";
            _busy = true;
            _send.EnabledClick = false;
            AddLine("user", text);
            var mode = _mode;
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
                        _send.EnabledClick = true;
                        RefreshChrome();
                    });
                }
            });
        }

        void Bake()
        {
            if (_busy || !_chipState.Visible) return;
            _busy = true;
            _send.EnabledClick = false;
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
                    _send.EnabledClick = true;
                    _chip.EnabledClick = true;
                    RefreshChrome();
                });
            });
        }

        void AddLine(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var label = new Label
            {
                Text = text.Trim(),
                Wrap = WrapMode.Word,
                TextColor = role == "receipt" ? Quiet : Ink,
                Font = role == "receipt" ? SystemFonts.Default(11) : SystemFonts.Default(12)
            };
            var bubble = new Panel
            {
                Content = label,
                Padding = new Padding(8, 4),
                BackgroundColor = role == "user" ? UserBg : Colors.Transparent
            };
            _labels.Add(label);
            _thread.Items.Add(bubble);
            RelayoutThread();
        }

        void RelayoutThread()
        {
            var width = _scroll.Width > 40 ? _scroll.Width - 28 : 240;
            foreach (var label in _labels)
                label.Width = width;
            _scroll.ScrollPosition = new Point(0, 100000);
        }
    }

    sealed class ForskButton : Drawable
    {
        public string Text { get; set; }
        public bool Filled { get; set; }
        public bool EnabledClick { get; set; } = true;
        public event EventHandler<EventArgs> Click;

        public ForskButton()
        {
            Height = 28;
            Cursor = Cursors.Pointer;
            MouseDown += (s, e) =>
            {
                if (!EnabledClick || e.Buttons != MouseButtons.Primary) return;
                Click?.Invoke(this, EventArgs.Empty);
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var w = Width;
            var h = Height;
            if (w < 1 || h < 1) return;
            var filled = Filled && EnabledClick;
            var bg = filled ? Colors.Black : Colors.White;
            var fg = !EnabledClick ? Color.FromArgb(160, 160, 160) : filled ? Colors.White : Color.FromArgb(20, 20, 20);
            g.FillRectangle(bg, 0, 0, w, h);
            if (!filled)
                g.DrawRectangle(new Pen(Color.FromArgb(220, 220, 220), 1), 0, 0, w - 1, h - 1);
            var font = SystemFonts.Default(12);
            var size = g.MeasureString(font, Text ?? "");
            g.DrawText(font, fg, (w - size.Width) / 2f, (h - size.Height) / 2f, Text ?? "");
        }
    }
}
