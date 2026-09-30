using System;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F5.2 capture diagnosis: a page preview read goes through the same copy,
/// ink count and blank rule as the PDF export, and the frame it gave is named
/// white, black, empty (unpainted), partly painted or a sheet. The print log
/// line carries it all on one line.
/// </summary>
public class PreviewFrameTests
{
    const int W = 400;
    const int H = 200;
    // 100 x 50 samples at step 4.
    const int Samples = 5000;

    sealed class Frame
    {
        public byte[] Pixels;
        public int Stride;
    }

    // A locked preview buffer: BGRA rows, padded.
    static Frame Fill(byte b, byte g, byte r, byte a)
    {
        var stride = W * 4 + 8;
        var pixels = new byte[stride * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                Set(pixels, stride, x, y, b, g, r, a);
        }
        return new Frame { Pixels = pixels, Stride = stride };
    }

    static void Set(byte[] pixels, int stride, int x, int y, byte b, byte g, byte r, byte a)
    {
        int i = y * stride + x * 4;
        pixels[i] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = a;
    }

    // Opaque black ink on every grid sample in the first rows, `samples` in all.
    static void Ink(Frame frame, int samples)
    {
        for (int n = 0; n < samples; n++)
        {
            int x = (n % (W / PreviewFrame.InkStep)) * PreviewFrame.InkStep;
            int y = (n / (W / PreviewFrame.InkStep)) * PreviewFrame.InkStep;
            Set(frame.Pixels, frame.Stride, x, y, 0, 0, 0, 255);
        }
    }

    static PreviewFrame.Attempt Read(Frame frame)
    {
        var copy = new byte[W * 4 * H];
        var clear = PreviewFrame.CopyOpaque(frame.Pixels, frame.Stride, copy, W * 4, W, H);
        var ink = PreviewFrame.Measure(copy, W * 4, 4, W, H, PreviewFrame.InkStep);
        return new PreviewFrame.Attempt
        {
            Width = W, Height = H, Dark = ink.Dark, Wash = ink.Wash, Band = ink.Band, Clear = clear
        };
    }

    // The frame the garage cap3 saved, to scale: 2 x 2 tiles, and in each the
    // top 62 of 100 rows hold the model view (its background after the copy's
    // colour match, a wall across it) with the page below, white with ink.
    static Frame ModelViewOnTiles(byte background)
    {
        var frame = Fill(255, 255, 255, 255);
        for (int y = 0; y < H; y++)
        {
            int row = y % (H / 2);
            for (int x = 0; x < W; x++)
            {
                if (row < 62)
                    Set(frame.Pixels, frame.Stride, x, y, background, background, background, 255);
                if (row >= 32 && row < 36 || row >= 80 && row < 84 && x % 8 == 0)
                    Set(frame.Pixels, frame.Stride, x, y, 0, 0, 0, 255);
            }
        }
        return frame;
    }

    [Fact]
    public void Classify_ModelViewPictureOnTheTiles_IsViewportNotBlackNotEmpty()
    {
        var read = Read(ModelViewOnTiles(235));

        Assert.Equal(PreviewFrame.Kind.Viewport, read.Frame);
        Assert.True(read.Blank);
        // Opaque: nothing of it is the unpainted buffer.
        Assert.Equal(0, read.Clear);
        // Rows 0..60 of the grid are the first tile's model view.
        Assert.Equal(64, read.Band);
        // Per tile: 16 grid rows of model view, one of them the wall, and half a row of page ink.
        Assert.Equal(2 * (1600 + 50), read.Dark);
        Assert.Equal(2 * 1500, read.Wash);
    }

    [Fact]
    public void Classify_TheSameTilesInBlack_IsBlack()
    {
        var read = Read(ModelViewOnTiles(0));

        Assert.Equal(PreviewFrame.Kind.Black, read.Frame);
        Assert.True(read.Blank);
        Assert.Equal(0, read.Wash);
        Assert.Equal(64, read.Band);
    }

    [Fact]
    public void Measure_SheetWithItsInkBelowTheMargin_HasNoBandAndNoWash()
    {
        var frame = Fill(255, 255, 255, 255);
        for (int y = 100; y < 116; y++)
        {
            for (int x = 0; x < W; x++)
                Set(frame.Pixels, frame.Stride, x, y, 0, 0, 0, 255);
        }
        var read = Read(frame);

        Assert.Equal(PreviewFrame.Kind.Ink, read.Frame);
        Assert.Equal(PreviewFrame.InkFloor, read.Dark);
        Assert.Equal(0, read.Wash);
        Assert.Equal(0, read.Band);
    }

