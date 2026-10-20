using System.Buffers.Binary;
using System.IO.Compression;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Draws a 32x32 LabVIEW icon and encodes it as a PNG, with no imaging dependency.
///
/// This exists because of a measurement, not for elegance. Profiling one whole VI generation
/// (41 tool calls, 455 s) showed the icon costing 21.1 s, of which only 8.6 s was
/// lvai_set_vi_icon actually reaching VI Server - the other 12.5 s was an agent composing and
/// running a PowerShell call that drew the bitmap through System.Drawing. Producing the image
/// server-side removes that step and one whole tool call with it, and a tool call is worth about
/// 11 s of wall clock in a generation session. Numbers in docs/aixml-reference.md.
///
/// Dependency-free is deliberate. System.Drawing.Common is Windows-only and a package; a PNG
/// encoder for one fixed size is about eighty lines, and IconTools already reads PNG headers by
/// hand for exactly the same reason.
///
/// THE TEXT IS THE ICON EDITOR'S FONT. LabVIEW's icon editor writes icon text in the Windows
/// raster font "Small Fonts" at 7 px, so that is what a hand-made icon looks like, and
/// <see cref="IconFont"/> holds those glyphs, generated from the font by
/// scripts\Export-IconFont.ps1. Until 2026-10-20 this class drew a 5x7 font of its own - capitals
/// only, five per line, banner rows 1-9 - and in a real toolkit its icons stood out beside the
/// hand-made ones: bigger letters, text touching the frame, five-letter abbreviations. The layout
/// is the icon editor's too, measured 2026-10-20 on six hand-made icons of one LabVIEW toolkit (one
/// of them is reproduced pixel for pixel by the golden test in IconImageTests): 1 px frame, banner
/// rows 1-10, a frame-coloured separator at row 11, body rows 12-30; every line centred on its ink,
/// x = floor((32 - inkWidth) / 2); cap tops at <see cref="BannerTop"/> for the banner and at
/// <see cref="BodyTops"/> for one, two or three body lines; no line wider than
/// <see cref="MaxLineWidth"/>. docs/vi-server-reference.md has the measurements.
/// </summary>
internal static class IconImage
{
    internal const int Size = 32;

    /// <summary>
    /// The widest line, in pixels of ink. 28 leaves one pixel of air between the text and the
    /// frame on both sides of the 30 px interior - what the hand-made icons keep. About six
    /// capitals: the glyphs are variable width (I is 1 px of ink, M and W are 5) with 1 px spacing.
    /// </summary>
    internal const int MaxLineWidth = 28;

    /// <summary>Last banner row; the separator line is the row under it.</summary>
    private const int BannerBottom = 10;

    /// <summary>Cap top of the banner text, so it covers rows 3..8: five of caps, one descender.</summary>
    internal const int BannerTop = 3;

    /// <summary>
    /// Cap tops of the body lines, indexed by how many there are. Each line takes six rows (cap
    /// height 5 plus the font's one descender row), so three lines 6 rows apart touch and never
    /// overlap, and the third line's descender ends on row 30, inside the frame.
    /// </summary>
    internal static readonly int[][] BodyTops = [[], [19], [15, 23], [13, 19, 25]];

    /// <summary>Whether the font can draw this character. Case is drawn as given.</summary>
    internal static bool Supports(char c) => IconFont.Glyphs.ContainsKey(c);

    /// <summary>
    /// Everything in <paramref name="text"/> the font cannot draw, de-duplicated, so the tool can
    /// warn about it instead of quietly leaving gaps.
    /// </summary>
    internal static string Unsupported(string? text) =>
        text is null ? "" : new string(text.Where(c => !Supports(c)).Distinct().ToArray());

    /// <summary>
    /// The width of <paramref name="text"/>'s ink in pixels, first inked column to last; 0 for
    /// text with no ink. Undrawable characters are left out, as <see cref="Render"/> leaves them out.
    /// </summary>
    internal static int InkWidth(string? text) => Layout(text).Width;

