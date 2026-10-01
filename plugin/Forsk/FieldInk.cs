using System;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>Composer colours as plain RGB, so a headless test can check the contrast.</summary>
    static class FieldInk
    {
        public static readonly int[] Background = { 255, 255, 255 };
        public static readonly int[] Text = { 28, 25, 23 };

        public static double Contrast(int[] a, int[] b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        static double Luminance(int[] c)
        {
            double Lin(int v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c[0]) + 0.7152 * Lin(c[1]) + 0.0722 * Lin(c[2]);
        }
    }
}
