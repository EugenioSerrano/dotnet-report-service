using System.Buffers.Binary;
using System.Globalization;

namespace ReportService.Pdf.Assembly;

/// <summary>Whether a stamp fits the reserved area, with the measurements it was decided on.</summary>
/// <param name="Fits">Whether the stamp's box fits the area.</param>
/// <param name="WidthPt">The measured width of the stamp, in points.</param>
/// <param name="HeightPt">The measured height of the stamp, in points.</param>
/// <param name="AreaWidthPt">The area's width, in points.</param>
/// <param name="AreaHeightPt">The area's height, in points.</param>
/// <param name="Reason">The reason a stamp that does not fit carries, or <see langword="null"/>.</param>
public sealed record NumberingFitResult(
    bool Fits,
    double WidthPt,
    double HeightPt,
    double AreaWidthPt,
    double AreaHeightPt,
    string? Reason);

/// <summary>
/// Measures the stamp at the full width of a document's page count against the
/// reserved area at the declared font and size. The measurement reads the advance
/// widths of the packaged font file itself — the same bytes the stamp is drawn
/// with — so a wording that would overflow the area at the largest page count this
/// service admits is refused before a document is ever printed.
/// </summary>
public static class NumberingFit
{
    /// <summary>The page count whose digits a stamp must fit: the document ceiling.</summary>
    public const int MaximumPageCount = 10_000;

    /// <summary>Measures the widest stamp of the contract against its area.</summary>
    /// <param name="numbering">The contract to measure.</param>
    /// <returns>The measurement and whether it fits.</returns>
    public static NumberingFitResult Measure(PageNumbering numbering)
    {
        ArgumentNullException.ThrowIfNull(numbering);
        return Measure(numbering, numbering.TextFor(MaximumPageCount, MaximumPageCount));
    }

