using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The print sheet. English landscape is the only template; the paper is A4,
/// A3, A2 or A1 (A3 when none is stored), and the set fits its one scale to
/// that paper. The title block is 280 mm wide where the paper allows it and
/// narrower on A4, which keeps the north arrow and scale bar beside it. Pure,
/// so it tests headless.
/// </summary>
public static class PrintTemplate
{
    public const string Key = "print_template";
    public const string EnglishA3 = "a3-en";
    /// <summary>The paper the set prints on, in the document strings beside the asked scale.</summary>
    public const string PaperKey = "print_paper";
    public const string DefaultPaper = "A3";
    public const double WidthMm = 420.0;
    public const double HeightMm = 297.0;
    /// <summary>The title block's width where the paper has room.</summary>
    public const double TitleBlockMm = 280.0;
    /// <summary>The footer's left side the title block leaves for the north arrow and the scale bar with its end label.</summary>
    const double FooterLeftMm = 95.0;

    public sealed class Paper
    {
        public string Name;
        public double WidthMm;
        public double HeightMm;
    }

    /// <summary>Landscape ISO sizes, smallest first.</summary>
    public static readonly IReadOnlyList<Paper> Papers = new[]
    {
        new Paper { Name = "A4", WidthMm = 297, HeightMm = 210 },
        new Paper { Name = "A3", WidthMm = 420, HeightMm = 297 },
        new Paper { Name = "A2", WidthMm = 594, HeightMm = 420 },
        new Paper { Name = "A1", WidthMm = 841, HeightMm = 594 }
    };

    public static string Names => string.Join(", ", Papers.Select(p => p.Name));

    public static string UnknownPaper => "Unknown paper. Use " + string.Join(", ", Papers.Take(Papers.Count - 1).Select(p => p.Name)) + " or " + Papers.Last().Name + ".";

    public static string Resolve(string stored) => EnglishA3;

    /// <summary>The paper by name (any case, spaces ignored), or null.</summary>
    public static Paper Find(string name)
    {
        var key = (name ?? "").Replace(" ", "").Trim();
        return Papers.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The stored paper, else A3.</summary>
    public static Paper Stored(string stored) => Find(stored) ?? Find(DefaultPaper);

    /// <summary>The title block's width on this paper: 280 mm, less on A4 so the arrow and bar keep their room.</summary>
    public static double TitleBlockWidthMm(Paper paper, double marginMm) =>
        Math.Min(TitleBlockMm, paper.WidthMm - 2 * marginMm - FooterLeftMm);
}
