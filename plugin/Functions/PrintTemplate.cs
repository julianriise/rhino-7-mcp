namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The print sheet when none is stored. English A3 landscape is the only
/// template, so a missing or unknown id still resolves to it. Page size stays
/// 420 × 297. The title block reads project info on its own.
/// </summary>
public static class PrintTemplate
{
    public const string Key = "print_template";
    public const string EnglishA3 = "a3-en";
    public const double WidthMm = 420.0;
    public const double HeightMm = 297.0;

    public static string Resolve(string stored) => EnglishA3;

    public static string Paper(string stored) => "A3";
}
