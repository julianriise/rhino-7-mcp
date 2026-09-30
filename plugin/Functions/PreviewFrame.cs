using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The Mac page preview as bytes. CopyOpaque makes the frame the PDF draws:
/// a zero-alpha black pixel is an unpainted buffer and turns white, any other
/// pixel is kept opaque. WriteColours puts each colour through SetPixel once,
/// as the old per-pixel copy did. Measure samples its ink on a grid. IsBlank is the
/// retry rule: under InkFloor dark samples, or dark over two fifths of the
/// grid, is a missed paint, not a sheet. Classify names what a frame was, and
/// Attempt is one read of a page as a line in /tmp/forsk-print.log. No
/// System.Drawing and no RhinoCommon, so it tests headless; LayoutPack locks
/// the bitmaps and hands the buffers in.
///
/// The frame long logged as black is not black and not transparent. The
/// saved cap3 frames (2480 x 1754, clear 0, dark 171738/272180) hold 153874
/// samples of one light grey, 235: the model viewport's background, 230,
/// after the copy's colour match. The preview is rendered as 2 x 2 tiles of
/// 1252 x 889 that overlap by 12, and in each tile the top 547 rows are the
/// model view's picture (the garage walls at that view's zoom) with the page
/// drawn below it, 547 rows down. So the page's tiles landed on the picture
/// of the model view, the view the wake had just put on screen. The first
/// and the last of 23 reads are the same file. Wash and band measure that,
/// and Classify names it Viewport.
/// </summary>
public static class PreviewFrame
{
    /// <summary>Grid step of the ink count. Step 4 on the 2480-wide sheet hits wall poché a few pixels thick and a long hairline.</summary>
    public const int InkStep = 4;
    /// <summary>Fewest dark samples a drawn sheet has. Fewer is a missed paint.</summary>
    public const int InkFloor = 400;
    /// <summary>Where a blank frame is saved. The garage smoke copies these into forsk's .smoke-preview/.</summary>
    public const string DebugPngPrefix = "/tmp/forsk-print-";
    /// <summary>
    /// A page's time for reads, in ms. A read without the per-pixel copy is
    /// about a second (wake, idle, preview, copy, check, pause), so a blank
    /// page gets some fifteen reads, and a two-page export that never paints
    /// still ends inside the 300 s MCP command. Two reads used to take ~165 s.
    /// </summary>
    public const int ReadBudgetMs = 20000;
    /// <summary>Reads a blank page always gets, the old two, so a slow first read still gets a second.</summary>
    public const int MinReads = 2;

    /// <summary>After a blank read: read again while the page has had fewer than MinReads, or time left in its budget.</summary>
    public static bool ReadAgain(int reads, long elapsedMs)
        => reads < MinReads || elapsedMs < ReadBudgetMs;

    /// <summary>
    /// What a read gave. None: no bitmap. Empty: mostly unpainted pixels
    /// (transparent black), no ink. White: opaque paper, no ink. Viewport:
    /// dark over two fifths of the grid and most of it wash, the model
    /// view's picture with its light grey background. Black: dark over two
    /// fifths and not wash, the opaque black buffer. Partial: some ink, under
    /// InkFloor. Ink: a sheet. Every kind but Ink is blank.
    /// </summary>
    public enum Kind { None, Empty, White, Viewport, Black, Partial, Ink }

    public static bool IsUnpainted(int b0, int b1, int b2, int alpha)
        => alpha < 16 && b0 < 8 && b1 < 8 && b2 < 8;

    public static bool IsDark(int b0, int b1, int b2) => (b0 + b1 + b2) / 3 < 248;

    /// <summary>Dark but light: a viewport background (230, or 235 after the colour match), never ink.</summary>
    public static bool IsWash(int b0, int b1, int b2)
        => IsDark(b0, b1, b2) && b0 >= 200 && b1 >= 200 && b2 >= 200;

    /// <summary>
    /// Colour times alpha, rounded as the Mac System.Drawing rounds it. It
    /// undoes that library's LockBits un-premultiply for every colour not
    /// over its alpha, the only colours a CoreGraphics bitmap holds.
    /// </summary>
    public static byte Premultiply(int alpha, int c) => (byte)Math.Min(255, (c * alpha + 127) / 255);

