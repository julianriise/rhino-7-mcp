using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Paper layouts of a greyscale HiddenLineDrawing. Rhino 7 has no
/// ClippingDrawings. Before each page, layout_pack bakes black curves on
/// S-DRAW (Plan, North, East, South, West). The plan drawing includes the
/// horizontal cut 1200 mm above the floor. Details show that drawing layer
/// only, in a Top view (the curves are flat in XY), Wireframe, so the Mac
/// preview is black lines on white. The model
/// viewport keeps the clay. PDF is Rhino.FileIO.FilePdf. Vector output uses
/// ViewCaptureSettings with RasterMode false. On Rhino 7 Mac that capture
/// wrote a white sheet, so Mac export activates each layout, redraws, waits,
/// then draws GetPreviewImage into the PDF. A blank frame is a paint race:
/// that page is activated and read again until it paints or its read budget
/// (PreviewFrame.ReadAgain) runs out. Pages that
/// have ink are still written. Each export appends a line to
/// /tmp/forsk-print.log, and one capture line per read (PreviewFrame); a
/// blank frame is saved as a PNG beside it. AddPageView width and height are millimetres
/// (A3 landscape 420 x 297). Detail corners and the title block are converted
/// into the document page units.
/// </summary>
public partial class RhinoMCPFunctions
{
    private struct LayoutViewSpec
    {
        public string View;
        public string PageName;
        public Vector3d Look;
        public Vector3d Up;
    }

    private const string LayoutMetaSection = "forsk";
    private const string LayoutPagePrefix = "Forsk — ";
    private const string NothingToLayOutMessage = "Nothing to lay out. Bake walls first.";
    private const string UnknownLayoutViewMessage =
        "Unknown view. Use front, plan, north, east, south, west, schedules, takeoff, or a stored section (section_a).";
    private const string UnknownPaperMessage = "Unknown paper. Use A3.";
    private const string ExportNeedsPathMessage = "export_pdf requires a file path.";
    private const string ExportNeedsPdfMessage = "export_pdf path must be an absolute .pdf file.";
    private const string NoLayoutsMessage = "No layouts to print. Call layout_pack first.";
    private const string PrintScaleEntry = "print_scale";
    private const string NoSheetOnMessage = "Every sheet in the set is off. Turn one on with Choose sheets.";
    private const string PdfWriteFailedMessage = "PDF write failed.";
    private const string LayoutDetailFailedMessage = "Layout detail failed.";
    private const string EmptyDetailMessage = "Layout detail is empty. The sheet does not show the drawing.";
    private const string EmptyPdfMessage = "PDF detail is empty. The sheet does not show the drawing.";
    private const string CaptureFailedPrefix = "capture failed after activate/Wait";
    private const string PlanCutRole = "plan_cut";
    private const string PrintColorSourceKey = "forsk:print_cs";
    private const string PrintRgbKey = "forsk:print_rgb";
    private static bool _drawIncludeExisting = true;
    private static PlanStats _lastPlanStats;
    private static bool _lastPlanStatsSet;

    private const double A3WidthMm = 420.0;
    private const double A3HeightMm = 297.0;
    private const double LayoutMarginMm = 10.0;
    // Space under the detail: the footer band plus a 5 mm gap.
    private const double FooterReserveMm = 23.0;
    // Footer band along the bottom margin: north arrow, scale bar, title block.
    private const double FooterBandMm = 18.0;
    private const double TitleBlockWidthMm = 280.0;
    private const double NorthArrowMm = 12.0;
    private const double ScaleBarLeftMm = 26.0;
    private const double ScaleBarHeightMm = 2.0;
    private const double FooterTextMm = 2.5;
    private const double PdfDpi = 150.0;
    private const string PrintLogPath = "/tmp/forsk-print.log";

    private static readonly string[] LayoutShowLayerNames = { "A-WALL", "A-FLOR", "A-ROOF" };

    private static bool TryGetLayoutView(string view, out LayoutViewSpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(view)) return false;
        switch (view.Trim().ToLowerInvariant())
        {
            case "plan":
                spec = LayoutSpec("plan", "Forsk — Plan", -Vector3d.ZAxis, Vector3d.YAxis);
                return true;
            case "north":
                spec = LayoutSpec("north", "Forsk — North", -Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "east":
                spec = LayoutSpec("east", "Forsk — East", -Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            case "south":
                spec = LayoutSpec("south", "Forsk — South", Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "west":
                spec = LayoutSpec("west", "Forsk — West", Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            case SheetSet.FrontId:
            case SheetSet.TakeoffId:
                // Tables only: no look, no detail.
                var key = view.Trim().ToLowerInvariant();
                spec = LayoutSpec(key, TablePageName(key), Vector3d.Zero, Vector3d.ZAxis);
                return true;
            default:
                if (Details.TrySheetId(view, out var detailScale, out var detailSheet))
                {
                    spec = LayoutSpec(Details.SheetId(detailScale, detailSheet), Details.PageName(detailScale, detailSheet),
                        -Vector3d.ZAxis, Vector3d.YAxis);
                    return true;
                }
                // A section's page. Its look lives on the stored section (TryGetSectionView).
                if (!Sections.TryLetter(view, out var letter)) return false;
                spec = LayoutSpec(Sections.View(letter), Sections.PageName(letter), Vector3d.Zero, Vector3d.ZAxis);
                return true;
        }
    }
