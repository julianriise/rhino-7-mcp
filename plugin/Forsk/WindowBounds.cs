using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>A rectangle in screen points, top-left origin, as Eto reports screens and windows.</summary>
    public struct ScreenRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public ScreenRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double Right => X + Width;
        public double Bottom => Y + Height;
        public bool IsEmpty => !(Width > 0) || !(Height > 0) || double.IsNaN(X) || double.IsNaN(Y)
            || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Width) || double.IsInfinity(Height);

        public double Overlap(ScreenRect other)
        {
            var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
            var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0} {2:0}x{3:0}", X, Y, Width, Height);
        }
    }

    /// <summary>
    /// The Forsk window's remembered place. On every open it is clamped to a
    /// screen that is there now: the one it overlaps most, or the first screen
    /// when its own was unplugged. It never opens larger than that screen or
    /// partly off it. No RhinoCommon, so it tests headless.
    /// </summary>
    public static class WindowBounds
    {
        public const double DefaultWidth = 420;
        public const double DefaultHeight = 680;
        public const double MinWidth = 320;
        public const double MinHeight = 420;
        const double Margin = 24;

        /// <param name="saved">The last place, or null on a first open.</param>
        /// <param name="screens">The working areas of the screens now. The first is the main screen.</param>
        public static ScreenRect Clamp(ScreenRect? saved, IList<ScreenRect> screens)
        {
            var usable = new List<ScreenRect>();
            if (screens != null)
                foreach (var screen in screens)
                    if (!screen.IsEmpty) usable.Add(screen);
            if (usable.Count == 0)
                return saved.HasValue && !saved.Value.IsEmpty ? saved.Value : new ScreenRect(Margin, Margin, DefaultWidth, DefaultHeight);

            var main = usable[0];
            if (!saved.HasValue || saved.Value.IsEmpty)
                return Default(main);

            var place = saved.Value;
            var home = main;
            var best = 0.0;
            foreach (var screen in usable)
            {
                var overlap = place.Overlap(screen);
                if (overlap > best)
                {
                    best = overlap;
                    home = screen;
                }
            }
            if (best <= 0)
                return Fit(Default(main, place.Width, place.Height), main);
            return Fit(place, home);
        }

        /// <summary>The first open: the default size at the right of the main screen, below its top edge.</summary>
        public static ScreenRect Default(ScreenRect screen, double width = DefaultWidth, double height = DefaultHeight)
        {
            var w = Math.Max(MinWidth, Math.Min(width, screen.Width - 2 * Margin));
            var h = Math.Max(MinHeight, Math.Min(height, screen.Height - 2 * Margin));
            return Fit(new ScreenRect(screen.Right - w - Margin, screen.Y + Margin, w, h), screen);
        }

        static ScreenRect Fit(ScreenRect place, ScreenRect screen)
        {
            var w = Math.Min(Math.Max(place.Width, MinWidth), screen.Width);
            var h = Math.Min(Math.Max(place.Height, MinHeight), screen.Height);
            var x = Math.Min(Math.Max(place.X, screen.X), screen.Right - w);
            var y = Math.Min(Math.Max(place.Y, screen.Y), screen.Bottom - h);
            return new ScreenRect(x, y, w, h);
        }

        /// <summary>"x,y,w,h" as stored on disk. Null when the text is not four numbers.</summary>
        public static ScreenRect? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',');
            if (parts.Length != 4) return null;
            var values = new double[4];
            for (var i = 0; i < 4; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            var rect = new ScreenRect(values[0], values[1], values[2], values[3]);
            return rect.IsEmpty ? (ScreenRect?)null : rect;
        }

        public static string Format(ScreenRect rect)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##},{3:0.##}", rect.X, rect.Y, rect.Width, rect.Height);
        }
    }
}
