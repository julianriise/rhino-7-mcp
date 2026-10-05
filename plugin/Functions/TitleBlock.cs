using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Sheet language. English is the default for everything printed and for the
/// print, Choose sheets and takeoff receipts. Pass norwegian for the bokmål
/// strings; they stay. The chat turn does not switch a sheet: a Norwegian
/// question is still understood, and the sheet stays English unless a caller
/// asks for bokmål.
/// </summary>
public static class SheetLang
{
    public static string Pick(bool norwegian, string english, string bokmal) =>
        norwegian ? bokmal : english;
}

/// <summary>
/// v3 P3: the sheet's title block. One flat row of cells along the footer
/// band, each a caption over its value. English is the default: Drawing,
/// Sheet no., Scale, Format, Date, Rev., Project no., Project, Client,
/// Address, Architect. Pass norwegian for Tegning, Tegningsnr., Målestokk,
/// Format, Dato, Rev., Prosjektnr., Prosjekt, Byggherre, Adresse, Arkitekt.
/// The project's captions are ProjectInfo's. A cell's key is English either way and
/// stays the forsk:cell stamp the smokes read. An empty value drops its cell,
/// never a dash. Also which footer parts a sheet gets. Pure, no Rhino
/// document, so it tests headless. Sizes are paper millimetres.
/// </summary>
public static class TitleBlock
{
    public const double CaptionMm = 1.8;
    public const double ValueMm = 2.5;
    /// <summary>The drawing's title and its number print larger: they name the sheet.</summary>
    public const double HeadMm = 3.5;
    /// <summary>Text inset from the cell's left line, and the same clear before the next.</summary>
    public const double PadMm = 2.0;
    /// <summary>Glyph advance per text height when Rhino cannot measure: generous, so a cell holds its text.</summary>
    const double CharShare = 0.8;

    public sealed class Fields
    {
        public string Drawing;
        public string Number;
        public string Scale;
        public string Format;
        public string Date;
        public string Revision;
        public string ProjectNo;
        public string Project;
        public string Client;
        public string Address;
        public string Architect;
    }

    public sealed class Cell
    {
        public string Key;
        public string Caption;
        public string Value;
        /// <summary>The value's text height.</summary>
        public double Mm;
    }

