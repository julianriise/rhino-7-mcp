using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 N3: a vector PDF 1.4 of the flat sheets, the same pieces the DWG is
/// written from: strokes at their pen in paper mm, solid fills, and real
/// text in the standard Helvetica fonts (WinAnsiEncoding), so lines stay
/// sharp at any zoom and the title block selects. Compressed content
/// streams, a correct xref, and the project info as the document Info.
/// Pure, no Rhino document, so it tests headless.
/// </summary>
public static class SheetPdf
{
    /// <summary>Points per paper millimetre.</summary>
    public const double PtPerMm = 72.0 / 25.4;
    /// <summary>Helvetica's cap height per em: Rhino's text height is the capitals', the PDF's size the em.</summary>
    public const double CapHeight = 0.718;
    /// <summary>The line pitch of a text of several lines, per text height.</summary>
    public const double LinePitch = 1.6;

    public enum Align { Left, Centre, Right }

    public enum Vertical { Bottom, Middle, Top }

    /// <summary>An open or closed polyline in paper mm, at its pen.</summary>
    public sealed class Stroke
    {
        public List<Pt> Points = new List<Pt>();
        public bool Closed;
        public double WidthMm;
        /// <summary>0 black … 1 white.</summary>
        public double Grey;
    }

    /// <summary>A filled region: outer rings and holes, in paper mm.</summary>
    public sealed class Fill
    {
        public List<List<Pt>> Rings = new List<List<Pt>>();
        public double Grey;
        public bool EvenOdd = true;
    }

    public sealed class Text
    {
        public string Value = "";
        /// <summary>The anchor in paper mm: the justification point of the text's plane.</summary>
        public double X;
        public double Y;
        /// <summary>The capitals' height in mm, as Rhino's text height.</summary>
        public double Mm;
        public double AngleDeg;
        public Align Align;
        public Vertical Vertical;
        public bool Bold;
        public double Grey;
    }

    public sealed class Page
    {
        public double WidthMm = 420;
        public double HeightMm = 297;
        public List<Stroke> Strokes = new List<Stroke>();
        public List<Fill> Fills = new List<Fill>();
        public List<Text> Texts = new List<Text>();
    }

    public sealed class Result
    {
        public byte[] Bytes;
        public int Pages;
        /// <summary>Characters outside WinAnsi, written as "?".</summary>
        public int Unmapped;
    }

