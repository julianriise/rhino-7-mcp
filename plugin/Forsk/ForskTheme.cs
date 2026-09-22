using System;
using System.Collections.Generic;
using System.IO;
using Eto.Drawing;
using Eto.Forms;

namespace RhinoMCPPlugin.Forsk
{
    static class ForskPaint
    {
        public static readonly Color Paper = Color.FromArgb(246, 245, 242);
        public static readonly Color Ink = Color.FromArgb(28, 25, 23);
        public static readonly Color InkHover = Color.FromArgb(0, 0, 0);
        public static readonly Color Quiet = Color.FromArgb(120, 113, 108);
        public static readonly Color Line = Color.FromArgb(228, 224, 216);
        public static readonly Color Track = Color.FromArgb(238, 235, 227);
        public static readonly Color Live = Color.FromArgb(44, 122, 75);
        public static readonly Color IdleDot = Color.FromArgb(201, 196, 186);
        public static readonly Color Clay = Color.FromArgb(159, 45, 45);

        public static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2f));
            if (w <= 0 || h <= 0) return path;
            if (r < 0.5f)
            {
                path.AddRectangle(x, y, w, h);
                return path;
            }
            float d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Icon(Graphics g, ForskIcon icon, RectangleF box, Color color)
        {
            using (g.SaveTransformState())
            using (var path = new GraphicsPath())
            using (var pen = new Pen(color, 1.6f))
            {
                pen.LineCap = PenLineCap.Round;
                pen.LineJoin = PenLineJoin.Round;
                g.TranslateTransform(box.X, box.Y);
                g.ScaleTransform(box.Width / 16f, box.Height / 16f);
                if (icon == ForskIcon.ArrowUp)
                {
                    path.MoveTo(8, 13);
                    path.LineTo(8, 3.5f);
                    path.MoveTo(4, 7.5f);
                    path.LineTo(8, 3.5f);
                    path.LineTo(12, 7.5f);
                }
                else if (icon == ForskIcon.Check)
                {
                    path.MoveTo(3.5f, 8.4f);
                    path.LineTo(6.6f, 11.4f);
                    path.LineTo(12.6f, 4.4f);
                }
                else
                {
                    path.MoveTo(4.5f, 4.5f);
                    path.LineTo(11.5f, 11.5f);
                    path.MoveTo(11.5f, 4.5f);
                    path.LineTo(4.5f, 11.5f);
                }
                g.DrawPath(pen, path);
            }
        }
    }

    enum ForskIcon
    {
        ArrowUp,
        Check,
        Cross
    }

    static class ForskType
    {
        static readonly List<MemoryStream> Keep = new List<MemoryStream>();
        static bool _ready;

        public static Font Body { get; private set; }
        public static Font Caption { get; private set; }
        public static Font Ui { get; private set; }
        public static Font Button { get; private set; }
        public static Font Mark { get; private set; }
        public static Font Input { get; private set; }
        public static bool IsGeist { get; private set; }

        public static void Load()
        {
            if (_ready) return;
            _ready = true;
            try
            {
                var regular = Open("RhinoMCPPlugin.Fonts.Geist-Regular.otf");
                var medium = Open("RhinoMCPPlugin.Fonts.Geist-Medium.otf");
                FontTypeface reg;
                FontTypeface med;
                try
                {
                    var family = FontFamily.FromStreams(regular, medium);
                    reg = Pick(family, "Regular");
                    med = Pick(family, "Medium") ?? reg;
                    if (reg == null) throw new InvalidOperationException("Geist has no typeface");
                }
                catch
                {
                    regular.Position = 0;
                    reg = new FontTypeface(regular);
                    med = reg;
                }
                Body = new Font(reg, 13);
                Caption = new Font(reg, 12);
                Ui = new Font(med, 12);
                Button = new Font(med, 13);
                Mark = new Font(med, 15);
                Input = SystemFonts.Default(16);
                IsGeist = true;
            }
            catch
            {
                Body = SystemFonts.Default(13);
                Caption = SystemFonts.Default(12);
                Ui = SystemFonts.Bold(12);
                Button = SystemFonts.Bold(13);
                Mark = SystemFonts.Bold(15);
                Input = SystemFonts.Default(16);
                IsGeist = false;
            }
        }

        static FontTypeface Pick(FontFamily family, string token)
        {
            FontTypeface fallback = null;
            foreach (var face in family.Typefaces)
            {
                var name = face.Name ?? "";
                if (name.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (fallback == null) fallback = face;
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return face;
            }
            return fallback;
        }

        static Stream Open(string name)
        {
            var src = typeof(ForskType).Assembly.GetManifestResourceStream(name);
            if (src == null) throw new FileNotFoundException(name);
            var copy = new MemoryStream();
            src.CopyTo(copy);
            src.Dispose();
            copy.Position = 0;
            Keep.Add(copy);
            return copy;
        }
    }

    sealed class ForskFill : Drawable
    {
        public Color Fill = Colors.White;
        public bool Stroke = true;
        public float Radius = 12;

        public ForskFill()
        {
            BackgroundColor = ForskPaint.Paper;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            if (Width < 2 || Height < 2) return;
            using (var path = ForskPaint.RoundRect(0.5f, 0.5f, Width - 1f, Height - 1f, Radius))
            {
                g.FillPath(Fill, path);
                if (!Stroke) return;
                using (var pen = new Pen(ForskPaint.Line, 1))
                    g.DrawPath(pen, path);
            }
        }
    }

    sealed class ForskButton : Drawable
    {
        string _text = "";
        bool _enabled = true;
        bool _hover;

        public string Text
        {
            get { return _text; }
            set { _text = value ?? ""; Invalidate(); }
        }

        public bool EnabledClick
        {
            get { return _enabled; }
            set { _enabled = value; Invalidate(); }
        }

        public event EventHandler<EventArgs> Click;

        public ForskButton()
        {
            Height = 36;
            BackgroundColor = ForskPaint.Paper;
            Cursor = Cursors.Pointer;
            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            MouseDown += (s, e) =>
            {
                if (!_enabled || e.Buttons != MouseButtons.Primary) return;
                var click = Click;
                if (click != null) click(this, EventArgs.Empty);
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            float w = Width;
            float h = Height;
            if (w < 2 || h < 2) return;
            var fill = !_enabled ? ForskPaint.Track : _hover ? ForskPaint.InkHover : ForskPaint.Ink;
            var fg = !_enabled ? ForskPaint.Quiet : Colors.White;
            using (var path = ForskPaint.RoundRect(0.5f, 0.5f, w - 1f, h - 1f, 8))
                g.FillPath(fill, path);
            var size = g.MeasureString(ForskType.Button, _text);
            g.DrawText(ForskType.Button, fg, (w - size.Width) / 2f, (h - size.Height) / 2f, _text);
        }
    }

    sealed class ForskIconButton : Drawable
    {
        bool _enabled = true;
        bool _armed;
        bool _hover;

        public bool EnabledClick
        {
            get { return _enabled; }
            set { if (_enabled == value) return; _enabled = value; Invalidate(); }
        }

        public bool Armed
        {
            get { return _armed; }
            set { if (_armed == value) return; _armed = value; Invalidate(); }
        }

        public event EventHandler<EventArgs> Click;

        public ForskIconButton()
        {
            Size = new Size(28, 28);
            BackgroundColor = Colors.White;
            Cursor = Cursors.Pointer;
            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            MouseDown += (s, e) =>
            {
                if (!_enabled || e.Buttons != MouseButtons.Primary) return;
                var click = Click;
                if (click != null) click(this, EventArgs.Empty);
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            float w = Width;
            float h = Height;
            if (w < 2 || h < 2) return;
            bool hot = _enabled && _armed;
            using (var path = ForskPaint.RoundRect(0.5f, 0.5f, w - 1f, h - 1f, 8))
            {
                if (hot)
                    g.FillPath(_hover ? ForskPaint.InkHover : ForskPaint.Ink, path);
                else
                {
                    g.FillPath(_hover ? ForskPaint.Track : Colors.White, path);
                    using (var pen = new Pen(ForskPaint.Line, 1))
                        g.DrawPath(pen, path);
                }
            }
            var ink = hot ? Colors.White : ForskPaint.Quiet;
            ForskPaint.Icon(g, ForskIcon.ArrowUp, new RectangleF((w - 14) / 2f, (h - 14) / 2f, 14, 14), ink);
        }
    }

    sealed class ForskModes : Drawable
    {
        static readonly string[] Labels = { "Build", "Edit", "Sheets" };
        int _hover = -1;

        public ForskMode Mode { get; set; }
        public event EventHandler<EventArgs> Picked;

        public ForskModes()
        {
            Height = 34;
            BackgroundColor = ForskPaint.Paper;
            Cursor = Cursors.Pointer;
            MouseLeave += (s, e) => { _hover = -1; Invalidate(); };
            MouseMove += (s, e) =>
            {
                int next = Hit(e.Location.X);
                if (next == _hover) return;
                _hover = next;
                Invalidate();
            };
            MouseDown += (s, e) =>
            {
                if (e.Buttons != MouseButtons.Primary) return;
                int index = Hit(e.Location.X);
                if (index < 0) return;
                Mode = (ForskMode)index;
                Invalidate();
                var picked = Picked;
                if (picked != null) picked(this, EventArgs.Empty);
            };
        }

        int Hit(float x)
        {
            if (Width < 3) return -1;
            int index = (int)(x / (Width / 3f));
            if (index < 0) return 0;
            if (index > 2) return 2;
            return index;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            float w = Width;
            float h = Height;
            if (w < 3 || h < 3) return;
            using (var track = ForskPaint.RoundRect(0.5f, 0.5f, w - 1f, h - 1f, 9))
                g.FillPath(ForskPaint.Track, track);

            int selected = (int)Mode;
            float inset = 3;
            float seg = (w - inset * 2) / 3f;
            float x = inset + seg * selected;
            using (var pill = ForskPaint.RoundRect(x, inset, seg, h - inset * 2, 7))
            {
                g.FillPath(Colors.White, pill);
                using (var pen = new Pen(ForskPaint.Line, 1))
                    g.DrawPath(pen, pill);
            }

            for (int i = 0; i < 3; i++)
            {
                bool on = i == selected;
                var font = on ? ForskType.Ui : ForskType.Caption;
                var color = on || i == _hover ? ForskPaint.Ink : ForskPaint.Quiet;
                var size = g.MeasureString(font, Labels[i]);
                float tx = inset + seg * i + (seg - size.Width) / 2f;
                float ty = (h - size.Height) / 2f;
                g.DrawText(font, color, tx, ty, Labels[i]);
            }
        }
    }

    sealed class ForskDot : Drawable
    {
        public bool On;

        public ForskDot()
        {
            Size = new Size(14, 14);
            BackgroundColor = ForskPaint.Paper;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            float d = 6;
            float x = (Width - d) / 2f;
            float y = (Height - d) / 2f;
            g.FillEllipse(On ? ForskPaint.Live : ForskPaint.IdleDot, x, y, d, d);
        }
    }

    sealed class ForskMessage : Drawable
    {
        readonly SolidBrush _brush;
        FormattedText _text;

        public string Role { get; private set; }
        public string Text { get; private set; }
        public bool Error { get; private set; }

        public ForskMessage(string role, string text)
        {
            Role = role;
            Text = text ?? "";
            Error = role == "receipt" && Text.IndexOf(" · error", StringComparison.Ordinal) >= 0;
            var color = role == "receipt" || role == "hint" ? ForskPaint.Quiet : ForskPaint.Ink;
            _brush = new SolidBrush(color);
            BackgroundColor = ForskPaint.Paper;
        }

        public void Reflow(float width)
        {
            if (width < 40) width = 40;
            var font = Role == "assistant" || Role == "user" ? ForskType.Body : ForskType.Caption;
            float gutter = Role == "receipt" ? 22 : 0;
            float limit = Role == "user" ? Math.Max(96, width * 0.86f) : Math.Max(40, width - gutter);
            float natural = 0;
            var lines = Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                float measured = font.MeasureString(line).Width;
                if (measured > natural) natural = measured;
            }
            float textW = Math.Min(limit, Math.Max(8, natural + 2));
            _text = new FormattedText
            {
                Font = font,
                Text = Text,
                ForegroundBrush = _brush,
                Wrap = FormattedTextWrapMode.Word,
                Alignment = FormattedTextAlignment.Left,
                MaximumWidth = textW
            };
            var size = _text.Measure();
            if (Role == "user")
            {
                Width = (int)Math.Ceiling(size.Width + 24);
                Height = (int)Math.Ceiling(Math.Max(28, size.Height + 16));
            }
            else
            {
                Width = (int)Math.Ceiling(width);
                Height = (int)Math.Ceiling(Math.Max(22, size.Height + 10));
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            if (Role == "user" && Width > 2 && Height > 2)
            {
                using (var path = ForskPaint.RoundRect(0.5f, 0.5f, Width - 1f, Height - 1f, 12))
                using (var pen = new Pen(ForskPaint.Line, 1))
                {
                    g.FillPath(Colors.White, path);
                    g.DrawPath(pen, path);
                }
            }
            if (_text == null) return;
            if (Role == "receipt")
            {
                var mark = Error ? ForskPaint.Clay : ForskPaint.Ink;
                var icon = Error ? ForskIcon.Cross : ForskIcon.Check;
                ForskPaint.Icon(g, icon, new RectangleF(1, 3, 14, 14), mark);
                g.DrawText(_text, new PointF(20, 2));
                return;
            }
            float x = Role == "user" ? 12 : 0;
            float y = Role == "user" ? 8 : 2;
            g.DrawText(_text, new PointF(x, y));
        }
    }

    sealed class ForskComposer : PixelLayout
    {
        readonly ForskFill _card;

        public TextBox Input { get; private set; }
        public ForskIconButton Send { get; private set; }

        public ForskComposer()
        {
            Height = 52;
            BackgroundColor = ForskPaint.Paper;
            _card = new ForskFill { Radius = 12 };
            Input = new TextBox
            {
                ShowBorder = false,
                BackgroundColor = Colors.White,
                TextColor = ForskPaint.Ink,
                Font = ForskType.Input,
                PlaceholderText = "Build the plan…"
            };
            Send = new ForskIconButton();
            Add(_card, 0, 0);
            Add(Input, 14, 8);
            Add(Send, 0, 8);
            Input.TextChanged += (s, e) => Send.Armed = (Input.Text ?? "").Trim().Length > 0;
            SizeChanged += (s, e) => Place();
            Place();
        }

        public void SetPlaceholder(ForskMode mode)
        {
            Input.PlaceholderText = mode == ForskMode.Edit
                ? "Edit the selection…"
                : mode == ForskMode.Sheets
                    ? "Draw the sheets…"
                    : "Build the plan…";
        }

        public void Place()
        {
            int w = Math.Max(Width, 160);
            const int h = 52;
            if (Height != h) Height = h;
            _card.Size = new Size(w, h);
            Send.Size = new Size(28, 28);
            int sendX = w - 10 - 28;
            Move(Send, sendX, (h - 28) / 2);
            const int fieldH = 36;
            Input.Size = new Size(Math.Max(40, sendX - 18), fieldH);
            Move(Input, 16, (h - fieldH) / 2);
        }
    }
}
