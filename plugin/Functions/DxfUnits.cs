using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The units a DXF is drawn in, for Forsk's DXF import into a millimetre
/// model. $INSUNITS says them. When it is missing, 0, or not a unit plans are
/// drawn in, the size of the drawing does, and the receipt says it was a
/// guess. Rhino's own import takes the units from its AutoCAD import setting,
/// not from the file (a mm DXF comes in 10x with the setting on cm), so what
/// it scaled by is measured here and dxf_import sets it right. Pure, tested
/// headless.
/// </summary>
public static class DxfUnits
{
    public sealed class Reading
    {
        /// <summary>$INSUNITS as the file has it, null when it is missing.</summary>
        public int? InsUnits;
        public string Unit = "mm";
        /// <summary>Millimetres per drawing unit: the scale into a mm model.</summary>
        public double Mm = 1.0;
        /// <summary>The file did not say; the unit is read from the drawing's size.</summary>
        public bool Guessed;
        /// <summary>Widest extent of the model-space curves, in drawing units. 0 with none to measure.</summary>
        public double Span;
    }

    // $INSUNITS codes plans are drawn in, and their millimetres.
    static readonly Dictionary<int, KeyValuePair<string, double>> Units = new Dictionary<int, KeyValuePair<string, double>>
    {
        [1] = new KeyValuePair<string, double>("in", 25.4),
        [2] = new KeyValuePair<string, double>("ft", 304.8),
        [4] = new KeyValuePair<string, double>("mm", 1.0),
        [5] = new KeyValuePair<string, double>("cm", 10.0),
        [6] = new KeyValuePair<string, double>("m", 1000.0)
    };

    /// <summary>A drawing narrower than this many units is in metres: no building is half a metre across.</summary>
    public const double MetresBelow = 500.0;
    /// <summary>How far the measured scale may be off a unit step and still be read as that step.</summary>
    public const double Near = 1.4;

    public static Reading Read(byte[] bytes)
    {
        var pairs = DxfText.Pairs(Encoding.ASCII.GetString(bytes));
        var reading = new Reading { Span = Span(pairs) };
        if (int.TryParse(DxfText.HeaderValue(pairs, "$INSUNITS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            reading.InsUnits = code;
        if (reading.InsUnits.HasValue && Units.TryGetValue(code, out var unit))
        {
            reading.Unit = unit.Key;
            reading.Mm = unit.Value;
            return reading;
        }
        // Not stated. Plans are drawn in mm or in m, and their size tells the
        // two apart. With nothing to measure it is mm, as Forsk models are.
        reading.Guessed = true;
        if (reading.Span > 0 && reading.Span < MetresBelow)
        {
            reading.Unit = "m";
            reading.Mm = 1000.0;
        }
        return reading;
    }

    /// <summary>
    /// What Rhino's import scaled the drawing by: the imported curves' extent
    /// over the DXF's, read as the nearest unit step into a model whose unit
    /// is <paramref name="modelMm"/> millimetres. 0 when it cannot be told:
    /// nothing to measure, or no step near.
    /// </summary>
    public static double Applied(double importedSpan, double sourceSpan, double modelMm)
    {
        if (importedSpan <= 0 || sourceSpan <= 0 || modelMm <= 0) return 0;
        var ratio = importedSpan / sourceSpan;
        foreach (var unit in Units.Values)
        {
            var step = unit.Value / modelMm;
            if (ratio > step / Near && ratio < step * Near) return step;
        }
        return 0;
    }

    /// <summary>The receipt sentence: the units read and the scale applied, and whether it was a guess.</summary>
    public static string Line(Reading reading)
    {
        var scale = "scale ×" + reading.Mm.ToString("0.###", CultureInfo.InvariantCulture);
        var header = "$INSUNITS " + (reading.InsUnits.HasValue ? reading.InsUnits.Value.ToString(CultureInfo.InvariantCulture) : "missing");
        if (!reading.Guessed)
            return "Units " + reading.Unit + " (" + header + "), " + scale + ".";
        return "Units not stated (" + header + "): guessed " + reading.Unit + " from its size, "
            + reading.Span.ToString("0.#", CultureInfo.InvariantCulture) + " across, " + scale + ". Check a known length.";
    }

    /// <summary>
    /// Widest extent of the curves in model space: LINE ends, polyline
    /// vertices, SPLINE control points, CIRCLE and ARC by centre and radius.
    /// Texts and block inserts are left out: Rhino's objects for them are
    /// wider than their insertion points.
    /// </summary>
    static double Span(List<KeyValuePair<string, string>> pairs)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(double px, double py)
        {
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        string section = null;
        string kind = null;
        double x = 0, centreX = 0, centreY = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var code = pairs[i].Key;
            var value = pairs[i].Value;
            if (code == "0")
            {
                if (value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Key == "2")
                    section = pairs[i + 1].Value.Trim();
                kind = section == "ENTITIES" ? value.Trim() : null;
                continue;
            }
            var round = kind == "CIRCLE" || kind == "ARC";
            if (!round && kind != "LINE" && kind != "LWPOLYLINE" && kind != "VERTEX" && kind != "SPLINE") continue;
            switch (code)
            {
                // Paper space: not part of the model.
                case "67": if (DxfText.Number(value) != 0) kind = null; break;
                case "10": x = DxfText.Number(value); break;
                case "20":
                    centreX = x;
                    centreY = DxfText.Number(value);
                    Add(centreX, centreY);
                    break;
                case "11": if (kind == "LINE") x = DxfText.Number(value); break;
                case "21": if (kind == "LINE") Add(x, DxfText.Number(value)); break;
                case "40":
                    if (!round) break;
                    var r = DxfText.Number(value);
                    Add(centreX - r, centreY - r);
                    Add(centreX + r, centreY + r);
                    break;
            }
        }
        return maxX < minX ? 0 : Math.Max(maxX - minX, maxY - minY);
    }
}