    [Theory]
    // The saved cap3 frames, 2480 x 1754: 171738 dark, 153874 of them the
    // model view's grey, the first tile's 547 rows on the grid.
    [InlineData(171738, 153874, PreviewFrame.Kind.Viewport)]
    // The same dark count with no grey in it is the black buffer.
    [InlineData(171738, 0, PreviewFrame.Kind.Black)]
    [InlineData(272180, 0, PreviewFrame.Kind.Black)]
    // A sheet keeps its name whatever grey its hatches have.
    [InlineData(12674, 9000, PreviewFrame.Kind.Ink)]
    public void Classify_TellsTheModelViewFromTheBlackBuffer(int dark, int wash, PreviewFrame.Kind kind)
    {
        Assert.Equal(kind, PreviewFrame.Classify(2480, 1754, dark, 0, wash));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1000)]
    [InlineData(4, 2000)]
    [InlineData(5, 4000)]
    [InlineData(6, 8000)]
    [InlineData(7, 8000)]
    [InlineData(40, 8000)]
    public void SettleMs_DoublesFromTheThirdReadToItsCap(int read, int ms)
    {
        Assert.Equal(ms, PreviewFrame.SettleMs(read));
    }

    [Fact]
    public void Classify_OpaqueWhitePaper_IsWhiteAndBlank()
    {
        var read = Read(Fill(255, 255, 255, 255));

        Assert.Equal(0, read.Dark);
        Assert.Equal(0, read.Clear);
        Assert.Equal(PreviewFrame.Kind.White, read.Frame);
        Assert.True(read.Blank);
    }

    [Fact]
    public void Classify_UnpaintedBuffer_TurnsWhiteButIsEmpty()
    {
        var read = Read(Fill(0, 0, 0, 0));

        Assert.Equal(0, read.Dark);
        Assert.Equal((long)W * H, read.Clear);
        Assert.Equal(PreviewFrame.Kind.Empty, read.Frame);
        Assert.True(read.Blank);
    }

    [Fact]
    public void Classify_OpaqueBlackBuffer_IsBlackAndBlank()
    {
        var read = Read(Fill(0, 0, 0, 255));

        Assert.Equal(Samples, read.Dark);
        Assert.Equal(0, read.Clear);
        Assert.Equal(PreviewFrame.Kind.Black, read.Frame);
        Assert.True(read.Blank);
    }

    [Fact]
    public void Classify_FewLinesOnPaper_IsPartialAndBlank()
    {
        var frame = Fill(255, 255, 255, 255);
        Ink(frame, PreviewFrame.InkFloor - 1);
        var read = Read(frame);

        Assert.Equal(PreviewFrame.InkFloor - 1, read.Dark);
        Assert.Equal(PreviewFrame.Kind.Partial, read.Frame);
        Assert.True(read.Blank);
    }

    [Fact]
    public void Classify_InkOnHalfUnpaintedBuffer_IsPartialAndCountsTheUnpaintedHalf()
    {
        var frame = Fill(255, 255, 255, 255);
        for (int y = H / 2; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                Set(frame.Pixels, frame.Stride, x, y, 0, 0, 0, 0);
        }
        Ink(frame, 120);
        var read = Read(frame);

        Assert.Equal(120, read.Dark);
        Assert.Equal((long)W * (H / 2), read.Clear);
        Assert.Equal(PreviewFrame.Kind.Partial, read.Frame);
        Assert.True(read.Blank);
    }

    [Fact]
    public void Classify_DrawnSheet_IsInkAndNotBlank()
    {
        var frame = Fill(255, 255, 255, 255);
        Ink(frame, PreviewFrame.InkFloor);
        var read = Read(frame);

        Assert.Equal(PreviewFrame.Kind.Ink, read.Frame);
        Assert.False(read.Blank);
    }

    [Fact]
    public void Classify_NoBitmap_IsNoneAndBlank()
    {
        var read = new PreviewFrame.Attempt();

        Assert.Equal(PreviewFrame.Kind.None, read.Frame);
        Assert.True(read.Blank);
    }

    // Rhino 7 Mac System.Drawing, the CoreGraphics build in RhCore.framework.
    // The preview bitmap holds premultiplied RGBA, stride W * 4. GetPixel
    // hands a pixel over as those bytes. LockBits hands the frame over as
    // BGRA with the colour un-premultiplied through this table
    // (ConversionHelpers.CalculateTables).
    static byte MacUnpremultiply(int alpha, int c)
        => (byte)(alpha == 0 ? c : Math.Min(255, (255 * c + alpha / 2) / alpha));

    static byte[] MacLockBits(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            bgra[i] = a < 255 ? MacUnpremultiply(a, rgba[i + 2]) : rgba[i + 2];
            bgra[i + 1] = a < 255 ? MacUnpremultiply(a, rgba[i + 1]) : rgba[i + 1];
            bgra[i + 2] = a < 255 ? MacUnpremultiply(a, rgba[i]) : rgba[i];
            bgra[i + 3] = (byte)a;
        }
        return bgra;
    }

    // SetPixel fills with a generic RGB colour, and the device RGB bitmap
    // colour-matches it: on the Mac grey 128 lands as 146, black and white
    // stay. This curve stands in for it.
    static int SetPixelColour(int rgb)
    {
        int Channel(int shift) => (int)Math.Round(255 * Math.Pow(((rgb >> shift) & 255) / 255.0, 1.8 / 2.2));
        return Channel(0) | (Channel(8) << 8) | (Channel(16) << 16);
    }

    // copy=pixel, the old path: GetPixel each pixel, unpainted to white, the
    // rest opaque, SetPixel into the 32bpp ARGB copy. Returns the copy's BGRA.
    static byte[] PixelPath(byte[] rgba, out long clear)
    {
        var bgra = new byte[rgba.Length];
        clear = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            // Color.FromArgb(d[3], d[0], d[1], d[2])
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
            if (PreviewFrame.IsUnpainted(r, g, b, a))
            {
                r = g = b = 255;
                clear++;
            }
            int put = SetPixelColour(b | (g << 8) | (r << 16));
            bgra[i] = (byte)put;
            bgra[i + 1] = (byte)(put >> 8);
            bgra[i + 2] = (byte)(put >> 16);
            bgra[i + 3] = 255;
        }
        return bgra;
    }

    // copy=fast: the frame drawn 1:1 into a 32bpp ARGB bitmap and locked,
    // copied opaque, each colour SetPixel once.
    static byte[] FastPath(byte[] rgba, out long clear)
    {
        var bgra = new byte[rgba.Length];
        clear = PreviewFrame.CopyOpaque(MacLockBits(rgba), W * 4, bgra, W * 4, W, H);
        PreviewFrame.WriteColours(bgra, W * 4, W, H, all => all.Select(SetPixelColour).ToArray());
        return bgra;
    }

    static void Paint(byte[] rgba, int x, int y, int r, int g, int b, int a)
    {
        int i = (y * W + x) * 4;
        rgba[i] = (byte)r;
        rgba[i + 1] = (byte)g;
        rgba[i + 2] = (byte)b;
        rgba[i + 3] = (byte)a;
    }

    static byte[] Native(int r, int g, int b, int a)
    {
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                Paint(rgba, x, y, r, g, b, a);
        }
        return rgba;
    }

    // Wall lines every 8 rows, a grey hatch and an anti-aliased edge between.
    static void Draw(byte[] rgba, int rows, int lines)
    {
        for (int n = 0; n < lines; n++)
        {
            int y = (n * 8) % rows;
            int x0 = (n * 8) / rows * 100;
            for (int x = x0; x < x0 + 96 && x < W; x++)
            {
                Paint(rgba, x, y, 0, 0, 0, 255);
                Paint(rgba, x, y + 1, 128, 128, 128, 255);
                Paint(rgba, x, y + 2, 246, 246, 246, 255);
                Paint(rgba, x, y + 3, 247, 247, 247, 255);
            }
        }
    }

    static byte[] Fixture(string name)
    {
        switch (name)
        {
            case "sheet":
            {
                var rgba = Native(255, 255, 255, 255);
                Draw(rgba, H, 40);
                return rgba;
            }
            case "black":
                return Native(0, 0, 0, 255);
            case "grey band":
            {
                // cap3: the model view's light grey picture over part of the sheet.
                var rgba = Native(255, 255, 255, 255);
                Draw(rgba, H, 40);
                for (int y = 0; y < H * 3 / 5; y++)
                {
                    for (int x = 0; x < W; x++)
                        Paint(rgba, x, y, 231, 231, 231, 255);
                }
                return rgba;
            }
            case "partly painted":
            {
                // The top third painted, the rest the unpainted buffer, and
                // the rows between half covered: premultiplied, alpha under 255.
                var rgba = Native(0, 0, 0, 0);
                for (int y = 0; y < H / 3; y++)
                {
                    for (int x = 0; x < W; x++)
                        Paint(rgba, x, y, 255, 255, 255, 255);
                }
                Draw(rgba, H / 3 - 8, 3);
                for (int x = 0; x < W; x++)
                {
                    int a = 1 + (x * 253 / (W - 1));
                    Paint(rgba, x, H / 3, a, a, a, a);
                    Paint(rgba, x, H / 3 + 1, 0, 0, 0, a);
                    Paint(rgba, x, H / 3 + 2, a / 2, a / 3, a / 4, a);
                    Paint(rgba, x, H / 3 + 3, Math.Min(a, 7), Math.Min(a, 7), Math.Min(a, 7), a);
                }
                return rgba;
            }
            case "empty":
                return Native(0, 0, 0, 0);
            default:
            {
                // Any premultiplied pixel: colour never over alpha.
                var random = new Random(52);
                var rgba = new byte[W * H * 4];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    int a = random.Next(3) == 0 ? 255 : random.Next(256);
                    rgba[i] = (byte)random.Next(a + 1);
                    rgba[i + 1] = (byte)random.Next(a + 1);
                    rgba[i + 2] = (byte)random.Next(a + 1);
                    rgba[i + 3] = (byte)a;
                }
                return rgba;
            }
        }
    }

    [Theory]
    [InlineData("sheet", PreviewFrame.Kind.Ink)]
    [InlineData("black", PreviewFrame.Kind.Black)]
    [InlineData("grey band", PreviewFrame.Kind.Viewport)]
    [InlineData("partly painted", PreviewFrame.Kind.Partial)]
    [InlineData("empty", PreviewFrame.Kind.Empty)]
    [InlineData("random", PreviewFrame.Kind.Black)]
    public void FastCopy_GivesThePixelCopysPixelsAndInk(string fixture, PreviewFrame.Kind kind)
    {
        var rgba = Fixture(fixture);

        var pixel = PixelPath(rgba, out var pixelClear);
        var fast = FastPath(rgba, out var fastClear);

        Assert.Equal(pixel, fast);
        Assert.Equal(pixelClear, fastClear);
        var pixelInk = PreviewFrame.Measure(pixel, W * 4, 4, W, H, PreviewFrame.InkStep);
        var fastInk = PreviewFrame.Measure(fast, W * 4, 4, W, H, PreviewFrame.InkStep);
        Assert.Equal(pixelInk.Dark, fastInk.Dark);
        Assert.Equal(pixelInk.Wash, fastInk.Wash);
        Assert.Equal(pixelInk.Band, fastInk.Band);
        Assert.Equal(kind, PreviewFrame.Classify(W, H, fastInk.Dark, fastClear, fastInk.Wash));
    }

    [Fact]
    public void Premultiply_UndoesTheMacLockBitsForEveryPremultipliedColour()
    {
        for (int a = 0; a < 256; a++)
        {
            for (int c = 0; c <= a; c++)
                Assert.Equal(c, PreviewFrame.Premultiply(a, MacUnpremultiply(a, c)));
        }
    }

    [Fact]
    public void WriteColours_HandsEachColourOverOnce()
    {
        var rgba = Fixture("sheet");
        var bgra = new byte[rgba.Length];
        PreviewFrame.CopyOpaque(MacLockBits(rgba), W * 4, bgra, W * 4, W, H);
        int calls = 0;
        int[] seen = null;

        var count = PreviewFrame.WriteColours(bgra, W * 4, W, H, all =>
        {
            calls++;
            seen = all;
            return all;
        });

        Assert.Equal(1, calls);
        Assert.Equal(5, count);
        // First seen first: row 0 starts on a wall line.
        Assert.Equal(new[] { 0x000000, 0xFFFFFF, 0x808080, 0xF6F6F6, 0xF7F7F7 }, seen);
    }

    [Theory]
    // The A3 sheet at 150 dpi: 620 x 439 samples, two fifths is 108872.
    [InlineData(0, true)]
    [InlineData(399, true)]
    [InlineData(400, false)]
    [InlineData(2054, false)]
    [InlineData(108872, false)]
    [InlineData(108873, true)]
    // The garage cap3 frame, and the office schedules read of 09-29 21:52.
    [InlineData(171738, true)]
    [InlineData(174976, true)]
    public void IsBlank_KeepsTheInkFloorAndTheBlackRule(int dark, bool blank)
    {
        Assert.Equal(blank, PreviewFrame.IsBlank(2480, 1754, dark));
        Assert.Equal(blank, PreviewFrame.Classify(2480, 1754, dark, 0, 0) != PreviewFrame.Kind.Ink);
        // Wash only names a blank frame. It never makes a sheet blank or a blank frame a sheet.
        Assert.Equal(blank, PreviewFrame.Classify(2480, 1754, dark, 0, dark) != PreviewFrame.Kind.Ink);
    }

    [Theory]
    // A slow first read (the old 15 s wake) still gets its second.
    [InlineData(1, 0, true)]
    [InlineData(1, 90000, true)]
    [InlineData(2, 900, true)]
    [InlineData(2, 19999, true)]
    [InlineData(14, 19999, true)]
    [InlineData(2, 20000, false)]
    [InlineData(15, 20400, false)]
    public void ReadAgain_UntilTheBudgetButNeverUnderTwoReads(int reads, long ms, bool again)
    {
        Assert.Equal(20000, PreviewFrame.ReadBudgetMs);
        Assert.Equal(again, PreviewFrame.ReadAgain(reads, ms));
    }

    [Fact]
    public void DebugPngPath_NamesExportPageAndAttempt()
    {
        Assert.Equal(
            "/tmp/forsk-print-forsk-f5-garage-doors-p2-a1.png",
            PreviewFrame.DebugPngPath("/tmp/forsk-f5-garage-doors.pdf", 2, 1));
        Assert.Equal(
            "/tmp/forsk-print-my-sheet-p1-a2.png",
            PreviewFrame.DebugPngPath("/Users/x/my sheet.pdf", 1, 2));
    }

    [Fact]
    public void LogLine_CarriesFrameSizeFormatCopyPathAndPhases()
    {
        var read = new PreviewFrame.Attempt
        {
            Pdf = "forsk-f5-garage-doors.pdf",
            Page = 2,
            PageName = "Schedules",
            Number = 1,
            Of = 2,
            RawWidth = 2480,
            RawHeight = 1754,
            Format = "Format32bppPArgb",
            Copy = "fast",
            Width = 2480,
            Height = 1754,
            Dark = 0,
            Clear = 2480L * 1754,
            Idle = false,
            PrepMs = 0,
            WakeMs = 950,
            IdleMs = 812,
            PreviewMs = 70123,
            CopyMs = 310,
            CheckMs = 25,
            Png = "/tmp/forsk-print-forsk-f5-garage-doors-p2-a1.png"
        };

        Assert.Equal(
            "capture forsk-f5-garage-doors.pdf p2 Schedules a1/2 frame=empty blank=yes ink=0/272180"
            + " wash=0 band=0 clear=4349920 raw=2480x1754 fmt=Format32bppPArgb copy=fast"
            + " via=model settle=0 paint=0+0 other=0 idle=cap"
            + " ms=prep:0,wake:950,paint:0,idle:812,preview:70123,copy:310,check:25"
            + " png=/tmp/forsk-print-forsk-f5-garage-doors-p2-a1.png",
            read.LogLine());
    }

    [Fact]
    public void LogLine_ModelViewFrame_SaysViewportWashBandAndHowThePageWasReached()
    {
        var read = new PreviewFrame.Attempt
        {
            Pdf = "forsk-f5-garage-cap3.pdf",
            Page = 1,
            PageName = "Plan",
            Number = 3,
            Of = 4,
            RawWidth = 2480,
            RawHeight = 1754,
            Format = "Undefined",
            Copy = "fast",
            Width = 2480,
            Height = 1754,
            Dark = 171738,
            Wash = 153874,
            Band = 548,
            Via = "page",
            SettleMs = 1000,
            Paint = 1,
            ReadPaint = 4,
            OtherPaint = 2,
            WakeMs = 1230,
            PaintMs = 40,
            IdleMs = 160,
            PreviewMs = 190,
            CopyMs = 211,
            CheckMs = 25,
            Png = "/tmp/forsk-print-forsk-f5-garage-cap3-p1-a3.png"
        };

        Assert.Equal(
            "capture forsk-f5-garage-cap3.pdf p1 Plan a3/4 frame=viewport blank=yes ink=171738/272180"
            + " wash=153874 band=548 clear=0 raw=2480x1754 fmt=Undefined copy=fast"
            + " via=page settle=1000 paint=1+4 other=2 idle=fired"
            + " ms=prep:0,wake:1230,paint:40,idle:160,preview:190,copy:211,check:25"
            + " png=/tmp/forsk-print-forsk-f5-garage-cap3-p1-a3.png",
            read.LogLine());
    }

    [Fact]
    public void LogLine_NoFrame_SaysNullAndNone()
    {
        var read = new PreviewFrame.Attempt { Pdf = "a.pdf", Page = 1, PageName = "Plan", Number = 2, Of = 2 };

        Assert.Equal(
            "capture a.pdf p1 Plan a2/2 frame=none blank=yes ink=0/0 wash=0 band=0 clear=0 raw=null fmt=- copy=none"
            + " via=model settle=0 paint=0+0 other=0 idle=fired"
            + " ms=prep:0,wake:0,paint:0,idle:0,preview:0,copy:0,check:0 png=-",
            read.LogLine());
    }
}