    /// <summary>The PDF of these pages with info as its Info. created null leaves CreationDate out (deterministic bytes).</summary>
    public static Result Write(IList<Page> pages, ProjectInfo.Pdf info, DateTime? created = null)
    {
        pages = pages ?? new Page[0];
        var result = new Result { Pages = pages.Count };
        var objects = new List<byte[]>();
        // 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, 5 Info, then a page and its content per page.
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => (6 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R"));
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii("<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count.ToString(CultureInfo.InvariantCulture) + " >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        objects.Add(Ascii(InfoDictionary(info ?? new ProjectInfo.Pdf(), created)));
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i] ?? new Page();
            var content = Deflate(Content(page, ref result.Unmapped));
            objects.Add(Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + Num(page.WidthMm * PtPerMm) + " " + Num(page.HeightMm * PtPerMm)
                + "] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents " + (7 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R >>"));
            objects.Add(Concat(Ascii("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " /Filter /FlateDecode >>\nstream\n"),
                content, Ascii("\nendstream")));
        }

        using (var output = new MemoryStream())
        {
            void Put(byte[] bytes) => output.Write(bytes, 0, bytes.Length);
            // A binary comment after the header marks the file as binary for transfer tools.
            Put(Ascii("%PDF-1.4\n"));
            Put(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
            var offsets = new List<long>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                Put(Ascii((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n"));
                Put(objects[i]);
                Put(Ascii("\nendobj\n"));
            }
            var xref = output.Position;
            var table = new StringBuilder();
            table.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
            foreach (var offset in offsets)
                table.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            table.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" /Root 1 0 R /Info 5 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
            Put(Ascii(table.ToString()));
            result.Bytes = output.ToArray();
        }
        return result;
    }

    /// <summary>The page's drawing operators: fills, then strokes, then the text on top.</summary>
    public static byte[] Content(Page page, ref int unmapped)
    {
        var ops = new MemoryStream();
        void Op(string text)
        {
            var bytes = Ascii(text);
            ops.Write(bytes, 0, bytes.Length);
        }
        double? grey = null;
        foreach (var fill in page.Fills)
        {
            var rings = fill?.Rings?.Where(r => r != null && r.Count >= 3).ToList();
            if (rings == null || rings.Count == 0) continue;
            if (grey != fill.Grey) Op(Num(Clamp(fill.Grey)) + " g\n");
            grey = fill.Grey;
            foreach (var ring in rings)
            {
                Op(Pt2(ring[0]) + " m\n");
                for (var i = 1; i < ring.Count; i++) Op(Pt2(ring[i]) + " l\n");
                Op("h\n");
            }
            Op(fill.EvenOdd ? "f*\n" : "f\n");
        }

        // Butt caps and miter joins, as the DWG's lines end.
        Op("0 J 0 j\n");
        double? width = null;
        double? strokeGrey = null;
        foreach (var stroke in page.Strokes)
        {
            if (stroke?.Points == null || stroke.Points.Count < 2) continue;
            if (width != stroke.WidthMm) Op(Num(Math.Max(0, stroke.WidthMm) * PtPerMm) + " w\n");
            width = stroke.WidthMm;
            if (strokeGrey != stroke.Grey) Op(Num(Clamp(stroke.Grey)) + " G\n");
            strokeGrey = stroke.Grey;
            Op(Pt2(stroke.Points[0]) + " m\n");
            for (var i = 1; i < stroke.Points.Count; i++) Op(Pt2(stroke.Points[i]) + " l\n");
            Op(stroke.Closed ? "s\n" : "S\n");
        }

        foreach (var text in page.Texts)
        {
            if (text == null || string.IsNullOrEmpty(text.Value) || text.Mm <= 0) continue;
            var lines = text.Value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var size = text.Mm / CapHeight;
            var angle = text.AngleDeg * Math.PI / 180.0;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            // The first line's baseline above the anchor, in the text's own frame.
            var pitch = text.Mm * LinePitch;
            var block = (lines.Length - 1) * pitch;
            var first = text.Vertical == Vertical.Top ? -text.Mm
                : text.Vertical == Vertical.Middle ? -text.Mm / 2 + block / 2
                : block;
            Op("BT\n/" + (text.Bold ? "F2" : "F1") + " " + Num(size * PtPerMm) + " Tf\n" + Num(Clamp(text.Grey)) + " g\n");
            grey = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var bytes = WinAnsi(lines[i], ref unmapped);
                if (bytes.Length == 0) continue;
                var advance = Width(bytes) * size;
                var u = text.Align == Align.Right ? -advance : text.Align == Align.Centre ? -advance / 2 : 0;
                var v = first - i * pitch;
                var x = text.X + u * cos - v * sin;
                var y = text.Y + u * sin + v * cos;
                Op(Num(cos) + " " + Num(sin) + " " + Num(-sin) + " " + Num(cos) + " " + Num(x * PtPerMm) + " " + Num(y * PtPerMm) + " Tm\n(");
                var escaped = Escape(bytes);
                ops.Write(escaped, 0, escaped.Length);
                Op(") Tj\n");
            }
            Op("ET\n");
        }
        return ops.ToArray();
    }

    /// <summary>Text as WinAnsi bytes. A character outside it becomes "?" and counts in unmapped.</summary>
    public static byte[] WinAnsi(string text, ref int unmapped)
    {
        var bytes = new List<byte>();
        foreach (var ch in text ?? "")
        {
            if (ch >= 32 && ch <= 126 || ch >= 160 && ch <= 255)
            {
                bytes.Add((byte)ch);
                continue;
            }
            var code = Array.IndexOf(Cp1252High, ch);
            if (code >= 0 && ch != '\0')
            {
                bytes.Add((byte)(128 + code));
                continue;
            }
            if (ch == '\t')
            {
                bytes.Add((byte)' ');
                continue;
            }
            bytes.Add((byte)'?');
            unmapped++;
        }
        return bytes.ToArray();
    }

    /// <summary>A WinAnsi string's advance in em: Helvetica's widths (Arial's are the same).</summary>
    public static double Width(byte[] winAnsi)
    {
        var sum = 0;
        foreach (var b in winAnsi ?? new byte[0])
            sum += b >= 32 ? HelveticaWidths[b - 32] : 0;
        return sum / 1000.0;
    }

    /// <summary>The Info dictionary: Title, Author, Subject, Keywords, Creator and Producer, and CreationDate when given.</summary>
    static string InfoDictionary(ProjectInfo.Pdf info, DateTime? created)
    {
        var text = new StringBuilder("<<");
        void Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.Append(" /").Append(key).Append(' ').Append(PdfString(value.Trim()));
        }
        Add("Title", info.Title);
        Add("Author", info.Author);
        Add("Subject", info.Subject);
        Add("Keywords", info.Keywords);
        Add("Creator", string.IsNullOrWhiteSpace(info.Creator) ? "Forsk" : info.Creator);
        Add("Producer", "Forsk");
        if (created.HasValue)
            text.Append(" /CreationDate (D:").Append(created.Value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(')');
        return text.Append(" >>").ToString();
    }

    /// <summary>An Info string: ASCII as a literal, anything else UTF-16BE hex with its BOM.</summary>
    public static string PdfString(string value)
    {
        if (value.All(c => c >= 32 && c <= 126))
            return "(" + value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var hex = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(value))
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return hex.Append('>').ToString();
    }

    /// <summary>zlib: a header, the raw deflate, and the Adler-32 of the input.</summary>
    public static byte[] Deflate(byte[] data)
    {
        using (var output = new MemoryStream())
        {
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            var adler = (b << 16) | a;
            output.WriteByte((byte)(adler >> 24));
            output.WriteByte((byte)(adler >> 16));
            output.WriteByte((byte)(adler >> 8));
            output.WriteByte((byte)adler);
            return output.ToArray();
        }
    }

    static byte[] Escape(byte[] text)
    {
        var output = new List<byte>(text.Length + 4);
        foreach (var b in text)
        {
            if (b == '\\' || b == '(' || b == ')') output.Add((byte)'\\');
            output.Add(b);
        }
        return output.ToArray();
    }

    static string Pt2(Pt p) => Num(p.X * PtPerMm) + " " + Num(p.Y * PtPerMm);

    static double Clamp(double grey) => Math.Max(0, Math.Min(1, grey));

    /// <summary>At most three decimals, invariant, no exponent: 1.417, 0, -2.5.</summary>
    public static string Num(double value)
    {
        var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>WinAnsi 128–159 as Unicode; '\0' where the code is unused.</summary>
    static readonly char[] Cp1252High =
    {
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ'
    };

    /// <summary>Helvetica's advance widths (AFM, per 1000 em) for WinAnsi 32–255; 0 for an unused code.</summary>
    static readonly int[] HelveticaWidths =
    {
        // 32–63
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        // 64–95
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        // 96–127
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 0,
        // 128–159
        556, 0, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        0, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 0, 500, 667,
        // 160–191
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        // 192–223
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        // 224–255
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500
    };
}
