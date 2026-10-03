using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// One sheet flattened for DWG/DXF, without a document: the export layers,
/// the layer each forsk:role goes to, the DWG lineweight a pen snaps to, and
/// the stroke a ribbon was drawn from. A sheet's strokes are filled ribbons
/// so the Mac preview shows their weight; the export draws the centreline
/// instead, at the pen's lineweight. Coordinates are paper millimetres.
/// </summary>
public static class SheetFlat
{
    /// <summary>The source curve of a stroke ribbon, in the sheet's model millimetres (Encode).</summary>
    public const string StrokeKey = "forsk:stroke";
    /// <summary>The stroke's pen width in paper millimetres.</summary>
    public const string PenKey = "forsk:pen";
    public const string Misc = "A-ANNO-MISC";

    public sealed class LayerDef
    {
        public string Name;
        /// <summary>The layer's lineweight in mm. Pieces without their own weight print at it.</summary>
        public double WeightMm;
        public string What;
    }

    public static readonly IReadOnlyList<LayerDef> Layers = new[]
    {
        Layer("A-WALL-CUT", 0.50, "cut outlines (plan cut, section cut)"),
        Layer("A-WALL-PATT", 0.00, "poché and section fills"),
        Layer("A-SYMB", 0.18, "door and window symbols, north arrow, section markers"),
        Layer("A-ELEV", 0.18, "projection and beyond lines, facade lines"),
        Layer("A-GRND", 0.50, "ground lines"),
        Layer("A-ANNO-DIMS", 0.13, "dimension lines, ticks, values"),
        Layer("A-ANNO-TEXT", 0.13, "tags, marks, levels, lists"),
        Layer("A-ANNO-TTLB", 0.25, "title block, frame, scale bar"),
        Layer(Misc, 0.13, "a piece whose role has no layer")
    };

