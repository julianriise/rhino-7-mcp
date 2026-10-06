using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// DM.1 and DM.2: every colour in the window is a token in :root, and the
/// dark palette (:root[data-theme="dark"]) gives every token a value with
/// WCAG AA contrast for text. Nothing sets data-theme yet (DM.3).
/// </summary>
public class PagePaletteTests
{
    static string Html() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
    static string Script() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));

    const string Light = ":root {";
    const string Dark = ":root[data-theme=\"dark\"] {";
    static readonly Regex RawColour = new(@"#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(", RegexOptions.Compiled);

    static string Block(string css, string head)
    {
        var at = css.IndexOf(head, StringComparison.Ordinal);
        Assert.True(at >= 0, "no " + head);
        return css.Substring(at, css.IndexOf('}', at) - at + 1);
    }

    static string Style(string html)
    {
        var at = html.IndexOf("<style>", StringComparison.Ordinal);
        return html.Substring(at, html.IndexOf("</style>", at, StringComparison.Ordinal) - at);
    }

    static Dictionary<string, string> Tokens(string block) =>
        Regex.Matches(block, @"--([a-z-]+):\s*([^;]+);").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

    [Fact]
    public void NoRawColour_OutsideTheTokenBlocks()
    {
        var html = Html();
        var style = Style(html);
        var rest = style.Replace(Block(style, Light), "").Replace(Block(style, Dark), "");
        Assert.Empty(RawColour.Matches(rest).Select(m => m.Value));
        Assert.DoesNotMatch(@"(fill|stroke|color)\s*=\s*""#", html);
        Assert.DoesNotMatch(@"style\s*=\s*""[^""]*(#[0-9a-fA-F]{3,8}|rgba?\()", html);
        Assert.Empty(RawColour.Matches(Script()).Select(m => m.Value));
    }

    [Fact]
    public void EveryToken_HasADarkValue_AndNothingSetsTheThemeYet()
    {
        var style = Style(Html());
        var light = Tokens(Block(style, Light));
        var dark = Tokens(Block(style, Dark));
        Assert.Equal(light.Keys.OrderBy(k => k), dark.Keys.OrderBy(k => k));
        Assert.DoesNotContain("data-theme", Script());
    }

    [Theory]
    [InlineData("ink", "page")]
    [InlineData("ink", "card")]
    [InlineData("meta", "page")]
    [InlineData("meta", "card")]
    [InlineData("faint", "page")]
    [InlineData("faint", "card")]
    [InlineData("ok", "page")]
    [InlineData("ok", "card")]
    [InlineData("bad", "page")]
    [InlineData("bad", "card")]
    [InlineData("brand", "page")]
    [InlineData("brand", "card")]
    [InlineData("on-brand", "brand")]
    [InlineData("ink", "bubble")]
    public void DarkText_MeetsWcagAA(string text, string background)
    {
        var dark = Tokens(Block(Style(Html()), Dark));
        // The user's bubble is the wash over the brand colour.
        var bg = background == "bubble" ? Over(Rgba(dark["wash"]), Rgb(dark["brand"])) : Rgb(dark[background]);
        var ratio = Contrast(Rgb(dark[text]), bg);
        Assert.True(ratio >= 4.5, $"{text} on {background}: {ratio:0.00} < 4.5");
    }

    static double[] Rgb(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        return new[] { 1, 3, 5 }.Select(i => (double)int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber)).ToArray();
    }

    static double[] Rgba(string css)
    {
        var parts = Regex.Match(css, @"rgba\(([^)]*)\)").Groups[1].Value.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts;
    }

    static double[] Over(double[] top, double[] bottom) =>
        Enumerable.Range(0, 3).Select(i => top[3] * top[i] + (1 - top[3]) * bottom[i]).ToArray();

    static double Luminance(double[] c)
    {
        var l = c.Select(v => v / 255).Select(v => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)).ToArray();
        return 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2];
    }

    static double Contrast(double[] a, double[] b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