    /// <summary>Measures one stamp text against the contract's area and font.</summary>
    /// <param name="numbering">The contract to measure against.</param>
    /// <param name="text">The stamp to measure.</param>
    /// <returns>The measurement and whether it fits.</returns>
    public static NumberingFitResult Measure(PageNumbering numbering, string text)
    {
        ArgumentNullException.ThrowIfNull(numbering);
        ArgumentNullException.ThrowIfNull(text);

        var areaWidth = numbering.Area.WidthPt;
        var areaHeight = numbering.Area.HeightPt;
        TrueTypeMetrics metrics;
        try
        {
            metrics = TrueTypeMetrics.Read(numbering.Font.Bytes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new NumberingFitResult(false, 0d, 0d, areaWidth, areaHeight, "font-unreadable");
        }

        var width = metrics.WidthPt(text, numbering.Font.SizePt);
        var height = metrics.LineHeightPt(numbering.Font.SizePt);
        var fits = width <= areaWidth && height <= areaHeight;
        return new NumberingFitResult(fits, width, height, areaWidth, areaHeight, fits ? null : "stamp-overflows-area");
    }

    /// <summary>
    /// The advance widths and vertical metrics of one TrueType font, read from its
    /// <c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>cmap</c> and <c>hmtx</c> tables.
    /// The reading is total: an invalid font raises, and the caller turns that into
    /// the refusal a template author can act on.
    /// </summary>
    private sealed class TrueTypeMetrics
    {
        private readonly byte[] _font;
        private readonly Dictionary<uint, ushort> _glyphs;
        private readonly int _unitsPerEm;
        private readonly int _numberOfHMetrics;
        private readonly int _hmtx;
        private readonly int _ascender;
        private readonly int _descender;

        private TrueTypeMetrics(byte[] font, Dictionary<uint, ushort> glyphs, int unitsPerEm, int numberOfHMetrics, int hmtx, int ascender, int descender)
        {
            _font = font;
            _glyphs = glyphs;
            _unitsPerEm = unitsPerEm;
            _numberOfHMetrics = numberOfHMetrics;
            _hmtx = hmtx;
            _ascender = ascender;
            _descender = descender;
        }

        public static TrueTypeMetrics Read(byte[] font)
        {
            ArgumentNullException.ThrowIfNull(font);
            if (font.Length < 12)
            {
                throw new InvalidDataException("the font is shorter than its header");
            }

            var tables = ReadTableDirectory(font);
            var head = Table(tables, "head");
            var hhea = Table(tables, "hhea");
            var maxp = Table(tables, "maxp");
            var cmap = Table(tables, "cmap");
            var hmtx = Table(tables, "hmtx");

            var unitsPerEm = U16(font, head + 18);
            if (unitsPerEm == 0)
            {
                throw new InvalidDataException("the font declares no em");
            }

            var numberOfHMetrics = U16(font, hhea + 34);
            var glyphCount = U16(font, maxp + 4);
            if (numberOfHMetrics == 0 || numberOfHMetrics > glyphCount)
            {
                throw new InvalidDataException("the font's horizontal metrics are inconsistent");
            }

            return new TrueTypeMetrics(
                font,
                ReadCmap(font, cmap),
                unitsPerEm,
                numberOfHMetrics,
                hmtx,
                S16(font, hhea + 4),
                S16(font, hhea + 6));
        }

        public double WidthPt(string text, double sizePt)
        {
            var units = 0L;
            foreach (var rune in text.EnumerateRunes())
            {
                var glyph = _glyphs.TryGetValue((uint)rune.Value, out var mapped) ? mapped : (ushort)0;
                units += Advance(glyph);
            }

            return units * sizePt / _unitsPerEm;
        }

        public double LineHeightPt(double sizePt) =>
            (_ascender - _descender) * sizePt / _unitsPerEm;

        private int Advance(ushort glyph)
        {
            var index = glyph < _numberOfHMetrics ? glyph : _numberOfHMetrics - 1;
            return U16(_font, _hmtx + (4 * index));
        }

        private static Dictionary<int, int> ReadTableDirectory(byte[] font)
        {
            var count = U16(font, 4);
            var tables = new Dictionary<int, int>(count);
            var record = 12;
            for (var index = 0; index < count; index++, record += 16)
            {
                var tag = (font[record] << 24) | (font[record + 1] << 16) | (font[record + 2] << 8) | font[record + 3];
                tables[tag] = (int)U32(font, record + 8);
            }

            return tables;
        }

        private static int Table(Dictionary<int, int> tables, string tag)
        {
            var key = (tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3];
            return tables.TryGetValue(key, out var offset)
                ? offset
                : throw new InvalidDataException("the font misses the " + tag + " table");
        }

        private static Dictionary<uint, ushort> ReadCmap(byte[] font, int cmap)
        {
            var count = U16(font, cmap + 2);
            var chosen = -1;
            var format = 0;
            var record = cmap + 4;
            for (var index = 0; index < count; index++, record += 8)
            {
                var platform = U16(font, record);
                var encoding = U16(font, record + 2);
                var offset = cmap + (int)U32(font, record + 4);
                var candidate = U16(font, offset);
                if ((platform == 3 && encoding == 10 && candidate == 12)
                    || (platform == 0 && candidate == 12))
                {
                    chosen = offset;
                    format = 12;
                    break;
                }

                if (chosen < 0
                    && ((platform == 3 && encoding == 1 && candidate == 4) || (platform == 0 && candidate == 4)))
                {
                    chosen = offset;
                    format = 4;
                }
            }

            if (chosen < 0)
            {
                throw new InvalidDataException("the font has no Unicode character map");
            }

            return format == 12 ? ReadFormat12(font, chosen) : ReadFormat4(font, chosen);
        }

        private static Dictionary<uint, ushort> ReadFormat4(byte[] font, int cmap)
        {
            var segments = U16(font, cmap + 6) / 2;
            var endCodes = cmap + 14;
            var startCodes = endCodes + (2 * segments) + 2;
            var deltas = startCodes + (2 * segments);
            var rangeOffsets = deltas + (2 * segments);
            var glyphs = new Dictionary<uint, ushort>();
            for (var segment = 0; segment < segments; segment++)
            {
                var end = U16(font, endCodes + (2 * segment));
                var start = U16(font, startCodes + (2 * segment));
                var delta = S16(font, deltas + (2 * segment));
                var range = U16(font, rangeOffsets + (2 * segment));
                if (start > end)
                {
                    continue;
                }

                for (var code = (int)start; code <= end; code++)
                {
                    ushort glyph;
                    if (range == 0)
                    {
                        glyph = (ushort)((code + delta) & 0xFFFF);
                    }
                    else
                    {
                        var address = rangeOffsets + (2 * segment) + range + (2 * (code - start));
                        if (address + 2 > font.Length)
                        {
                            continue;
                        }

                        glyph = U16(font, address);
                        if (glyph != 0)
                        {
                            glyph = (ushort)((glyph + delta) & 0xFFFF);
                        }
                    }

                    if (glyph != 0)
                    {
                        glyphs[(uint)code] = glyph;
                    }
                }
            }

            return glyphs;
        }

        private static Dictionary<uint, ushort> ReadFormat12(byte[] font, int cmap)
        {
            var groups = (int)U32(font, cmap + 12);
            var glyphs = new Dictionary<uint, ushort>(groups * 8);
            var group = cmap + 16;
            for (var index = 0; index < groups; index++, group += 12)
            {
                var start = U32(font, group);
                var end = U32(font, group + 4);
                var glyph = U32(font, group + 8);
                if (start > end || end - start > 65_536)
                {
                    continue;
                }

                for (var code = (long)start; code <= end; code++)
                {
                    glyphs[(uint)code] = (ushort)glyph++;
                }
            }

            return glyphs;
        }

        private static ushort U16(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

        private static short S16(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2));

        private static uint U32(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    }

    /// <summary>The invariant text of a measured value, for a diagnostic that carries no data.</summary>
    /// <param name="value">The value.</param>
    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