    /// <summary>Every forsk:role a sheet bakes, to its export layer. A role missing here goes to A-ANNO-MISC.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByRole = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cut"] = "A-WALL-CUT",
        ["section_fill"] = "A-WALL-PATT",
        ["symbol"] = "A-SYMB",
        ["section_marker"] = "A-SYMB",
        ["north"] = "A-SYMB",
        ["greyscale"] = "A-ELEV",
        ["beyond"] = "A-ELEV",
        ["roof_outline"] = "A-ELEV",
        ["ground_line"] = "A-GRND",
        ["dimension"] = "A-ANNO-DIMS",
        ["free_height"] = "A-ANNO-DIMS",
        ["level"] = "A-ANNO-TEXT",
        ["opening_mark"] = "A-ANNO-TEXT",
        ["room_tag"] = "A-ANNO-TEXT",
        ["room_leader"] = "A-ANNO-TEXT",
        ["schedule_title"] = "A-ANNO-TEXT",
        ["schedule_head"] = "A-ANNO-TEXT",
        ["schedule_cell"] = "A-ANNO-TEXT",
        ["schedule_line"] = "A-ANNO-TEXT",
        ["schedule_note"] = "A-ANNO-TEXT",
        ["title_block"] = "A-ANNO-TTLB",
        ["title_cell"] = "A-ANNO-TTLB",
        ["scale_bar"] = "A-ANNO-TTLB",
        ["scale_bar_label"] = "A-ANNO-TTLB"
    };

    /// <summary>The lineweights DWG and DXF can store, in mm.</summary>
    public static readonly IReadOnlyList<double> Lineweights = new[]
    {
        0.00, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53,
        0.60, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11
    };

    public static string LayerFor(string role)
    {
        return role != null && ByRole.TryGetValue(role, out var layer) ? layer : Misc;
    }

    public static LayerDef Def(string name)
    {
        return Layers.FirstOrDefault(l => l.Name == name) ?? Layers.Last();
    }

    /// <summary>The nearest stored lineweight. A tie takes the heavier one; none or below zero is 0.</summary>
    public static double Snap(double mm)
    {
        if (double.IsNaN(mm) || mm <= 0) return 0;
        var best = Lineweights[0];
        foreach (var weight in Lineweights)
            if (Math.Abs(weight - mm) <= Math.Abs(best - mm) + 1e-9) best = weight;
        return best;
    }

    /// <summary>A straight piece (x0 y0 x1 y1) or a three-point arc (start, a point on it, end).</summary>
    public sealed class Seg
    {
        public bool Arc;
        public double[] P;

        public static Seg Line(double x0, double y0, double x1, double y1) => new Seg { P = new[] { x0, y0, x1, y1 } };

        public static Seg ArcOf(double x0, double y0, double xm, double ym, double x1, double y1) =>
            new Seg { Arc = true, P = new[] { x0, y0, xm, ym, x1, y1 } };
    }

    /// <summary>"L x,y x,y|A x,y x,y x,y": what Decode reads back.</summary>
    public static string Encode(IEnumerable<Seg> segs)
    {
        var parts = new List<string>();
        foreach (var seg in segs ?? Enumerable.Empty<Seg>())
        {
            if (seg?.P == null || seg.P.Length != (seg.Arc ? 6 : 4)) continue;
            var sb = new StringBuilder(seg.Arc ? "A" : "L");
            for (var i = 0; i < seg.P.Length; i += 2)
                sb.Append(' ').Append(Num(seg.P[i])).Append(',').Append(Num(seg.P[i + 1]));
            parts.Add(sb.ToString());
        }
        return string.Join("|", parts);
    }

    /// <summary>The pieces of an encoded stroke. A piece that does not read is skipped.</summary>
    public static List<Seg> Decode(string text)
    {
        var segs = new List<Seg>();
        foreach (var part in (text ?? "").Split('|'))
        {
            var bits = part.Trim().Split(' ');
            if (bits.Length < 3 || (bits[0] != "L" && bits[0] != "A")) continue;
            var arc = bits[0] == "A";
            if (bits.Length != (arc ? 4 : 3)) continue;
            var p = new List<double>();
            foreach (var pair in bits.Skip(1))
            {
                var xy = pair.Split(',');
                if (xy.Length != 2
                    || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    p = null;
                    break;
                }
                p.Add(x);
                p.Add(y);
            }
            if (p != null) segs.Add(new Seg { Arc = arc, P = p.ToArray() });
        }
        return segs;
    }

    /// <summary>A plan affine map: x' = A x + B y + C, y' = D x + E y + F. The detail's model-to-page map, flattened.</summary>
    public struct Affine
    {
        public double A, B, C, D, E, F;

        public void Apply(double x, double y, out double ox, out double oy)
        {
            ox = A * x + B * y + C;
            oy = D * x + E * y + F;
        }

        /// <summary>How much a length grows: 1/50 at 1:50. Text heights scale by it.</summary>
        public double Scale => Math.Sqrt(Math.Abs(A * E - B * D));
    }

    public static Seg Map(Seg seg, Affine map)
    {
        var p = new double[seg.P.Length];
        for (var i = 0; i < p.Length; i += 2)
            map.Apply(seg.P[i], seg.P[i + 1], out p[i], out p[i + 1]);
        return new Seg { Arc = seg.Arc, P = p };
    }

    /// <summary>"Holmen A-20-001 Plan.dwg". Characters a file name cannot hold become a dash.</summary>
    public static string FileName(string project, string number, string title, string format)
    {
        var words = new[] { project, number, title }
            .Select(Clean)
            .Where(w => w.Length > 0);
        var stem = string.Join(" ", words);
        if (stem.Length == 0) stem = "Forsk sheet";
        return stem + "." + (format ?? "dwg").Trim().ToLowerInvariant();
    }

    /// <summary>The folder the window writes a set to: "Holmen DWG".</summary>
    public static string FolderName(string project, string format)
    {
        var stem = Clean(project);
        return (stem.Length == 0 ? "Forsk" : stem) + " " + (format ?? "dwg").Trim().ToUpperInvariant();
    }

    static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in text.Trim())
            sb.Append(ch == '/' || ch == '\\' || ch == ':' || Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        var clean = sb.ToString().Trim().Trim('.');
        return clean.Length > 60 ? clean.Substring(0, 60).Trim() : clean;
    }

    static string Num(double value)
    {
        return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    static LayerDef Layer(string name, double weight, string what)
    {
        return new LayerDef { Name = name, WeightMm = weight, What = what };
    }
}
