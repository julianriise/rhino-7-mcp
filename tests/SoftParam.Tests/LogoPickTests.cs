using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Choose logo takes PNG, JPEG and SVG. An SVG becomes a PNG when it is picked,
/// so the layout, the PDF and the DWG/DXF all print the same picture. The
/// Project info card stays open with the picked logo shown, and only Save keeps it.
/// </summary>
public class LogoPickTests
{
    const string Svg = "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 30 10\"><rect width=\"30\" height=\"10\"/></svg>";

    static byte[] OnePixelPng() => OfficeLogoTests.Png(1, 1, 2, new[] { new byte[] { 0, 1, 2, 3 } });

    [Fact]
    public void AnSvg_IsKnownByItsText_APngIsNot()
    {
        Assert.True(OfficeLogo.IsSvg(Encoding.UTF8.GetBytes(Svg)));
        Assert.True(OfficeLogo.IsSvg(Encoding.UTF8.GetBytes("﻿  <svg width=\"10\" height=\"10\"></svg>")));
        Assert.False(OfficeLogo.IsSvg(OnePixelPng()));
        Assert.False(OfficeLogo.IsSvg(Encoding.UTF8.GetBytes("<html><body>no</body></html>")));
    }

    [Fact]
    public void Prepare_TurnsAnSvgIntoThePngItPrints_AndKeepsAPngAsItIs()
    {
        var png = OnePixelPng();
        var fromSvg = OfficeLogo.Prepare(Encoding.UTF8.GetBytes(Svg), svg => png, out var error);
        Assert.Null(error);
        Assert.Same(png, fromSvg);

        Assert.Same(png, OfficeLogo.Prepare(png, svg => throw new InvalidOperationException("not an svg"), out error));
        Assert.Null(error);

        Assert.Null(OfficeLogo.Prepare(Encoding.UTF8.GetBytes(Svg), svg => null, out error));
        Assert.Equal(OfficeLogo.SvgFailed, error);
        Assert.Null(OfficeLogo.Prepare(Encoding.ASCII.GetBytes("%PDF-1.4"), svg => png, out error));
        Assert.Equal(OfficeLogo.Unreadable, error);
        Assert.Contains("SVG", OfficeLogo.Unreadable);
    }

    [Fact]
    public void QuickLook_WritesItsThumbnailBesideTheName()
    {
        Assert.Equal("-t -s 1200 -o \"/tmp/a b\" \"/tmp/a b/logo.svg\"", OfficeLogo.QuickLookArguments("/tmp/a b/logo.svg", "/tmp/a b"));
        Assert.Equal(Path.Combine("/tmp/out", "logo.svg.png"), OfficeLogo.QuickLookOutput("/x/logo.svg", "/tmp/out"));
    }

    static JObject OpenCard() => new JObject { ["kind"] = "meta.title", ["state"] = "open" };

    [Fact]
    public void APickedLogo_KeepsTheCardOpen_ShowsIt_AndWaitsForSave()
    {
        var card = OpenCard();
        ForskCards.HoldLogo(card, "office.svg", "/tmp/forsk-logo-pending.png", OnePixelPng());
        Assert.Equal("open", card["state"]!.ToString());
        Assert.Equal("Logo: office.svg · click Save to keep it.", card["note"]!.ToString());
        Assert.StartsWith("data:image/png;base64,", card["image"]!.ToString());

        var meta = new JObject();
        ForskCards.LogoMeta(card, meta);
        Assert.Equal("/tmp/forsk-logo-pending.png", meta["logo_path"]!.ToString());
        Assert.Equal("office.svg", meta["logo_name"]!.ToString());
        Assert.Null(meta["logo"]);
    }

    [Fact]
    public void RemoveLogo_AlsoWaitsForSave_AndDropsAPickedOne()
    {
        var card = OpenCard();
        ForskCards.HoldLogo(card, "office.png", "/tmp/p.png", OnePixelPng());
        ForskCards.HoldLogoRemoval(card);
        Assert.Equal("The logo is removed when you click Save.", card["note"]!.ToString());
        Assert.Null(card["image"]);
        var meta = new JObject();
        ForskCards.LogoMeta(card, meta);
        Assert.Equal("", meta["logo"]!.ToString());
        Assert.Null(meta["logo_path"]);
    }

    [Fact]
    public void NothingHeld_LeavesTheLogoAlone_AndABigLogoShowsNoPreview()
    {
        var meta = new JObject();
        ForskCards.LogoMeta(OpenCard(), meta);
        Assert.Empty(meta);

        Assert.Null(OfficeLogo.PreviewUrl(new byte[OfficeLogo.PreviewMaxBytes + 1]));
        Assert.StartsWith("data:image/jpeg;base64,", OfficeLogo.PreviewUrl(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
    }
}
