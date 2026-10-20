using System.Buffers.Binary;
using System.IO.Compression;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The icon renderer writes a PNG by hand, so "it produced bytes" proves nothing - a decoder has
/// to get the pixels back out. These tests walk the chunks, verify every CRC, inflate the IDAT and
/// read individual pixels, which is the only way to catch a stream that LabVIEW would silently
/// refuse. No imaging dependency here either, for the same reason there is none in the server.
/// </summary>
public sealed class IconImageTests
{
    private const int Size = IconImage.Size;

    /// <summary>A decoded image plus what the chunk walk found on the way.</summary>
    private sealed record Decoded(IconImage.Rgb[] Pixels, List<string> ChunkTypes, int Width, int Height);

    /// <summary>
    /// Decode an 8-bit truecolour PNG with filter 0 on every scanline - the only kind
    /// <see cref="IconImage"/> writes. Throws on a bad signature, a bad CRC or an unexpected
    /// header, so a malformed stream fails the test that produced it.
    /// </summary>
    private static Decoded Decode(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan(0, 8).SequenceEqual(signature), "PNG signature is wrong");

        var types = new List<string>();
        var idat = new MemoryStream();
        int width = 0, height = 0;
        var at = 8;

        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);

            var declared = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length, 4));
            Assert.Equal(Crc32(png.AsSpan(at + 4, 4 + length)), declared);

            types.Add(type);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                    Assert.Equal(8, data[8]);       // bit depth
                    Assert.Equal(2, data[9]);       // truecolour
                    Assert.Equal(0, data[10]);      // deflate
                    Assert.Equal(0, data[11]);      // filter method 0
                    Assert.Equal(0, data[12]);      // no interlace
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
            }

            at += 12 + length;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], types);

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            zlib.CopyTo(inflated);

        var raw = inflated.ToArray();
        Assert.Equal(height * (1 + width * 3), raw.Length);

        var pixels = new IconImage.Rgb[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            Assert.Equal(0, raw[row]);              // filter: none, on every scanline
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 3;
                pixels[y * width + x] = new IconImage.Rgb(raw[p], raw[p + 1], raw[p + 2]);
            }
        }

        return new Decoded(pixels, types, width, height);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly IconImage.Rgb Blue = new(0x00, 0x5A, 0x9C);
    private static readonly IconImage.Rgb White = new(0xFF, 0xFF, 0xFF);
    private static readonly IconImage.Rgb Black = new(0x00, 0x00, 0x00);
    private static readonly IconImage.Rgb Green = new(0x00, 0xFF, 0x00);

    private static byte[] Render(string? l1 = "DAQ", string? l2 = null, string? l3 = null,
                                 string? l4 = null) =>
        IconImage.Render(l1, l2, l3, Blue, White, Black,
                         IconImage.ReadableOn(Blue), IconImage.ReadableOn(White), l4);

    private static IconImage.Rgb At(Decoded d, int x, int y) => d.Pixels[y * d.Width + x];

    /// <summary>The image as rows of '#' black, '.' white, 'g' green and '?' anything else.</summary>
    private static string[] Picture(Decoded d) =>
        [.. Enumerable.Range(0, d.Height).Select(y => new string(Enumerable.Range(0, d.Width)
            .Select(x => At(d, x, y) switch
            {
                var p when p == Black => '#',
                var p when p == White => '.',
                var p when p == Green => 'g',
                _ => '?',
            }).ToArray()))];

    [Fact]
    public void Writes_a_decodable_32x32_png()
    {
        var decoded = Decode(Render());

        Assert.Equal(32, decoded.Width);
        Assert.Equal(32, decoded.Height);
        Assert.Equal(Size * Size, decoded.Pixels.Length);
    }

    /// <summary>
    /// A hand-made icon, pixel for pixel: the style of a LabVIEW toolkit whose icons were drawn in
    /// LabVIEW's icon editor - green banner, "MCP" in black, two body lines. The picture is what
    /// that toolkit's own generator (New-VIIcon.ps1 -Lines TOOL,CALL) drew through GDI's Small
    /// Fonts, and that generator was measured 2026-10-20 to reproduce six of the hand-made icons
    /// exactly. The renderer here has no GDI and must agree on every one of the 1 024 pixels.
    /// </summary>
    [Fact]
    public void Reproduces_a_hand_made_icon_pixel_for_pixel()
    {
        string[] expected =
        [
            "################################",
            "#gggggggggggggggggggggggggggggg#",
            "#gggggggggggggggggggggggggggggg#",
            "#ggggggg#ggg#gg##gg###ggggggggg#",
            "#ggggggg##g##g#gg#g#gg#gggggggg#",
            "#ggggggg#g#g#g#gggg###ggggggggg#",
            "#ggggggg#ggg#g#gg#g#ggggggggggg#",
            "#ggggggg#ggg#gg##gg#ggggggggggg#",
            "#gggggggggggggggggggggggggggggg#",
            "#gggggggggggggggggggggggggggggg#",
            "#gggggggggggggggggggggggggggggg#",
            "################################",
            "#..............................#",
            "#..............................#",
            "#..............................#",
            "#......###..##...##..#.........#",
            "#.......#..#..#.#..#.#.........#",
            "#.......#..#..#.#..#.#.........#",
            "#.......#..#..#.#..#.#.........#",
            "#.......#...##...##..###.......#",
            "#..............................#",
            "#..............................#",
            "#..............................#",
            "#.......##...##..#...#.........#",
            "#......#..#..##..#...#.........#",
            "#......#....#..#.#...#.........#",
            "#......#..#.####.#...#.........#",
            "#.......##..#..#.###.###.......#",
            "#..............................#",
            "#..............................#",
            "#..............................#",
            "################################",
        ];

        var drawn = Picture(Decode(IconImage.Render("MCP", "TOOL", "CALL",
            Green, White, Black, bannerText: Black, bodyText: Black)));

        Assert.Equal(expected, drawn);
    }

    /// <summary>Rows of <paramref name="d"/> holding any pixel of <paramref name="colour"/>
    /// inside the frame.</summary>
    private static List<int> RowsWith(Decoded d, IconImage.Rgb colour, int from, int to) =>
        [.. Enumerable.Range(from, to - from + 1).Where(y => CountIn(d, colour, y, y) > 0)];

    [Theory]
    [InlineData(1, new[] { 19 })]
    [InlineData(2, new[] { 15, 23 })]
    [InlineData(3, new[] { 13, 19, 25 })]
    public void Places_body_lines_where_the_icon_editor_does(int count, int[] capTops)
    {
        // Capitals only, so a line's first inked row is its cap top and it is five rows tall.
        var lines = Enumerable.Range(0, 3).Select(i => i < count ? "HEH" : null).ToArray();
        var decoded = Decode(Render("H", lines[0], lines[1], lines[2]));

        var expected = capTops.SelectMany(top => Enumerable.Range(top, 5)).ToList();
        Assert.Equal(expected, RowsWith(decoded, Black, 12, 30));
        Assert.Equal([3, 4, 5, 6, 7], RowsWith(decoded, White, 1, 10));   // banner caps
    }

    [Fact]
    public void Frames_the_icon_and_fills_banner_separator_and_body()
    {
        var decoded = Decode(Render(l1: null));   // no text: only the fills are under test

        Assert.Equal(Black, At(decoded, 0, 0));            // corners are border
        Assert.Equal(Black, At(decoded, 31, 31));
        Assert.Equal(Black, At(decoded, 0, 16));           // left edge
        Assert.Equal(Black, At(decoded, 31, 16));          // right edge

        Assert.Equal(Blue, At(decoded, 16, 5));            // inside the banner
        Assert.Equal(Blue, At(decoded, 1, 10));            // banner reaches row 10
        for (var x = 0; x < Size; x++)
            Assert.Equal(Black, At(decoded, x, 11));       // the separator, frame to frame
        Assert.Equal(White, At(decoded, 1, 12));           // body from row 12
        Assert.Equal(White, At(decoded, 16, 30));
    }

    [Fact]
    public void Draws_banner_text_in_the_readable_colour()
    {
        var withText = Decode(Render(l1: "DAQ"));
        var without = Decode(Render(l1: null));

        // On NI blue the readable choice is white, so banner text must appear as white pixels
        // inside the banner - where the empty render has none.
        var lit = CountIn(withText, White, 1, 10);
        Assert.True(lit > 20, $"expected banner glyph pixels, found {lit}");
        Assert.Equal(0, CountIn(without, White, 1, 10));
        Assert.Equal(White, IconImage.ReadableOn(Blue));
        Assert.Equal(Black, IconImage.ReadableOn(White));
    }

    [Fact]
    public void Skips_an_empty_body_line_and_spaces_the_rest_for_their_number()
    {
        // line2 empty, line3 given: one body line, so it sits where one line sits.
        var gap = Decode(Render(l1: "DAQ", l2: null, l3: "HEH"));
        var one = Decode(Render(l1: "DAQ", l2: "HEH"));

        Assert.Equal(Picture(one), Picture(gap));
        Assert.Equal(19, RowsWith(gap, Black, 12, 30)[0]);
    }

    [Fact]
    public void line4_is_the_third_body_line()
    {
        var three = Decode(Render(l1: "DAQ", l2: "READ", l3: "AI", l4: "TDMS"));

        Assert.True(CountIn(three, Black, 13, 17) > 10, "line2 did not draw");
        Assert.True(CountIn(three, Black, 19, 23) > 5, "line3 did not draw");
        Assert.True(CountIn(three, Black, 25, 29) > 10, "line4 did not draw");
    }

    /// <summary>Count pixels of one colour in rows <paramref name="from"/>..<paramref name="to"/>,
    /// excluding the 1 px border columns so the frame never counts.</summary>
    private static int CountIn(Decoded d, IconImage.Rgb colour, int from, int to)
    {
        var n = 0;
        for (var y = from; y <= to; y++)
            for (var x = 1; x < d.Width - 1; x++)
                if (At(d, x, y) == colour) n++;
        return n;
    }

    /// <summary>Columns of <paramref name="d"/> holding black in rows from..to, inside the frame.</summary>
    private static List<int> ColumnsWithBlack(Decoded d, int from, int to) =>
        [.. Enumerable.Range(1, Size - 2).Where(x => Enumerable.Range(from, to - from + 1)
            .Any(y => At(d, x, y) == Black))];

    [Theory]
    [InlineData("I", 15)]          // 1 px of ink: floor(31 / 2)
    [InlineData("TOOL", 7)]        // 17 px: floor(15 / 2), as in the hand-made icon
    [InlineData("MM", 10)]         // 11 px: floor(21 / 2)
    public void Centres_a_line_on_its_ink(string text, int left)
    {
        var columns = ColumnsWithBlack(Decode(Render(l1: null, l2: text)), 12, 30);

        Assert.Equal(left, columns[0]);
        Assert.Equal(IconImage.InkWidth(text), columns[^1] - columns[0] + 1);
    }

    [Theory]
    [InlineData("I", 1)]
    [InlineData("M", 5)]
    [InlineData("W", 5)]
    [InlineData("MCP", 15)]        // the hand-made banner text
    [InlineData("TOOL", 17)]
    [InlineData("LOGGER", 27)]
    [InlineData("SAMPLE", 28)]     // exactly the limit
    [InlineData("CHANNEL", 32)]    // over it
    [InlineData("   ", 0)]
    [InlineData("", 0)]
    public void Measures_ink_width_in_pixels(string text, int width) =>
        Assert.Equal(width, IconImage.InkWidth(text));

    /// <summary>
    /// A few glyphs pinned row by row, cap top first, so a regenerated table that drew something
    /// else fails here and not only in the golden icon. Rows 2..7 of the font's 8-row cell, each
    /// as wide as the advance: the first column is the 1 px spacing, which is why I is ".#".
    /// </summary>
    [Theory]
    [InlineData('I', ".#|.#|.#|.#|.#|..")]
    [InlineData('M', ".#...#|.##.##|.#.#.#|.#...#|.#...#|......")]
    [InlineData('A', "..##.|..##.|.#..#|.####|.#..#|.....")]
    [InlineData('g', "....|....|..##|.#.#|..##|...#")]      // the one descender row
    [InlineData('3', ".##.|...#|..#.|...#|.##.|....")]
    [InlineData('-', "...|...|.##|...|...|...")]
    [InlineData(' ', ".|.|.|.|.|.")]
    public void Pins_the_shape_of_individual_glyphs(char c, string rows) =>
        Assert.Equal(rows, IconFont.Glyphs[c]);

    [Fact]
    public void Every_glyph_is_six_rows_of_one_width()
    {
        foreach (var (c, glyph) in IconFont.Glyphs)
        {
            var rows = glyph.Split('|');
            Assert.True(rows.Length == IconFont.Rows, $"'{c}' has {rows.Length} rows");
            Assert.True(rows.All(r => r.Length == rows[0].Length && r.All(p => p is '#' or '.')),
                        $"'{c}' has ragged or foreign rows: {glyph}");
        }
    }

    [Fact]
    public void Covers_printable_ascii_including_the_old_alphabet()
    {
        for (var c = ' '; c <= '~'; c++)
            Assert.True(IconImage.Supports(c), $"'{c}' is missing");
        Assert.Equal("", IconImage.Unsupported("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -./:+#"));
        Assert.Equal(95, IconFont.Glyphs.Count);
    }

    [Fact]
    public void Draws_lowercase_as_lowercase()
    {
        var lower = Decode(Render(l1: null, l2: "daq"));
        var upper = Decode(Render(l1: null, l2: "DAQ"));

        Assert.NotEqual(Picture(upper), Picture(lower));
        var columns = ColumnsWithBlack(lower, 12, 30);
        Assert.Equal(IconImage.InkWidth("daq"), columns[^1] - columns[0] + 1);
        Assert.True(IconImage.InkWidth("daq") < IconImage.InkWidth("DAQ"), "lowercase is narrower");
    }

    [Fact]
    public void Keeps_descenders_inside_their_line_and_the_frame()
    {
        // Three lines with descenders: each line owns six rows, cap top to descender, so they
        // touch and never overlap, and the last descender is row 30 - inside the frame.
        var decoded = Decode(Render(l1: "Hgjpqy", l2: "Hgjpqy", l3: "Hgjpqy", l4: "Hgjpqy"));

        Assert.Equal(Enumerable.Range(13, 18).ToList(), RowsWith(decoded, Black, 12, 30));
        Assert.Equal(Black, At(decoded, 16, 31));                          // the frame
        Assert.Equal([3, 4, 5, 6, 7, 8], RowsWith(decoded, White, 1, 10));  // banner, the same
    }

    [Theory]
    [InlineData("ACQUISITION")]
    [InlineData("WWWWWWWWWW")]
    [InlineData("@@@@@@@@")]
    public void Cuts_a_line_that_cannot_fit_and_keeps_a_pixel_of_air(string text)
    {
        var fitted = IconImage.Fit(text);
        Assert.True(fitted.Length < text.Length, "nothing was cut");
        Assert.True(IconImage.InkWidth(fitted) <= IconImage.MaxLineWidth);
        Assert.True(IconImage.InkWidth(text[..(fitted.Length + 1)]) > IconImage.MaxLineWidth,
                    "more could have been kept");

        var decoded = Decode(Render(l1: null, l2: text));
        for (var y = 12; y <= 30; y++)
        {
            Assert.Equal(White, At(decoded, 1, y));                // air beside the frame
            Assert.Equal(White, At(decoded, Size - 2, y));
            Assert.Equal(Black, At(decoded, 0, y));
            Assert.Equal(Black, At(decoded, Size - 1, y));
        }
    }

    [Fact]
    public void Fit_leaves_a_line_that_fits_alone_and_drops_a_trailing_space()
    {
        Assert.Equal("SAMPLE", IconImage.Fit("  SAMPLE "));
        Assert.Equal("SAMPLE", IconImage.Fit("SAMPLE X"));         // the cut lands on the space
        Assert.Equal("AB CDEF", IconImage.Fit("AB CDEFGHIJ"));     // characters, not words
    }

    [Theory]
    [InlineData("005A9C", 0x00, 0x5A, 0x9C)]
    [InlineData("#FF8000", 0xFF, 0x80, 0x00)]
    [InlineData("  ffffff  ", 0xFF, 0xFF, 0xFF)]
    public void Parses_both_colour_spellings(string text, int r, int g, int b)
    {
        var parsed = IconImage.ParseColor(text);

        Assert.NotNull(parsed);
        Assert.Equal(new IconImage.Rgb((byte)r, (byte)g, (byte)b), parsed!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("0x005A9C")]
    [InlineData("blue")]
    [InlineData("GGHHII")]
    public void Rejects_a_colour_it_cannot_read(string text) =>
        Assert.Null(IconImage.ParseColor(text));

    [Fact]
    public void Reports_characters_the_font_cannot_draw()
    {
        Assert.Equal("", IconImage.Unsupported("DAQ 3AI-1.0/2:+#"));
        Assert.Equal("", IconImage.Unsupported("daq_x"));       // lowercase and _ are ASCII
        Assert.Equal("ÄÖ", IconImage.Unsupported("ÄÖÄ"));       // de-duplicated
        Assert.Equal("€", IconImage.Unsupported("A€B"));
    }

    [Fact]
    public void Leaves_an_undrawable_character_out_rather_than_leaving_a_gap()
    {
        Assert.Equal(IconImage.InkWidth("AB"), IconImage.InkWidth("A€B"));
        Assert.Equal(Picture(Decode(Render(l1: null, l2: "AB"))),
                     Picture(Decode(Render(l1: null, l2: "A€B"))));
    }

    [Theory]
    [InlineData(0x00, 0x5A, 0x9C, 0x00, 0x66, 0x99)]   // NI blue -> its web-safe neighbour
    [InlineData(0x00, 0x66, 0x99, 0x00, 0x66, 0x99)]   // already safe: unchanged
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)]
    [InlineData(0x7F, 0x7F, 0x7F, 0x66, 0x66, 0x66)]   // 127 is nearer 102 than 153
    [InlineData(0xC0, 0x39, 0x2B, 0xCC, 0x33, 0x33)]
    public void Quantises_the_way_labview_does(int r, int g, int b, int qr, int qg, int qb)
    {
        var quantised = IconImage.Quantise(new IconImage.Rgb((byte)r, (byte)g, (byte)b));

        Assert.Equal(new IconImage.Rgb((byte)qr, (byte)qg, (byte)qb), quantised);
        Assert.True(IconImage.IsWebSafe(quantised), "quantising must land inside the cube");
    }

    [Fact]
    public void Recognises_which_colours_survive_a_round_trip()
    {
        Assert.True(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x66, 0x99)));
        Assert.True(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x00, 0x00)));
        Assert.False(IconImage.IsWebSafe(new IconImage.Rgb(0x00, 0x5A, 0x9C)));
        Assert.False(IconImage.IsWebSafe(new IconImage.Rgb(0x01, 0x00, 0x00)));
    }

    [Fact]
    public void Renders_every_drawable_character_without_leaving_the_frame()
    {
        // Every glyph, four at a time - four of the widest (@, 7 px) still fit - on every line.
        var all = new string([.. IconFont.Glyphs.Keys]);
        for (var i = 0; i < all.Length; i += 4)
        {
            var chunk = all.Substring(i, Math.Min(4, all.Length - i));
            Assert.True(IconImage.Fits(chunk), $"'{chunk}' does not fit");
            var decoded = Decode(Render(l1: chunk, l2: chunk, l3: chunk, l4: chunk));
            for (var k = 0; k < Size; k++)
            {
                Assert.Equal(Black, At(decoded, k, 0));
                Assert.Equal(Black, At(decoded, k, Size - 1));
                Assert.Equal(Black, At(decoded, 0, k));
                Assert.Equal(Black, At(decoded, Size - 1, k));
            }
        }
    }
}
