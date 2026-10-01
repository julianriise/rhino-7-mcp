using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// What a Forsk layer or object prints. Display colour stays the modelling
/// colour. Print colour is this table (layers) or the sheet's own pen
/// (drawings). A-ANALYSE and X-PLAN stay off the sheet (plot weight below 0).
/// PDF output uses <see cref="OutputColorMode"/> (ViewCaptureSettings.ColorMode).
/// </summary>
public static class PrintInk
{
    /// <summary>ViewCaptureSettings.ColorMode name the PDF path parses.</summary>
    public const string OutputColorMode = "PrintColor";

    public readonly struct Spec
    {
        public Spec(byte r, byte g, byte b, double weightMm)
        {
            R = r;
            G = g;
            B = b;
            WeightMm = weightMm;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        /// <summary>Millimetres. Below 0 is Rhino's "do not print".</summary>
        public double WeightMm { get; }
        public bool Prints => WeightMm >= 0;
        public bool Greyscale => R == G && G == B;
    }

    /// <summary>The colour an object prints, and where it comes from. Never the display colour.</summary>
    public readonly struct ObjectInk
    {
        public ObjectInk(string source, byte r, byte g, byte b)
        {
            Source = source;
            R = r;
            G = g;
            B = b;
        }

        /// <summary>"layer" or "object".</summary>
        public string Source { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public bool Greyscale => R == G && G == B;
    }

    static readonly Dictionary<string, Spec> ByName = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase)
    {
        ["A-WALL"] = new Spec(0, 0, 0, 0.35),
        ["A-FLOR"] = new Spec(80, 80, 80, 0.18),
        ["A-ROOF"] = new Spec(90, 90, 90, 0.18),
        ["A-OPEN"] = new Spec(70, 70, 70, 0.18),
        ["A-ROOM"] = new Spec(130, 130, 130, 0.13),
        ["A-STRU"] = new Spec(0, 0, 0, 0.35),
        ["A-ANNO"] = new Spec(0, 0, 0, 0.13),
        ["A-ANALYSE"] = new Spec(128, 128, 128, -1),
        ["X-EXIST"] = new Spec(140, 140, 140, 0.18),
        ["X-PLAN"] = new Spec(140, 140, 140, -1),
        ["S-DRAW"] = new Spec(0, 0, 0, 0.18),
        ["S-PLAN"] = new Spec(40, 40, 40, 0.18),
        ["S-ELEV-N"] = new Spec(40, 40, 40, 0.18),
        ["S-ELEV-E"] = new Spec(40, 40, 40, 0.18),
        ["S-ELEV-S"] = new Spec(40, 40, 40, 0.18),
        ["S-ELEV-W"] = new Spec(40, 40, 40, 0.18),
        ["cross-sections"] = new Spec(90, 90, 90, 0.13)
    };

    public static IEnumerable<string> Names => ByName.Keys;

    public static bool TryResolve(string fullPath, string name, out Spec spec)
    {
        spec = default;
        if (!string.IsNullOrEmpty(fullPath) && ByName.TryGetValue(fullPath, out spec))
            return true;
        if (!string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out spec))
            return true;
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (fullPath.StartsWith("S-DRAW::", StringComparison.OrdinalIgnoreCase)
            && ByName.TryGetValue("S-DRAW", out spec))
            return true;
        if (fullPath.StartsWith("A-OPEN::", StringComparison.OrdinalIgnoreCase)
            && ByName.TryGetValue("A-OPEN", out spec))
            return true;
        spec = default;
        return false;
    }

    /// <summary>Model geometry prints by its layer, not by its display colour.</summary>
    public static ObjectInk ForModel(Spec layer) => new ObjectInk("layer", layer.R, layer.G, layer.B);

    /// <summary>A sheet curve, hatch, or title prints its own pen.</summary>
    public static ObjectInk ForSheet(byte r, byte g, byte b) => new ObjectInk("object", r, g, b);
}
