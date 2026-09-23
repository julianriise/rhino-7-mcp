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

        public static void Rect(Graphics g, Color fill, float x, float y, float w, float h, bool stroke)
        {
            if (w < 1 || h < 1) return;
            g.AntiAlias = false;
            g.FillRectangle(fill, x, y, w, h);
            if (!stroke) return;
            using (var pen = new Pen(Line, 1))
                g.DrawRectangle(pen, x, y, w - 1, h - 1);
        }

        public static void Icon(Graphics g, ForskIcon icon, RectangleF box, Color color)
        {
            g.AntiAlias = true;
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
                Input = new Font(reg, 14);
                IsGeist = true;
            }
            catch
            {
                Body = SystemFonts.Default(13);
                Caption = SystemFonts.Default(12);
                Ui = SystemFonts.Bold(12);
                Button = SystemFonts.Bold(13);
                Mark = SystemFonts.Bold(15);
                Input = SystemFonts.Default(14);
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

        public ForskFill()
        {
            BackgroundColor = ForskPaint.Paper;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            ForskPaint.Rect(e.Graphics, Fill, 0, 0, Width, Height, Stroke);
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
            float w = Width;
            float h = Height;
            if (w < 2 || h < 2) return;
            var fill = !_enabled ? ForskPaint.Track : _hover ? ForskPaint.InkHover : ForskPaint.Ink;
            var fg = !_enabled ? ForskPaint.Quiet : Colors.White;
            ForskPaint.Rect(g, fill, 0, 0, w, h, false);
            g.AntiAlias = true;
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
            float w = Width;
            float h = Height;
            if (w < 2 || h < 2) return;
            bool hot = _enabled && _armed;
            var fill = hot
                ? (_hover ? ForskPaint.InkHover : ForskPaint.Ink)
                : (_hover ? ForskPaint.Track : Colors.White);
            ForskPaint.Rect(g, fill, 0, 0, w, h, !hot);
            g.AntiAlias = true;
            var ink = hot ? Colors.White : ForskPaint.Quiet;
            ForskPaint.Icon(g, ForskIcon.ArrowUp, new RectangleF((w - 14) / 2f, (h - 14) / 2f, 14, 14), ink);
        }
    }

    /// <summary>
    /// Brand, connection, and selection line. Drawn, so they use Geist.
    /// An Eto Label on Mac is an NSTextField and ignores a face loaded from a file.
    /// </summary>
    sealed class ForskHeader : Drawable
    {
        string _status = "Type mcpstart";
        string _target = "Click something in the model.";
        bool _live;
        readonly SolidBrush _quiet = new SolidBrush(ForskPaint.Quiet);
        readonly SolidBrush _ink = new SolidBrush(ForskPaint.Ink);
        FormattedText _targetText;

        public ForskHeader()
        {
            BackgroundColor = ForskPaint.Paper;
            Height = 48;
        }

        public void SetStatus(bool live, string status)
        {
            _live = live;
            _status = status ?? "";
            Invalidate();
        }

        public void SetTarget(string target)
        {
            _target = string.IsNullOrEmpty(target) ? "Click something in the model." : target;
            Reflow(Width > 40 ? Width : 260);
        }

        public void Reflow(float width)
        {
            if (width < 40) width = 40;
            _targetText = new FormattedText
            {
                Font = ForskType.Body,
                Text = _target,
                ForegroundBrush = _target.StartsWith("Target:") ? _ink : _quiet,
                Wrap = FormattedTextWrapMode.Word,
                Alignment = FormattedTextAlignment.Left,
                MaximumWidth = width
            };
            var size = _targetText.Measure();
            Width = (int)Math.Ceiling(width);
            Height = (int)Math.Ceiling(22 + 8 + Math.Max(16, size.Height));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.PixelOffsetMode = PixelOffsetMode.None;
            if (Width < 8 || _targetText == null) return;

            var brand = g.MeasureString(ForskType.Mark, "Forsk");
            var status = g.MeasureString(ForskType.Caption, _status);
            float row = Math.Max(brand.Height, status.Height);
            g.DrawText(ForskType.Mark, ForskPaint.Ink, 0, (row - brand.Height) / 2f, "Forsk");

            const float dot = 6f;
            const float gap = 6f;
            float statusW = dot + gap + status.Width;
            float sx = Math.Max(brand.Width + 12f, Width - statusW);
            float sy = (row - status.Height) / 2f;
            g.FillEllipse(_live ? ForskPaint.Live : ForskPaint.IdleDot, sx, sy + (status.Height - dot) / 2f, dot, dot);
            g.DrawText(ForskType.Caption, ForskPaint.Quiet, sx + dot + gap, sy, _status);
            g.DrawText(_targetText, new PointF(0, row + 8f));
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
                ForskPaint.Rect(g, Colors.White, 0, 0, Width, Height, true);
            g.AntiAlias = true;
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

    /// <summary>
    /// One line at rest. Grows with wrapped text up to five lines, then the
    /// field scrolls. Enter sends. Shift+Enter keeps a line break.
    /// </summary>
    sealed class ForskComposer : PixelLayout
    {
        const int LineLimit = 5;
        const int TextX = 14;
        const int TextY = 12;
        const int Button = 28;
        const int Gap = 8;
        const int Bottom = 10;
        readonly ForskFill _card;
        bool _placing;

        public TextArea Input { get; private set; }
        public ForskIconButton Send { get; private set; }

        public ForskComposer()
        {
            BackgroundColor = ForskPaint.Paper;
            _card = new ForskFill();
            // The native face is applied later. A stream font assigned here
            // is not an NSFont, and the field falls back to a tiny size.
            Input = new TextArea
            {
                Wrap = true,
                AcceptsReturn = true,
                AcceptsTab = false,
                SpellCheck = false,
                TextReplacements = TextReplacements.None,
                BackgroundColor = Colors.White,
                TextColor = ForskPaint.Ink,
                Font = SystemFonts.Default(14)
            };
            Send = new ForskIconButton();
            Add(_card, 0, 0);
            Add(Input, TextX, TextY);
            Add(Send, 0, 0);
            Input.TextChanged += (s, e) =>
            {
                Send.Armed = (Input.Text ?? "").Trim().Length > 0;
                Place();
            };
            SizeChanged += (s, e) => Place();
            Place();
        }

        public void Place()
        {
            if (_placing) return;
            _placing = true;
            try
            {
                int w = Math.Max(Width, 160);
                int textW = Math.Max(40, w - TextX - 12);
                int textH = Lines(Input.Text, textW) * LinePx();
                int h = TextY + textH + Gap + Button + Bottom;
                if (Height != h) Height = h;
                _card.Size = new Size(w, h);
                Input.Size = new Size(textW, textH);
                Move(Input, TextX, TextY);
                int rowY = h - Bottom - Button;
                Send.Size = new Size(Button, Button);
                int sendX = w - 10 - Button;
                Move(Send, sendX, rowY);
            }
            finally
            {
                _placing = false;
            }
        }

        static int LinePx()
        {
            int line = (int)Math.Ceiling(ForskType.Input.MeasureString("Mg").Height);
            if (line < 16) return 18;
            if (line > 28) return 20;
            return line;
        }

        static int Lines(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            float limit = Math.Max(8, width - 8);
            int count = 0;
            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                float wide = part.Length == 0 ? 0 : ForskType.Input.MeasureString(part).Width;
                count += Math.Max(1, (int)Math.Ceiling(wide / limit));
            }
            if (count < 1) count = 1;
            if (count > LineLimit) return LineLimit;
            return count;
        }
    }
}
