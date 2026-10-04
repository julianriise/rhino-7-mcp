using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The detail sheets: every detail's drawings grouped by the detail's scale
/// and shelf-packed into the 400 × 254 detail area, a detail's drawings
/// together in one row (plan, elevation, section), the next sheet of that
/// scale when one is full. Each box is the drawing, its 15 mm band on every
/// side and its 12 mm title band below. A row's boxes share a vertical
/// centre, so a short plan sits with the tallest view. Deterministic from
/// the record order. Once a sheet is drawn, what it drew is centred in the
/// area (Centre), one move, so the export's lines move with it.
/// Pure, no RhinoCommon.
/// </summary>
public static class DetailSheet
{
    public const double GapMm = 10;

    /// <summary>One detail, its scale and its drawings.</summary>
    public sealed class Item
    {
        public string Detail;
        public string Name;
        public int Scale;
        public List<Details.Drawing> Drawings = new List<Details.Drawing>();
    }

    public sealed class Sheet
    {
        public string Id;
        public int Scale;
        public int N;
        public List<Details.Placed> Drawings = new List<Details.Placed>();
    }

    public static double BoxWidth(Details.Drawing d, int scale) => d.Width / scale + 2 * Details.BandMm;
    public static double BoxHeight(Details.Drawing d, int scale) => d.Height / scale + 2 * Details.BandMm + Details.TitleBandMm;

    /// <summary>The details as packer items, each at the finest scale all its drawings fit.</summary>
    public static List<Item> Items(IList<Details.Facts> facts)
    {
        var items = new List<Item>();
        var names = Details.Names(facts ?? new List<Details.Facts>());
        for (var i = 0; i < (facts?.Count ?? 0); i++)
        {
            var drawings = Details.Drawings(facts[i], names[i]);
            if (drawings.Count == 0) continue;
            items.Add(new Item { Detail = facts[i].Record.Id, Name = names[i], Scale = Details.ScaleOf(drawings), Drawings = drawings });
        }
        return items;
    }

    /// <summary>The sheets, scale ascending then n; drawings numbered 1..n on each in placement order.</summary>
    public static List<Sheet> Plan(IEnumerable<Item> items)
    {
        var sheets = new List<Sheet>();
        var list = (items ?? Enumerable.Empty<Item>()).ToList();
        foreach (var scale in list.Select(i => i.Scale).Distinct().OrderBy(s => s))
        {
            Sheet sheet = null;
            double used = 0, x = 0, shelf = 0;
            var shelfFrom = 0;
            // A shelf is top-aligned as it fills. When it closes, each box drops
            // so its middle matches the tallest. The group centre later moves
            // the whole sheet by one dx,dy, which the DWG reads back.
            void CentreShelf()
            {
                if (sheet == null || shelf <= 0) return;
                for (var i = shelfFrom; i < sheet.Drawings.Count; i++)
                {
                    var placed = sheet.Drawings[i];
                    placed.Y += (BoxHeight(placed.Drawing, sheet.Scale) - shelf) / 2.0;
                }
            }
            void NewSheet()
            {
                CentreShelf();
                sheet = new Sheet { Scale = scale, N = sheets.Count(s => s.Scale == scale) + 1 };
                sheet.Id = Details.SheetId(scale, sheet.N);
                sheets.Add(sheet);
                used = x = shelf = 0;
                shelfFrom = 0;
            }
            void NewShelf()
            {
                CentreShelf();
                used += shelf + GapMm;
                x = shelf = 0;
                shelfFrom = sheet.Drawings.Count;
            }
            foreach (var item in list.Where(i => i.Scale == scale))
            {
                if (sheet == null) NewSheet();
                var block = item.Drawings.Sum(d => BoxWidth(d, scale)) + GapMm * (item.Drawings.Count - 1);
                if (x > 0 && x + block > Details.AreaWidthMm + 1e-6) NewShelf();
                foreach (var d in item.Drawings)
                {
                    var w = BoxWidth(d, scale);
                    var h = BoxHeight(d, scale);
                    if (x > 0 && x + w > Details.AreaWidthMm + 1e-6) NewShelf();
                    if (sheet.Drawings.Count > 0 && used + Math.Max(shelf, h) > Details.AreaHeightMm + 1e-6) NewSheet();
                    var bottom = Details.AreaHeightMm - used - h;
                    sheet.Drawings.Add(new Details.Placed
                    {
                        Drawing = d,
                        Number = sheet.Drawings.Count + 1,
                        X = x + Details.BandMm,
                        Y = bottom + Details.TitleBandMm + Details.BandMm
                    });
                    shelf = Math.Max(shelf, h);
                    x += w + GapMm;
                }
            }
            CentreShelf();
        }
        return sheets;
    }