    /// <summary>
    /// Copy locked BGRA pixels into opaque BGRA pixels, row by row. The Mac
    /// LockBits un-premultiplies the colour, and GetPixel, which the copy
    /// used to read, does not, so under full alpha the colour is multiplied
    /// back first. Returns how many unpainted pixels turned white.
    /// </summary>
    public static long CopyOpaque(byte[] src, int srcStride, byte[] dst, int dstStride, int width, int height)
    {
        long clear = 0;
        for (int y = 0; y < height; y++)
        {
            int srow = y * srcStride;
            int drow = y * dstStride;
            for (int x = 0; x < width; x++)
            {
                int s = srow + (x * 4);
                int d = drow + (x * 4);
                if (s + 3 >= src.Length || d + 3 >= dst.Length) continue;
                byte b0 = src[s];
                byte b1 = src[s + 1];
                byte b2 = src[s + 2];
                byte alpha = src[s + 3];
                if (alpha < 255)
                {
                    b0 = Premultiply(alpha, b0);
                    b1 = Premultiply(alpha, b1);
                    b2 = Premultiply(alpha, b2);
                }
                if (IsUnpainted(b0, b1, b2, alpha))
                {
                    b0 = b1 = b2 = 255;
                    clear++;
                }
                dst[d] = b0;
                dst[d + 1] = b1;
                dst[d + 2] = b2;
                dst[d + 3] = 255;
            }
        }
        return clear;
    }