    /// <summary>The cells in order, those without a value left out. English captions unless norwegian.</summary>
    public static List<Cell> Cells(Fields fields, bool norwegian = false)
    {
        var cells = new List<Cell>();
        if (fields == null) return cells;
        void Add(string key, string caption, string value, double mm)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text) || text == "—" || text == "-") return;
            cells.Add(new Cell { Key = key, Caption = caption, Value = text, Mm = mm });
        }
        Add("drawing", SheetLang.Pick(norwegian, "Drawing", "Tegning"), fields.Drawing, HeadMm);
        Add("number", SheetLang.Pick(norwegian, "Sheet no.", "Tegningsnr."), fields.Number, HeadMm);
        Add("scale", SheetLang.Pick(norwegian, "Scale", "Målestokk"), fields.Scale, ValueMm);
        Add("sheet", "Format", fields.Format, ValueMm);
        void Info(string key, string value) => Add(key, ProjectInfo.Caption(key, norwegian), value, ValueMm);
        Info(ProjectInfo.Date, fields.Date);
        Info(ProjectInfo.Revision, fields.Revision);
        Info(ProjectInfo.ProjectNo, fields.ProjectNo);
        Info(ProjectInfo.Project, fields.Project);
        Info(ProjectInfo.Client, fields.Client);
        Info(ProjectInfo.Address, fields.Address);
        Info(ProjectInfo.Architect, fields.Architect);
        return cells;
    }

    /// <summary>The second row's value height: smaller, as it holds the project number and the architect.</summary>
    public const double SecondValueMm = 2.0;
    /// <summary>The second row's height at the bottom of the band, when there is one.</summary>
    public const double SecondRowMm = 7.0;
    /// <summary>The cells that move to the second row when the first does not hold every cell.</summary>
    public static readonly IReadOnlyList<string> SecondRowKeys = new[] { ProjectInfo.ProjectNo, ProjectInfo.Architect };

    /// <summary>
    /// The band's rows. One row when every cell's caption and value fit in
    /// total. Otherwise Project no. and Architect move to a second, smaller
    /// row (value SecondValueMm) and the rest keep the first; a cell is
    /// never squeezed below its text when a second row can take it.
    /// </summary>
    public static List<List<Cell>> Rows(IList<Cell> cells, double total, Func<string, double, double> measure)
    {
        var rows = new List<List<Cell>>();
        if (cells == null || cells.Count == 0) return rows;
        var wants = cells.Sum(c => Math.Max(CaptionWidth(c, measure), TextWidth(c.Value, c.Mm, measure) + 2 * PadMm));
        var second = cells.Where(c => SecondRowKeys.Contains(c.Key)).ToList();
        if (wants <= total || second.Count == 0)
        {
            rows.Add(cells.ToList());
            return rows;
        }
        rows.Add(cells.Where(c => !SecondRowKeys.Contains(c.Key)).ToList());
        rows.Add(second.Select(c => new Cell { Key = c.Key, Caption = c.Caption, Value = c.Value, Mm = SecondValueMm }).ToList());
        return rows;
    }

    /// <summary>The narrowest a cell may be: its caption and the padding.</summary>
    public static double CaptionWidth(Cell cell, Func<string, double, double> measure)
    {
        return TextWidth(cell.Caption, CaptionMm, measure) + 2 * PadMm;
    }

    /// <summary>
    /// Cell widths that sum to total. Each cell wants room for its caption
    /// and its value. When there is room to spare, the number wants as much
    /// as any cell but the title, and the title as much as the number, so
    /// the number has the second widest share; the wants are then spread to
    /// the total. Prominence never squeezes text: when that does not fit,
    /// the plain wants are spread, and when even those do not fit every cell
    /// keeps its caption's width and the rest is shared by how much more
    /// each wants. measure gives a text's printed width in mm at a height
    /// (Rhino's layout); without it, or when it gives nothing, the width
    /// comes from the character count.
    /// </summary>
    public static double[] Widths(IList<Cell> cells, double total, Func<string, double, double> measure)
    {
        if (cells == null || cells.Count == 0 || total <= 0) return new double[0];
        var min = cells.Select(c => CaptionWidth(c, measure)).ToArray();
        var want = cells.Select((c, i) => Math.Max(min[i], TextWidth(c.Value, c.Mm, measure) + 2 * PadMm)).ToArray();
        var lead = (double[])want.Clone();
        var drawing = IndexOf(cells, "drawing");
        var number = IndexOf(cells, "number");
        if (number >= 0)
        {
            for (var i = 0; i < cells.Count; i++)
                if (i != number && i != drawing) lead[number] = Math.Max(lead[number], want[i]);
            if (drawing >= 0) lead[drawing] = Math.Max(lead[drawing], lead[number]);
        }
        if (lead.Sum() <= total) return Spread(lead, total);
        if (want.Sum() <= total) return Spread(want, total);
        var sumMin = min.Sum();
        if (sumMin >= total) return Spread(min, total);
        var extra = want.Sum() - sumMin;
        return min.Select((m, i) => m + (total - sumMin) * (want[i] - m) / extra).ToArray();
    }

    static double[] Spread(double[] widths, double total)
    {
        var sum = widths.Sum();
        return widths.Select(w => total * w / sum).ToArray();
    }

    /// <summary>The logo cell's widest, so a long wordmark leaves the fields their room.</summary>
    public const double LogoMaxMm = 60.0;
    /// <summary>The logo's least width: a tall mark still gets a readable cell.</summary>
    public const double LogoMinMm = 8.0;

    /// <summary>The logo's cell at the right end of the band, and the picture inside it, in paper mm.</summary>
    public sealed class LogoBox
    {
        /// <summary>The cell's width, padding included: the fields share the rest of the block.</summary>
        public double CellMm;
        public double X0, Y0, X1, Y1;
    }

    /// <summary>
    /// The logo cell for a band from x1 - cell to x1 and y0 to y1: the picture
    /// as tall as the band less the padding, as wide as its aspect allows up to
    /// LogoMaxMm, and centred in the cell when the width caps it. Null for no logo.
    /// </summary>
    public static LogoBox Logo(double aspect, double x1, double y0, double y1)
    {
        if (double.IsNaN(aspect) || aspect <= 0 || y1 <= y0) return null;
        var h = Math.Max(1, y1 - y0 - 2 * PadMm);
        var w = h * aspect;
        if (w > LogoMaxMm)
        {
            w = LogoMaxMm;
            h = w / aspect;
        }
        var cell = Math.Max(LogoMinMm, w) + 2 * PadMm;
        var cx = x1 - cell / 2;
        var cy = (y0 + y1) / 2;
        return new LogoBox { CellMm = cell, X0 = cx - w / 2, Y0 = cy - h / 2, X1 = cx + w / 2, Y1 = cy + h / 2 };
    }

    /// <summary>The north arrow is on the plans.</summary>
    public static bool NorthArrow(string sheetId)
    {
        return string.Equals((sheetId ?? "").Trim(), SheetSet.PlanId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scale bar is on every drawing sheet whose detail locked its scale:
    /// the plan, the facades, the sections and the detail sheets. The lists have no scale.
    /// </summary>
    public static bool ScaleBar(string sheetId, int pageScale)
    {
        var id = (sheetId ?? "").Trim().ToLowerInvariant();
        var drawing = id == SheetSet.PlanId || SheetSet.Facades.Contains(id) || Sections.TryLetter(id, out _)
            || Details.TrySheetId(id, out _, out _);
        return drawing && pageScale > 0;
    }

    static double TextWidth(string text, double mm, Func<string, double, double> measure)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var measured = measure == null ? 0 : measure(text, mm);
        return measured > 0 ? measured : text.Length * CharShare * mm;
    }

    static int IndexOf(IList<Cell> cells, string key)
    {
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Key == key) return i;
        return -1;
    }
}
