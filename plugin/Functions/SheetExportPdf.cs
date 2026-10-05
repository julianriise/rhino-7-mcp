using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 N3: the Print PDF from the flat sheets the DWG is written from, so the
/// two cannot drift. Curves become polylines at 0.05 paper mm, solid hatches
/// fills, patterned hatches their exploded lines at the piece's weight, and
/// texts real Helvetica text at their plane's angle and justification.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>A curve's chord tolerance on paper, mm.</summary>
    private const double PdfCurveTolMm = 0.05;

    private sealed class PdfSheets
    {
        public List<SheetPdf.Page> Pages = new List<SheetPdf.Page>();
        public JArray Names = new JArray();
        /// <summary>Patterned hatches drawn as their boundary because they would not explode.</summary>
        public int HatchFallback;
        /// <summary>Pieces whose box lies partly off the page.</summary>
        public int OffPage;
        /// <summary>Texts in a font other than Arial: they print in Helvetica.</summary>
        public int OtherFonts;
    }

    /// <summary>The set's pages as vector PDF pages, and the first and last sheet numbers for the Title.</summary>
    private static PdfSheets VectorPages(RhinoDoc doc, IList<RhinoPageView> pages, out string firstSheet, out string lastSheet)
    {
        var sheets = new PdfSheets();
        firstSheet = null;
        lastSheet = null;
        var logo = OfficeLogo.Decode(doc.Strings.GetValue(ProjectInfo.Section, OfficeLogo.Key));
        foreach (var page in pages)
        {
            var misc = new HashSet<string>(StringComparer.Ordinal);
            var pieces = FlattenPage(doc, page, misc);
            var pdfPage = PdfPage(doc, page, pieces, sheets);
            if (logo != null) pdfPage.Images.AddRange(LogoImages(doc, page, logo));
            sheets.Pages.Add(pdfPage);
            sheets.Names.Add(page.PageName ?? "");
            var number = SheetNumberOf(doc, page);
            if (!string.IsNullOrEmpty(number))
            {
                firstSheet = firstSheet ?? number;
                lastSheet = number;
            }
        }
        return sheets;
    }

    /// <summary>The logo on this page, at the paper box its title block stamped on it.</summary>
    private static List<SheetPdf.Image> LogoImages(RhinoDoc doc, RhinoPageView page, OfficeLogo.Picture logo)
    {
        var images = new List<SheetPdf.Image>();
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false,
            ViewportFilter = page.MainViewport
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj?.Attributes == null || obj.Attributes.Space != ActiveSpace.PageSpace) continue;
            if (obj.Attributes.GetUserString("forsk:role") != OfficeLogo.Role) continue;
            if (OfficeLogo.TryParseBox(obj.Attributes.GetUserString(OfficeLogo.BoxKey), out var x0, out var y0, out var x1, out var y1))
                images.Add(new SheetPdf.Image { Picture = logo, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
        }
        return images;
    }

    /// <summary>One flat page as a SheetPdf page in paper mm.</summary>
    private static SheetPdf.Page PdfPage(RhinoDoc doc, RhinoPageView page, List<FlatPiece> pieces, PdfSheets sheets)
    {
        var perMm = MmToPage(doc, 1.0);
        var result = new SheetPdf.Page
        {
            WidthMm = perMm > 0 && page.PageWidth > 0 ? page.PageWidth / perMm : SheetWidthMm,
            HeightMm = perMm > 0 && page.PageHeight > 0 ? page.PageHeight / perMm : SheetHeightMm
        };
        foreach (var piece in pieces)
        {
            var grey = Grey(piece.Color);
            var width = piece.Weight ?? SheetFlat.Def(piece.Layer).WeightMm;
            if (width <= 0) width = SheetFlat.Def(piece.Layer).WeightMm;
            if (piece.Geometry == null)
            {
                if (string.IsNullOrEmpty(piece.Text)) continue;
                if (!string.IsNullOrEmpty(piece.Font) && !piece.Font.StartsWith("Arial", StringComparison.OrdinalIgnoreCase)) sheets.OtherFonts++;
                var justify = piece.Justify ?? "BottomLeft";
                result.Texts.Add(new SheetPdf.Text
                {
                    Value = piece.Text,
                    X = piece.TextPlane.Origin.X,
                    Y = piece.TextPlane.Origin.Y,
                    Mm = piece.TextMm,
                    AngleDeg = Math.Atan2(piece.TextPlane.XAxis.Y, piece.TextPlane.XAxis.X) * 180.0 / Math.PI,
                    Align = justify.EndsWith("Center", StringComparison.Ordinal) ? SheetPdf.Align.Centre
                        : justify.EndsWith("Right", StringComparison.Ordinal) ? SheetPdf.Align.Right : SheetPdf.Align.Left,
                    Vertical = justify.StartsWith("Top", StringComparison.Ordinal) ? SheetPdf.Vertical.Top
                        : justify.StartsWith("Middle", StringComparison.Ordinal) ? SheetPdf.Vertical.Middle : SheetPdf.Vertical.Bottom,
                    Grey = grey
                });
                continue;
            }
            var box = piece.Geometry.GetBoundingBox(true);
            if (box.IsValid && (box.Min.X < -0.5 || box.Min.Y < -0.5 || box.Max.X > result.WidthMm + 0.5 || box.Max.Y > result.HeightMm + 0.5))
                sheets.OffPage++;
            if (piece.Geometry is Hatch hatch)
            {
                if (string.Equals(piece.HatchPattern, "Solid", StringComparison.OrdinalIgnoreCase))
                {
                    var fill = new SheetPdf.Fill { Grey = grey };
                    foreach (var curve in (hatch.Get3dCurves(true) ?? new Curve[0]).Concat(hatch.Get3dCurves(false) ?? new Curve[0]))
                    {
                        var ring = PolylinePoints(curve);
                        if (ring.Count >= 3) fill.Rings.Add(ring);
                    }
                    if (fill.Rings.Count > 0) result.Fills.Add(fill);
                    continue;
                }
                var lines = new List<SheetPdf.Stroke>();
                try
                {
                    foreach (var part in hatch.Explode() ?? new GeometryBase[0])
                        if (part is Curve segment) AddStroke(lines, segment, width, grey);
                }
                catch (Exception)
                {
                    lines.Clear();
                }
                if (lines.Count == 0)
                {
                    // The hatch's boundary stands in, at the piece's weight.
                    sheets.HatchFallback++;
                    foreach (var curve in (hatch.Get3dCurves(true) ?? new Curve[0]).Concat(hatch.Get3dCurves(false) ?? new Curve[0]))
                        AddStroke(lines, curve, width, grey);
                }
                result.Strokes.AddRange(lines);
                continue;
            }
            if (piece.Geometry is Curve line)
                AddStroke(result.Strokes, line, width, grey);
        }
        return result;
    }

    private static void AddStroke(List<SheetPdf.Stroke> strokes, Curve curve, double widthMm, double grey)
    {
        var points = PolylinePoints(curve);
        if (points.Count < 2) return;
        var closed = curve.IsClosed && points.Count > 2;
        if (closed && points[0].X == points[points.Count - 1].X && points[0].Y == points[points.Count - 1].Y)
            points.RemoveAt(points.Count - 1);
        strokes.Add(new SheetPdf.Stroke { Points = points, Closed = closed, WidthMm = widthMm, Grey = grey });
    }

    /// <summary>A curve as its polyline at 0.05 paper mm: a polyline as it is, anything else through ToPolyline.</summary>
    private static List<Pt> PolylinePoints(Curve curve)
    {
        var points = new List<Pt>();
        if (curve == null) return points;
        Polyline polyline;
        if (!curve.TryGetPolyline(out polyline))
        {
            var approx = curve.ToPolyline(PdfCurveTolMm, RhinoMath.ToRadians(2), 0, 0);
            if (approx == null || !approx.TryGetPolyline(out polyline)) return points;
        }
        foreach (var p in polyline)
            points.Add(new Pt(p.X, p.Y));
        return points;
    }

    /// <summary>
    /// The object's print colour as Print resolves it: its own, its display
    /// colour, or its layer's. An unset colour is black.
    /// </summary>
    private static Color PrintColour(RhinoDoc doc, ObjectAttributes attr)
    {
        Color colour;
        switch (attr.PlotColorSource)
        {
            case ObjectPlotColorSource.PlotColorFromObject:
                colour = attr.PlotColor;
                break;
            case ObjectPlotColorSource.PlotColorFromDisplay:
                colour = attr.DrawColor(doc);
                break;
            default:
                var layer = attr.LayerIndex >= 0 && attr.LayerIndex < doc.Layers.Count ? doc.Layers[attr.LayerIndex] : null;
                colour = layer?.PlotColor ?? Color.Black;
                break;
        }
        return colour.IsEmpty || colour.A == 0 ? Color.Black : colour;
    }

    /// <summary>0 black … 1 white: the colour's luminance.</summary>
    private static double Grey(Color colour)
    {
        return (0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B) / 255.0;
    }

    /// <summary>The page's sheet number off its footer (forsk:sheet_no), or null.</summary>
    private static string SheetNumberOf(RhinoDoc doc, RhinoPageView page)
    {
        var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ViewportFilter = page.MainViewport };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            var number = obj?.Attributes?.GetUserString("forsk:sheet_no");
            if (!string.IsNullOrWhiteSpace(number)) return number.Trim();
        }
        return null;
    }

    /// <summary>
    /// The set as one vector PDF at full: its pages, its Info from the project
    /// info. Throws when anything fails, so export_pdf can fall back.
    /// </summary>
    private static PdfSheets WriteVectorPdf(RhinoDoc doc, IList<RhinoPageView> pages, string full, out int unmapped)
    {
        var sheets = VectorPages(doc, pages, out var first, out var last);
        var info = ProjectInfo.PdfInfo(ReadProjectInfo(doc), first, last);
        var written = SheetPdf.Write(sheets.Pages, info, DateTime.Now);
        unmapped = written.Unmapped;
        File.WriteAllBytes(full, written.Bytes);
        return sheets;
    }
}
