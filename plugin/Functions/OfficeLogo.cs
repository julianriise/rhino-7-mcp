using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3: the office's logo, picked once in Project info and printed at the right
/// end of every title block. The file keeps the picked PNG or JPEG itself
/// (document strings, section forsk), so the logo travels with the 3dm. This
/// reads the two formats without System.Drawing: a PNG is inflated into RGB
/// and alpha, a JPEG is kept as it is for the PDF's DCTDecode. Pure, so it
/// tests headless.
/// </summary>
public static class OfficeLogo
{
    public const string Key = "logo";
    public const string NameKey = "logo_name";
    /// <summary>The page object that holds the logo on a layout, and the paper box the PDF draws it in.</summary>
    public const string Role = "title_logo";
    public const string BoxKey = "forsk:logo_box";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const string Unreadable = "That logo could not be read. Pick a PNG or JPEG.";
    public const string TooLarge = "That logo is over 2 MB. Pick a smaller PNG or JPEG.";

    public sealed class Picture
    {
        public int Width;
        public int Height;
        /// <summary>A JPEG's own bytes, drawn as they are. Null for a PNG.</summary>
        public byte[] Jpeg;
        /// <summary>1 grey or 3 RGB, for the JPEG's colour space.</summary>
        public int JpegComponents;
        /// <summary>A PNG's pixels, 3 bytes each, rows top down.</summary>
        public byte[] Rgb;
        /// <summary>A PNG's alpha, 1 byte a pixel, or null when every pixel is opaque.</summary>
        public byte[] Alpha;
        public double Aspect => Height > 0 ? (double)Width / Height : 1;
    }

    /// <summary>The picture in a PNG or JPEG file's bytes, or null with the reason.</summary>
    public static Picture Read(byte[] data, out string error)
    {
        error = null;
        if (data == null || data.Length < 8)
        {
            error = Unreadable;
            return null;
        }
        if (data.Length > MaxBytes)
        {
            error = TooLarge;
            return null;
        }
        Picture picture = null;
        try
        {
            if (data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
                picture = ReadPng(data);
            else if (data[0] == 0xFF && data[1] == 0xD8)
                picture = ReadJpeg(data);
        }
        catch (Exception)
        {
            picture = null;
        }
        if (picture == null || picture.Width <= 0 || picture.Height <= 0) error = Unreadable;
        return error == null ? picture : null;
    }

    /// <summary>The stored logo: the document string as written by Encode.</summary>
    public static Picture Decode(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            return Read(Convert.FromBase64String(stored.Trim()), out _);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Encode(byte[] data) => Convert.ToBase64String(data ?? new byte[0]);

    static Picture ReadJpeg(byte[] data)
    {
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            var length = (data[i + 2] << 8) | data[i + 3];
            // SOF0 to SOF15, without DHT (C4), JPG (C8) and DAC (CC): the frame header with the size.
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                var components = data[i + 9];
                if (components != 1 && components != 3) return null;
                return new Picture
                {
                    Height = (data[i + 5] << 8) | data[i + 6],
                    Width = (data[i + 7] << 8) | data[i + 8],
                    Jpeg = data,
                    JpegComponents = components
                };
            }
            i += 2 + length;
        }
        return null;
    }

    static Picture ReadPng(byte[] data)
    {
        var pos = 8;
        int width = 0, height = 0, depth = 0, colour = -1, interlace = 0;
        byte[] palette = null, paletteAlpha = null;
        var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            var length = (int)BigEndian(data, pos);
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = pos + 8;
            if (length < 0 || body + length > data.Length) return null;
            switch (type)
            {
                case "IHDR":
                    width = (int)BigEndian(data, body);
                    height = (int)BigEndian(data, body + 4);
                    depth = data[body + 8];
                    colour = data[body + 9];
                    interlace = data[body + 12];
                    break;
                case "PLTE":
                    palette = Slice(data, body, length);
                    break;
                case "tRNS":
                    paletteAlpha = Slice(data, body, length);
                    break;
                case "IDAT":
                    idat.Write(data, body, length);
                    break;
            }
            if (type == "IEND") break;
            pos = body + length + 4;
        }
        // 8-bit, not interlaced: what an exported logo is. Others are refused, not guessed.
        if (width <= 0 || height <= 0 || depth != 8 || interlace != 0) return null;
        var channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || (colour == 3 && palette == null)) return null;
        var raw = Inflate(idat.ToArray());
        var stride = width * channels;
        if (raw.Length < (long)height * (stride + 1)) return null;
        var pixels = Unfilter(raw, width, height, channels);

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var opaque = true;
        for (var p = 0; p < width * height; p++)
        {
            byte r, g, b, a = 255;
            var s = p * channels;
            switch (colour)
            {
                case 0: r = g = b = pixels[s]; break;
                case 2: r = pixels[s]; g = pixels[s + 1]; b = pixels[s + 2]; break;
                case 3:
                    var index = pixels[s];
                    if (index * 3 + 2 >= palette.Length) return null;
                    r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                    if (paletteAlpha != null && index < paletteAlpha.Length) a = paletteAlpha[index];
                    break;
                case 4: r = g = b = pixels[s]; a = pixels[s + 1]; break;
                default: r = pixels[s]; g = pixels[s + 1]; b = pixels[s + 2]; a = pixels[s + 3]; break;
            }
            rgb[p * 3] = r;
            rgb[p * 3 + 1] = g;
            rgb[p * 3 + 2] = b;
            alpha[p] = a;
            if (a != 255) opaque = false;
        }
        return new Picture { Width = width, Height = height, Rgb = rgb, Alpha = opaque ? null : alpha };
    }

    /// <summary>The scanlines without their filter bytes, each filter undone.</summary>
    static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var row = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int left = x >= channels ? output[row + x - channels] : 0;
                int up = y > 0 ? output[row - stride + x] : 0;
                int corner = y > 0 && x >= channels ? output[row - stride + x - channels] : 0;
                int value = raw[src + x];
                switch (filter)
                {
                    case 1: value += left; break;
                    case 2: value += up; break;
                    case 3: value += (left + up) / 2; break;
                    case 4: value += Paeth(left, up, corner); break;
                }
                output[row + x] = (byte)value;
            }
        }
        return output;
    }

    static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>A zlib stream's data: the 2-byte header skipped, the deflate body read to its end.</summary>
    static byte[] Inflate(byte[] zlib)
    {
        using (var input = new MemoryStream(zlib, 2, zlib.Length - 2))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    static uint BigEndian(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    static byte[] Slice(byte[] data, int start, int length)
    {
        var copy = new byte[length];
        Array.Copy(data, start, copy, 0, length);
        return copy;
    }

    /// <summary>A paper box as the layout stamps it on the logo: "x0 y0 x1 y1" in mm.</summary>
    public static string FormatBox(double x0, double y0, double x1, double y1) =>
        string.Join(" ", new[] { x0, y0, x1, y1 }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

    public static bool TryParseBox(string text, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = x1 = y1 = 0;
        var parts = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
        x0 = v[0]; y0 = v[1]; x1 = v[2]; y1 = v[3];
        return x1 > x0 && y1 > y0;
    }
}
