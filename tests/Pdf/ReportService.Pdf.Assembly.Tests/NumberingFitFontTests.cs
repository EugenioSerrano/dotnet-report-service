using System.Buffers.Binary;
using System.Text;
using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The measured fit against fonts the test builds itself: a format-12 font with an
/// astral character, a format-4 font with a glyph range, a font with no Unicode map,
/// no tables, no em and no horizontal metrics, and a character the font does not map.
/// The reading is total: every unusable font is the fit's own refusal, never a crash.
/// </summary>
public sealed class NumberingFitFontTests
{
    private static PageNumbering Contract(byte[] font) => new(
        new NumberingArea(0, 0, 200, 50),
        new NumberingFont("synthetic", "fonts/synthetic.ttf", 10, font),
        "{X} of {Y}",
        "en-US");

    [Fact]
    public void AFormat12FontMeasuresItsAstralAndBmpCharacters()
    {
        var font = Font(format12: true);

        var fit = NumberingFit.Measure(Contract(font), "A\u00A0"); // U+00A0 is unmapped; A maps to glyph 1

        Assert.True(fit.Fits);
        Assert.True(fit.WidthPt > 0);
        Assert.True(fit.HeightPt > 0);
    }

    [Fact]
    public void AFormat4FontWithARangeOffsetMeasuresItsGlyphs()
    {
        var font = Font(format12: false);

        var fit = NumberingFit.Measure(Contract(font), "A B!");

        Assert.True(fit.Fits);
        Assert.True(fit.WidthPt > 0);
    }

    [Fact]
    public void AFontWithoutAUnicodeMapIsRefused()
    {
        var fit = NumberingFit.Measure(Contract(Font(format12: false, unicode: false)), "A");

        Assert.False(fit.Fits);
        Assert.Equal("font-unreadable", fit.Reason);
    }

    [Fact]
    public void AFontWithNoTablesIsRefused()
    {
        var fit = NumberingFit.Measure(Contract(Font(format12: false, tables: false)), "A");

        Assert.False(fit.Fits);
        Assert.Equal("font-unreadable", fit.Reason);
    }

    [Fact]
    public void AFontWithNoEmIsRefused()
    {
        var fit = NumberingFit.Measure(Contract(Font(format12: false, unitsPerEm: 0)), "A");

        Assert.False(fit.Fits);
        Assert.Equal("font-unreadable", fit.Reason);
    }

    [Fact]
    public void AFontWithNoHorizontalMetricsIsRefused()
    {
        var fit = NumberingFit.Measure(Contract(Font(format12: false, horizontalMetrics: 0)), "A");

        Assert.False(fit.Fits);
        Assert.Equal("font-unreadable", fit.Reason);
    }

    [Fact]
    public void AnUnmappedCharacterUsesTheMissingGlyph()
    {
        var fit = NumberingFit.Measure(Contract(Font(format12: false)), "Z");

        Assert.True(fit.Fits);
        Assert.True(fit.WidthPt > 0);
    }

    [Fact]
    public void AnAreaWithBothDimensionsPositiveIsPositive()
    {
        Assert.True(new NumberingArea(0, 0, 1, 1).IsPositive);
        Assert.False(new NumberingArea(0, 0, 0, 1).IsPositive);
        Assert.False(new NumberingArea(0, 0, 1, 0).IsPositive);
    }

    private static byte[] Font(
        bool format12,
        bool unicode = true,
        int unitsPerEm = 1_000,
        int horizontalMetrics = 2,
        bool tables = true)
    {
        var head = new byte[54];
        U16(head, 18, unitsPerEm);
        var hhea = new byte[36];
        S16(hhea, 4, 800);
        S16(hhea, 6, -200);
        U16(hhea, 34, horizontalMetrics);
        var maxp = new byte[32];
        U16(maxp, 4, 12);
        var hmtx = new byte[8];
        U16(hmtx, 0, 500);
        U16(hmtx, 4, 200);
        var cmap = Cmap(format12, unicode);

        var entries = tables
            ? new (string Tag, byte[] Data)[] { ("head", head), ("hhea", hhea), ("maxp", maxp), ("hmtx", hmtx), ("cmap", cmap) }
            : [];
        var directory = 12 + (16 * entries.Length);
        var total = directory + entries.Sum(entry => entry.Data.Length);
        var font = new byte[total];
        U16(font, 4, entries.Length);
        var record = 12;
        var data = directory;
        foreach (var (tag, bytes) in entries)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(font, record);
            U32(font, record + 8, (uint)data);
            U32(font, record + 12, (uint)bytes.Length);
            bytes.CopyTo(font, data);
            record += 16;
            data += bytes.Length;
        }

        return font;
    }

    private static byte[] Cmap(bool format12, bool unicode)
    {
        if (!unicode)
        {
            // One record for a Macintosh encoding with a format 6 subtable: no Unicode
            // character map exists, and the fit refuses the font.
            var unsupported = new byte[12 + 10];
            U16(unsupported, 2, 1);
            U16(unsupported, 4, 1);
            U16(unsupported, 6, 0);
            U32(unsupported, 8, 12);
            U16(unsupported, 12, 6);
            return unsupported;
        }

        return format12 ? Cmap12() : Cmap4();
    }

    private static byte[] Cmap4()
    {
        var table = new byte[12 + 52];
        U16(table, 2, 1);
        U16(table, 4, 3);
        U16(table, 6, 1);
        U32(table, 8, 12);

        var f = 12;
        var segments = 4;
        U16(table, f, 4);
        U16(table, f + 6, (ushort)(segments * 2));
        var endCodes = f + 14;
        var startCodes = endCodes + (segments * 2) + 2;
        var deltas = startCodes + (segments * 2);
        var ranges = deltas + (segments * 2);
        var glyphs = ranges + (segments * 2);

        U16(table, endCodes + 0, 66);
        U16(table, endCodes + 2, 33);
        U16(table, endCodes + 4, 0xFFFF);
        U16(table, endCodes + 6, 100);

        U16(table, startCodes + 0, 65);
        U16(table, startCodes + 2, 32);
        U16(table, startCodes + 4, 0xFFFF);
        U16(table, startCodes + 6, 200);

        S16(table, deltas + 0, -64);
        S16(table, deltas + 2, 0);
        S16(table, deltas + 4, 1);
        S16(table, deltas + 6, 0);

        U16(table, ranges + 0, 0);
        U16(table, ranges + 2, 6);
        U16(table, ranges + 4, 0);
        U16(table, ranges + 6, 0);

        U16(table, glyphs + 0, 3);
        U16(table, glyphs + 2, 0);
        return table;
    }

    private static byte[] Cmap12()
    {
        var table = new byte[12 + 16 + (12 * 4)];
        U16(table, 2, 1);
        U16(table, 4, 3);
        U16(table, 6, 10);
        U32(table, 8, 12);

        var f = 12;
        U16(table, f, 12);
        U32(table, f + 12, 4);
        var group = f + 16;
        Group(table, group, 65, 67, 1);
        Group(table, group + 12, 0x1F600, 0x1F601, 10);
        Group(table, group + 24, 100, 50, 1);
        Group(table, group + 36, 0, 100_000, 1);
        return table;
    }

    private static void Group(byte[] bytes, int offset, uint start, uint end, uint glyph)
    {
        U32(bytes, offset, start);
        U32(bytes, offset + 4, end);
        U32(bytes, offset + 8, glyph);
    }

    private static void U16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), (ushort)value);

    private static void S16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset, 2), (short)value);

    private static void U32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
}