    /// <summary>Whether <paramref name="text"/> fits in <see cref="MaxLineWidth"/>.</summary>
    internal static bool Fits(string? text) => InkWidth(text) <= MaxLineWidth;

    /// <summary>
    /// The longest start of <paramref name="text"/> (trimmed) whose ink fits in
    /// <see cref="MaxLineWidth"/> - what <see cref="Render"/> draws of a line that is too wide.
    /// </summary>
    internal static string Fit(string? text)
    {
        var line = text?.Trim() ?? "";
        while (line.Length > 0 && !Fits(line)) line = line[..^1].TrimEnd();
        return line;
    }

    /// <summary>
    /// A line laid out from x = 0: the glyph cells side by side, each advanced by its own width,
    /// as GDI's TextOut places a raster font. Ink is the first and last inked column.
    /// </summary>
    private sealed record LineLayout(List<(int X, string[] Rows)> Cells, int InkLeft, int Width);

    private static LineLayout Layout(string? text)
    {
        var cells = new List<(int X, string[] Rows)>();
        int x = 0, left = int.MaxValue, right = int.MinValue;
        foreach (var c in text?.Trim() ?? "")
        {
            if (!IconFont.Glyphs.TryGetValue(c, out var glyph)) continue;   // reported by Unsupported()
            var rows = glyph.Split('|');
            for (var col = 0; col < rows[0].Length; col++)
                if (rows.Any(r => r[col] == '#'))
                {
                    left = Math.Min(left, x + col);
                    right = Math.Max(right, x + col);
                }
            cells.Add((x, rows));
            x += rows[0].Length;
        }
        return right < left ? new LineLayout(cells, 0, 0) : new LineLayout(cells, left, right - left + 1);
    }

    /// <summary>An 8-bit-per-channel colour. No alpha: LabVIEW icons are opaque.</summary>
    internal readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>
    /// Parse "RRGGBB" or "#RRGGBB". Returns null for anything else, so a caller can report the
    /// offending string rather than silently drawing black.
    /// </summary>
    internal static Rgb? ParseColor(string? text)
    {
        if (text is null) return null;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        return byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? new Rgb(r, g, b)
            : null;
    }

    /// <summary>
    /// Black or white, whichever stays legible on <paramref name="background"/>. Chosen from the fill rather than
    /// exposed as two more parameters, because the one thing a caller must not be able to do is
    /// ask for an icon whose text cannot be read. Rec. 601 luma, threshold at the midpoint.
    /// </summary>
    internal static Rgb ReadableOn(Rgb background) =>
        (299 * background.R + 587 * background.G + 114 * background.B) / 1000 < 128
            ? new Rgb(0xFF, 0xFF, 0xFF)
            : new Rgb(0x00, 0x00, 0x00);

    /// <summary>
    /// The six values LabVIEW quantises each channel to. Measured, not assumed - see
    /// vi-server-reference.md, "Writing back: setting a VI's icon": an icon read back out of a VI
    /// has every channel in this set, 189 of 1 024 pixels having moved on a non-web-safe image.
    /// </summary>
    private static readonly byte[] WebSafe = [0x00, 0x33, 0x66, 0x99, 0xCC, 0xFF];

    /// <summary>Whether LabVIEW will store this colour unchanged.</summary>
    internal static bool IsWebSafe(Rgb c) =>
        WebSafe.Contains(c.R) && WebSafe.Contains(c.G) && WebSafe.Contains(c.B);

    /// <summary>What LabVIEW will turn this colour into, so a caller can be told up front.</summary>
    internal static Rgb Quantise(Rgb c) => new(Nearest(c.R), Nearest(c.G), Nearest(c.B));

    private static byte Nearest(byte channel)
    {
        var best = WebSafe[0];
        foreach (var candidate in WebSafe)
            if (Math.Abs(candidate - channel) < Math.Abs(best - channel)) best = candidate;
        return best;
    }