    /// <summary>
    /// The move (paper mm) that centres a sheet's drawn content, its box in
    /// detail-area paper mm, in the detail area: the page's usable area,
    /// which it frames inside the margins and above the title block. One
    /// move for all of it, so the views keep their spacing.
    /// </summary>
    public static Pt Centre(RoomDetect.Box content) =>
        new Pt((Details.AreaWidthMm - content.MinX - content.MaxX) / 2.0, (Details.AreaHeightMm - content.MinY - content.MaxY) / 2.0);

    /// <summary>A detail sheet's number in the set: A-50-001 onwards in id order, scale ascending, then n.</summary>
    public static string Number(string id, IEnumerable<string> ids)
    {
        if (!Details.TrySheetId(id, out _, out var n)) return "";
        var order = Order(ids ?? new[] { id });
        var at = order.FindIndex(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
        return "A-50-" + (at >= 0 ? at + 1 : n).ToString("000", CultureInfo.InvariantCulture);
    }

    /// <summary>Detail sheet ids, scale ascending then n.</summary>
    public static List<string> Order(IEnumerable<string> ids) =>
        (ids ?? Enumerable.Empty<string>())
            .Select(i => (Id: (i ?? "").Trim().ToLowerInvariant(), Ok: Details.TrySheetId(i, out var s, out var n), S: s, N: n))
            .Where(t => t.Ok)
            .GroupBy(t => t.Id).Select(g => g.First())
            .OrderBy(t => t.S).ThenBy(t => t.N)
            .Select(t => t.Id)
            .ToList();

    /// <summary>Where a detail's drawing sits: its sheet and number. False when it is on none.</summary>
    public static bool Find(IEnumerable<Sheet> sheets, string detail, string view, out Sheet sheet, out Details.Placed placed)
    {
        sheet = null;
        placed = null;
        foreach (var s in sheets ?? Enumerable.Empty<Sheet>())
        {
            var p = s.Drawings.FirstOrDefault(d => d.Drawing.View == view
                && string.Equals(d.Drawing.Facts?.Record?.Id, detail, StringComparison.OrdinalIgnoreCase));
            if (p == null) continue;
            sheet = s;
            placed = p;
            return true;
        }
        return false;
    }

    /// <summary>A drawing's title under it, in detail-area paper mm: the number's circle, the title, the scale and the rule.</summary>
    public sealed class Title
    {
        public Pt Circle;
        public double Radius;
        public Pt Name;
        public Pt Scale;
        public Pt RuleFrom;
        public Pt RuleTo;
    }

    public const double TitleCircleMm = 9;
    public const double TitleTextMm = 2.5;
    public const double TitleScaleMm = 1.8;
    public const double TitleRuleMm = 0.35;

    /// <summary>
    /// The view title in a drawing's title band: a 9 mm circle at the box's
    /// left, the title (2.5 mm) on the rule, the scale (1.8 mm) under it.
    /// Text points are left baselines; the rule runs the box's drawing width.
    /// </summary>
    public static Title TitleOf(Details.Placed placed, int scale)
    {
        var left = placed.X - Details.BandMm;
        var band = placed.Y - Details.BandMm;
        var r = TitleCircleMm / 2.0;
        var mid = band - Details.TitleBandMm / 2.0;
        var textX = left + TitleCircleMm + 2.0;
        return new Title
        {
            Circle = new Pt(left + r, mid),
            Radius = r,
            Name = new Pt(textX, mid + 0.6),
            Scale = new Pt(textX, mid - 0.6 - TitleScaleMm),
            RuleFrom = new Pt(textX, mid),
            RuleTo = new Pt(Math.Max(textX + 20, placed.X + placed.Drawing.Width / scale), mid)
        };
    }
}
