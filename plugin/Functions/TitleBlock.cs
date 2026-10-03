using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P3: the sheet's title block. One flat row of cells along the footer
/// band, each a Norwegian caption over its value: Tegning, Tegningsnr.,
/// Målestokk, Format, Dato, Rev. (only when set), Prosjekt, Byggherre,
/// Adresse. A cell's key is English and stays the forsk:cell stamp the
/// smokes read; only the printed caption is Norwegian. An empty value drops
/// its cell, never a dash. Also which footer parts a sheet gets. Pure, no
/// Rhino document, so it tests headless. Sizes are paper millimetres.
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
        public string Project;
        public string Client;
        public string Address;
    }

    public sealed class Cell
    {
        public string Key;
        public string Caption;
        public string Value;
        /// <summary>The value's text height.</summary>
        public double Mm;
    }

    /// <summary>The cells in order, those without a value left out.</summary>
    public static List<Cell> Cells(Fields fields)
    {
        var cells = new List<Cell>();
        if (fields == null) return cells;
        void Add(string key, string caption, string value, double mm)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text) || text == "—" || text == "-") return;
            cells.Add(new Cell { Key = key, Caption = caption, Value = text, Mm = mm });
        }
        Add("drawing", "Tegning", fields.Drawing, HeadMm);
        Add("number", "Tegningsnr.", fields.Number, HeadMm);
        Add("scale", "Målestokk", fields.Scale, ValueMm);
        Add("sheet", "Format", fields.Format, ValueMm);
        Add("date", "Dato", fields.Date, ValueMm);
        Add("revision", "Rev.", fields.Revision, ValueMm);
        Add("project", "Prosjekt", fields.Project, ValueMm);
        Add("client", "Byggherre", fields.Client, ValueMm);
        Add("address", "Adresse", fields.Address, ValueMm);
        return cells;
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

    /// <summary>The north arrow is on the plans.</summary>
    public static bool NorthArrow(string sheetId)
    {
        return string.Equals((sheetId ?? "").Trim(), SheetSet.PlanId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scale bar is on every drawing sheet whose detail locked its scale:
    /// the plan, the facades and the sections. The lists have no scale.
    /// </summary>
    public static bool ScaleBar(string sheetId, int pageScale)
    {
        var id = (sheetId ?? "").Trim().ToLowerInvariant();
        var drawing = id == SheetSet.PlanId || SheetSet.Facades.Contains(id) || Sections.TryLetter(id, out _);
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
