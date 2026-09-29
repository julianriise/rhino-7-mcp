using System;
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
        public int Bpp;
    }

    // A preview buffer as GetPreviewImage hands it over: BGRA or BGR rows, padded.
    static Frame Fill(byte b, byte g, byte r, byte a, int bpp = 4)
    {
        var stride = W * bpp + 8;
        var pixels = new byte[stride * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                Set(pixels, stride, bpp, x, y, b, g, r, a);
        }
        return new Frame { Pixels = pixels, Stride = stride, Bpp = bpp };
    }

    static void Set(byte[] pixels, int stride, int bpp, int x, int y, byte b, byte g, byte r, byte a)
    {
        int i = y * stride + x * bpp;
        pixels[i] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        if (bpp == 4) pixels[i + 3] = a;
    }

    // Opaque black ink on every grid sample in the first rows, `samples` in all.
    static void Ink(Frame frame, int samples)
    {
        for (int n = 0; n < samples; n++)
        {
            int x = (n % (W / PreviewFrame.InkStep)) * PreviewFrame.InkStep;
            int y = (n / (W / PreviewFrame.InkStep)) * PreviewFrame.InkStep;
            Set(frame.Pixels, frame.Stride, frame.Bpp, x, y, 0, 0, 0, 255);
        }
    }

    static PreviewFrame.Attempt Read(Frame frame)
    {
        var copy = new byte[W * 4 * H];
        var clear = PreviewFrame.CopyOpaque(frame.Pixels, frame.Stride, frame.Bpp, copy, W * 4, W, H);
        var dark = PreviewFrame.CountDark(copy, W * 4, 4, W, H, PreviewFrame.InkStep);
        return new PreviewFrame.Attempt { Width = W, Height = H, Dark = dark, Clear = clear };
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
    public void Classify_BlackRgbWithoutAlpha_IsKeptAsInk()
    {
        // A 24 bit frame has no alpha: black there is paint, never unpainted.
        var read = Read(Fill(0, 0, 0, 0, bpp: 3));

        Assert.Equal(0, read.Clear);
        Assert.Equal(PreviewFrame.Kind.Black, read.Frame);
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
                Set(frame.Pixels, frame.Stride, frame.Bpp, x, y, 0, 0, 0, 0);
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

    [Theory]
    // The A3 sheet at 150 dpi: 620 x 439 samples, two fifths is 108872.
    [InlineData(0, true)]
    [InlineData(399, true)]
    [InlineData(400, false)]
    [InlineData(2054, false)]
    [InlineData(108872, false)]
    [InlineData(108873, true)]
    public void IsBlank_KeepsTheInkFloorAndTheBlackRule(int dark, bool blank)
    {
        Assert.Equal(blank, PreviewFrame.IsBlank(2480, 1754, dark));
        Assert.Equal(blank, PreviewFrame.Classify(2480, 1754, dark, 0) != PreviewFrame.Kind.Ink);
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
            + " clear=4349920 raw=2480x1754 fmt=Format32bppPArgb copy=fast idle=cap"
            + " ms=prep:0,wake:950,idle:812,preview:70123,copy:310,check:25"
            + " png=/tmp/forsk-print-forsk-f5-garage-doors-p2-a1.png",
            read.LogLine());
    }

    [Fact]
    public void LogLine_NoFrame_SaysNullAndNone()
    {
        var read = new PreviewFrame.Attempt { Pdf = "a.pdf", Page = 1, PageName = "Plan", Number = 2, Of = 2 };

        Assert.Equal(
            "capture a.pdf p1 Plan a2/2 frame=none blank=yes ink=0/0 clear=0 raw=null fmt=- copy=none idle=fired"
            + " ms=prep:0,wake:0,idle:0,preview:0,copy:0,check:0 png=-",
            read.LogLine());
    }
}