    /// <summary>
    /// A 32x32 PNG in the icon editor's layout: 1 px frame, a coloured banner carrying
    /// <paramref name="line1"/> in <paramref name="bannerText"/>, a separator line in
    /// <paramref name="border"/>, and <paramref name="line2"/>, <paramref name="line3"/> and
    /// <paramref name="line4"/> as up to three body lines on <paramref name="background"/>. Empty
    /// body lines are skipped, and the rest are spaced for how many remain. A line wider than
    /// <see cref="MaxLineWidth"/> is cut to <see cref="Fit"/> - the caller is expected to have
    /// warned already. line4 is last and optional so that callers written for two body lines keep
    /// compiling and drawing what they drew.
    /// </summary>
    internal static byte[] Render(
        string? line1, string? line2, string? line3,
        Rgb banner, Rgb background, Rgb border, Rgb bannerText, Rgb bodyText,
        string? line4 = null)
    {
        var pixels = new Rgb[Size * Size];
        Array.Fill(pixels, background);

        // Banner rows 1..10: the font's six rows (caps plus descender) with air above and below.
        for (var y = 1; y <= BannerBottom; y++)
            for (var x = 1; x < Size - 1; x++)
                pixels[y * Size + x] = banner;

        // Frame and separator after the fills, so the corners stay exact whatever the fills do.
        for (var i = 0; i < Size; i++)
        {
            pixels[i] = border;                              // top
            pixels[(BannerBottom + 1) * Size + i] = border;  // separator under the banner
            pixels[(Size - 1) * Size + i] = border;          // bottom
            pixels[i * Size] = border;                       // left
            pixels[i * Size + Size - 1] = border;            // right
        }

        DrawLine(pixels, line1, BannerTop, bannerText);

        var body = new[] { line2, line3, line4 }.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        for (var i = 0; i < body.Length; i++)
            DrawLine(pixels, body[i], BodyTops[body.Length][i], bodyText);

        return Encode(pixels);
    }

    /// <summary>Draw one line centred on its ink, with its cap top at row <paramref name="top"/>.</summary>
    private static void DrawLine(Rgb[] pixels, string? text, int top, Rgb colour)
    {
        var layout = Layout(Fit(text));
        if (layout.Width == 0) return;

        var x0 = (Size - layout.Width) / 2 - layout.InkLeft;
        foreach (var (cellX, rows) in layout.Cells)
            for (var row = 0; row < rows.Length; row++)
                for (var col = 0; col < rows[row].Length; col++)
                {
                    if (rows[row][col] != '#') continue;
                    var x = x0 + cellX + col;
                    var y = top + row;
                    if (x > 0 && x < Size - 1 && y > 0 && y < Size - 1)
                        pixels[y * Size + x] = colour;
                }
    }

    /// <summary>
    /// Encode as 8-bit truecolour PNG: signature, IHDR, one IDAT, IEND. Each scanline is
    /// prefixed with filter type 0 (none) - at 32x32 a filter would save bytes nobody counts.
    /// </summary>
    private static byte[] Encode(Rgb[] pixels)
    {
        var raw = new byte[Size * (1 + Size * 3)];
        var at = 0;
        for (var y = 0; y < Size; y++)
        {
            raw[at++] = 0;                                // filter: none
            for (var x = 0; x < Size; x++)
            {
                var p = pixels[y * Size + x];
                raw[at++] = p.R;
                raw[at++] = p.G;
                raw[at++] = p.B;
            }
        }

        using var deflated = new MemoryStream();
        // PNG's IDAT is a zlib stream (RFC 1950), which is exactly what ZLibStream writes -
        // DeflateStream would emit a raw deflate stream and every decoder would reject it.
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), Size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), Size);
        ihdr[8] = 8;      // bit depth
        ihdr[9] = 2;      // colour type 2: truecolour RGB
        ihdr[10] = 0;     // compression: deflate
        ihdr[11] = 0;     // filter method 0
        ihdr[12] = 0;     // no interlace

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.Write(length);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed.AsSpan(4));
        target.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        target.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
