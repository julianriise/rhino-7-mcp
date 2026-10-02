using System;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// One window or door element, in the opening plane, in millimetres.
    /// X runs along the wall, Y is the wall thickness, Z is up. The frame
    /// face and depth stay put when the opening is resized; the members
    /// only get longer or wider. No RhinoCommon.
    /// </summary>
    public static class OpeningElement
    {
        public const double FrameFaceMm = 50.0;
        public const double FrameInsetMm = 1.0;
        public const double LeafMm = 40.0;
        public const double GlazeMm = 8.0;
        public const double ThresholdMm = 15.0;

        public sealed class Box
        {
            public double X0, X1, Y0, Y1, Z0, Z1;
            public double SpanX => X1 - X0;
            public double SpanY => Y1 - Y0;
            public double SpanZ => Z1 - Z0;
        }

        public sealed class Layout
        {
            public bool Window;
            public double Face;
            public double Depth;
            public double Z0;
            public double Z1;
            public double OuterHalf;
            public double InnerHalf;
            public double HalfThick;
            public Box Left;
            public Box Right;
            public Box Head;
            public Box Glass;
            public Box Leaf;
            public Box Swing;
        }

        /// <summary>
        /// Profile face. A larger opening does not thicken it. It shrinks
        /// only when the opening cannot hold 50 mm and still leave a void.
        /// </summary>
        public static double FrameFace(double outerHalf, double clear, bool window)
        {
            var face = FrameFaceMm;
            if (outerHalf - face < 15)
                face = Math.Max(12.0, outerHalf - 15);
            var maxByHeight = window ? (clear - 30.0) / 2.0 : clear - 30.0;
            if (maxByHeight < face)
                face = Math.Max(12.0, maxByHeight);
            return face;
        }

        public static bool TryLayout(
            bool window,
            double width,
            double sill,
            double head,
            double thickness,
            double pad,
            out Layout layout)
        {
            layout = null;
            var z0 = sill + FrameInsetMm;
            var z1 = head - FrameInsetMm;
            var outerHalf = width * 0.5 + Math.Max(pad, 0) - FrameInsetMm;
            var halfThick = thickness * 0.5 - FrameInsetMm;
            var clear = z1 - z0;
            if (clear < 80 || outerHalf < 30 || halfThick < 8)
                return false;

            var face = FrameFace(outerHalf, clear, window);
            var innerHalf = outerHalf - face;
            var minClear = window ? face * 2 + 30 : face + 30;
            if (innerHalf < 15 || clear < minClear)
                return false;

            var frameZ0 = window ? z0 + face - 0.2 : z0;
            layout = new Layout
            {
                Window = window,
                Face = face,
                Depth = halfThick * 2,
                Z0 = z0,
                Z1 = z1,
                OuterHalf = outerHalf,
                InnerHalf = innerHalf,
                HalfThick = halfThick,
                Left = Make(-outerHalf, -innerHalf, -halfThick, halfThick, frameZ0, z1),
                Right = Make(innerHalf, outerHalf, -halfThick, halfThick, frameZ0, z1),
                Head = Make(-outerHalf, outerHalf, -(halfThick - 0.4), halfThick - 0.4, z1 - face, z1)
            };

            GlassSpan(innerHalf, z0, z1, face, out var gx0, out var gx1, out var gz0, out var gz1);
            var glaze = Math.Min(GlazeMm * 0.5, Math.Max(3, halfThick * 0.45));
            layout.Glass = Make(gx0, gx1, -glaze, glaze, gz0, gz1);

            if (!window)
            {
                LeafSpan(innerHalf, z0, z1, face, true, out var lx0, out var lx1, out var lz0, out var lz1);
                var leafThick = Math.Min(LeafMm, Math.Max(16.0, halfThick * 0.5));
                var outer = Math.Max(4.0, halfThick - 0.8);
                var depth = Math.Min(Math.Max(4.0, leafThick), outer);
                layout.Leaf = Make(lx0, lx1, outer - depth, outer, lz0, lz1);
                const double hinge = 22.0;
                var hingeAt = innerHalf - 1;
                layout.Swing = Make(hingeAt - hinge, hingeAt, outer - depth, outer, lz0, lz1);
            }

            return true;
        }

        /// <summary>Glass meets the inner frame: jamb to jamb, sill rail to head.</summary>
        public static void GlassSpan(
            double innerHalf, double z0, double z1, double face,
            out double x0, out double x1, out double gz0, out double gz1)
        {
            x0 = -innerHalf;
            x1 = innerHalf;
            gz0 = z0 + face;
            gz1 = z1 - face;
        }

        /// <summary>A hinged door leaf fills the void. Its swing uses the same Z.</summary>
        public static void LeafSpan(
            double innerHalf, double z0, double z1, double face, bool threshold,
            out double x0, out double x1, out double lz0, out double lz1)
        {
            var leafClear = threshold ? ThresholdMm + 0.5 : 0.5;
            lz0 = z0 + leafClear;
            lz1 = z1 - face + 2;
            if (lz1 - lz0 < 20) lz1 = z1 - 4;
            x0 = -(innerHalf - 1);
            x1 = innerHalf - 1;
        }

        static Box Make(double x0, double x1, double y0, double y1, double z0, double z1)
        {
            return new Box { X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1 };
        }
    }
}