    /// <summary>
    /// Hand each colour of an opaque BGRA frame to write, once and as
    /// 0xRRGGBB, and put the colour it gives back on every pixel of that
    /// colour. Returns how many colours the frame has.
    /// </summary>
    public static int WriteColours(byte[] frame, int stride, int width, int height, Func<int[], int[]> write)
    {
        var index = new Dictionary<int, int>();
        var colours = new List<int>();
        int last = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + (x * 4);
                if (i + 2 >= frame.Length) continue;
                int rgb = Rgb(frame, i);
                if (rgb == last) continue;
                last = rgb;
                if (index.ContainsKey(rgb)) continue;
                index[rgb] = colours.Count;
                colours.Add(rgb);
            }
        }
        var written = write(colours.ToArray());
        last = -1;
        int put = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + (x * 4);
                if (i + 2 >= frame.Length) continue;
                int rgb = Rgb(frame, i);
                if (rgb != last)
                {
                    last = rgb;
                    put = written[index[rgb]];
                }
                frame[i] = (byte)put;
                frame[i + 1] = (byte)(put >> 8);
                frame[i + 2] = (byte)(put >> 16);
            }
        }
        return colours.Count;
    }

    static int Rgb(byte[] frame, int i) => frame[i] | (frame[i + 1] << 8) | (frame[i + 2] << 16);

    /// <summary>
    /// A frame on the ink grid. Dark: dark samples. Wash: the dark samples
    /// that are light grey. Band: pixel rows from the top that are dark nine
    /// tenths across, 0 on a sheet and 548 where the first tile holds the
    /// model view's picture.
    /// </summary>
    public struct Ink
    {
        public int Dark;
        public int Wash;
        public int Band;
    }

    /// <summary>The frame's ink on a grid of the given step.</summary>
    public static Ink Measure(byte[] buffer, int stride, int bpp, int width, int height, int step)
    {
        var ink = new Ink();
        bool top = true;
        for (int y = 0; y < height; y += step)
        {
            int row = y * stride;
            int across = 0;
            int dark = 0;
            for (int x = 0; x < width; x += step)
            {
                int i = row + (x * bpp);
                if (i + 2 >= buffer.Length) continue;
                across++;
                if (!IsDark(buffer[i], buffer[i + 1], buffer[i + 2])) continue;
                dark++;
                if (IsWash(buffer[i], buffer[i + 1], buffer[i + 2])) ink.Wash++;
            }
            ink.Dark += dark;
            top = top && across > 0 && dark * 10 >= across * 9;
            if (top) ink.Band = Math.Min(height, y + step);
        }
        return ink;
    }

    /// <summary>Dark samples on a grid of the given step.</summary>
    public static int CountDark(byte[] buffer, int stride, int bpp, int width, int height, int step)
        => Measure(buffer, stride, bpp, width, height, step).Dark;

    public static int Samples(int width, int height)
        => ((width + InkStep - 1) / InkStep) * ((height + InkStep - 1) / InkStep);

    /// <summary>True when the frame is not a sheet, so the page is read again.</summary>
    public static bool IsBlank(int width, int height, int dark)
        => Classify(width, height, dark, 0, 0) != Kind.Ink;

    public static Kind Classify(int width, int height, int dark, long clear, int wash)
    {
        if (width < 2 || height < 2) return Kind.None;
        if (dark * 5L > Samples(width, height) * 2L) return wash * 2L > dark ? Kind.Viewport : Kind.Black;
        if (dark == 0) return clear * 2 > (long)width * height ? Kind.Empty : Kind.White;
        if (dark < InkFloor) return Kind.Partial;
        return Kind.Ink;
    }

    /// <summary>A blank frame's PNG, named by export, page and attempt so no read overwrites another.</summary>
    public static string DebugPngPath(string pdfPath, int page, int attempt)
    {
        var stem = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(pdfPath ?? "") ?? "")
            stem.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        if (stem.Length == 0) stem.Append("pdf");
        return DebugPngPrefix + stem
            + "-p" + page.ToString(CultureInfo.InvariantCulture)
            + "-a" + attempt.ToString(CultureInfo.InvariantCulture) + ".png";
    }

    /// <summary>
    /// One read of a page preview. Raw is what GetPreviewImage returned; the
    /// checked frame is the copy (or raw, when the copy failed). Copy is fast
    /// (drawn once and locked), raw (copy failed, raw kept) or none (no
    /// frame). Idle is false when an
    /// idle wait ran into its cap. Png is the saved blank frame, failed:Type,
    /// or - for a sheet, no frame, or a blank frame between a page's first
    /// and last (not kept). Of is how many reads the page had.
    /// </summary>
    public sealed class Attempt
    {
        public string Pdf = "";
        public int Page;
        public string PageName = "";
        public int Number;
        public int Of;
        public int RawWidth;
        public int RawHeight;
        public string Format = "-";
        public string Copy = "none";
        public int Width;
        public int Height;
        public int Dark;
        public int Wash;
        public int Band;
        public long Clear;
        public bool Idle = true;
        public long PrepMs;
        public long WakeMs;
        public long IdleMs;
        public long PreviewMs;
        public long CopyMs;
        public long CheckMs;
        public string Png = "-";

        public Kind Frame => Classify(Width, Height, Dark, Clear, Wash);
        public bool Blank => IsBlank(Width, Height, Dark);

        public string LogLine()
        {
            var inv = CultureInfo.InvariantCulture;
            var raw = RawWidth > 0 ? RawWidth.ToString(inv) + "x" + RawHeight.ToString(inv) : "null";
            var name = string.IsNullOrWhiteSpace(PageName) ? "-" : PageName.Trim().Replace(' ', '_');
            return "capture " + Pdf
                + " p" + Page.ToString(inv) + " " + name
                + " a" + Number.ToString(inv) + "/" + Of.ToString(inv)
                + " frame=" + Frame.ToString().ToLowerInvariant()
                + " blank=" + (Blank ? "yes" : "no")
                + " ink=" + Dark.ToString(inv) + "/" + (Width < 2 || Height < 2 ? 0 : Samples(Width, Height)).ToString(inv)
                + " wash=" + Wash.ToString(inv)
                + " band=" + Band.ToString(inv)
                + " clear=" + Clear.ToString(inv)
                + " raw=" + raw
                + " fmt=" + Format
                + " copy=" + Copy
                + " idle=" + (Idle ? "fired" : "cap")
                + " ms=prep:" + PrepMs.ToString(inv)
                + ",wake:" + WakeMs.ToString(inv)
                + ",idle:" + IdleMs.ToString(inv)
                + ",preview:" + PreviewMs.ToString(inv)
                + ",copy:" + CopyMs.ToString(inv)
                + ",check:" + CheckMs.ToString(inv)
                + " png=" + Png;
        }
    }
}
