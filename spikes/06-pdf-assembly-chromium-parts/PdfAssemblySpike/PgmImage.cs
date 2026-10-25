using System.Globalization;
using System.Text;

namespace PdfAssemblySpike;

/// <summary>Result of comparing two greyscale pages pixel by pixel.</summary>
internal sealed record PixelDiff(bool SameSize, long ChangedInside, long ChangedOutside, PixelRect Area);

/// <summary>A rectangle of pixels, both edges included on each axis.</summary>
internal sealed record PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>
    /// The numbering area at 72 dpi (one pixel per point): rounded outward to whole
    /// pixels, then widened by one pixel on each side.
    /// </summary>
    public static PixelRect NumberingArea { get; } = new(
        (int)Math.Floor(PageLayout.NumberingAreaLeft) - 1,
        (int)Math.Floor(PageLayout.NumberingAreaTop) - 1,
        (int)Math.Ceiling(PageLayout.NumberingAreaRight) + 1,
        (int)Math.Ceiling(PageLayout.NumberingAreaBottom) + 1);

    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"x {Left}-{Right} px, y {Top}-{Bottom} px");
}

/// <summary>
/// A binary greyscale image (PGM, "P5", maximum value 255) as written by the
/// renderer, and a strict pixel-by-pixel comparison that splits the changes
/// between the numbering area and the rest of the page.
/// </summary>
internal sealed class PgmImage
{
    private PgmImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public static PgmImage Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int position = 0;
        string magic = Token(data, ref position);
        if (magic != "P5")
        {
            throw new InvalidDataException($"{path}: not a binary PGM ({magic}).");
        }

        int width = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        int height = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        int maxValue = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        if (maxValue != 255)
        {
            throw new InvalidDataException($"{path}: maximum value {maxValue}, expected 255.");
        }

        position++; // the single whitespace byte after the header
        int length = width * height;
        if (data.Length - position < length)
        {
            throw new InvalidDataException($"{path}: truncated pixel data.");
        }

        return new PgmImage(width, height, data.AsSpan(position, length).ToArray());
    }

    /// <summary>Compares two pages, splitting the changed pixels between the area and the rest of the page.</summary>
    public static PixelDiff Compare(PgmImage reference, PgmImage candidate, PixelRect area)
    {
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            return new PixelDiff(false, 0, 0, area);
        }

        long inside = 0;
        long outside = 0;
        int width = reference.Width;
        for (int i = 0; i < reference.Pixels.Length; i++)
        {
            if (reference.Pixels[i] != candidate.Pixels[i])
            {
                if (area.Contains(i % width, i / width))
                {
                    inside++;
                }
                else
                {
                    outside++;
                }
            }
        }

        return new PixelDiff(true, inside, outside, area);
    }

    /// <summary>Counts the pixels of a rectangle that are not exactly white (255).</summary>
    public long NotWhite(PixelRect area)
    {
        long count = 0;
        for (int y = Math.Max(0, area.Top); y <= Math.Min(Height - 1, area.Bottom); y++)
        {
            for (int x = Math.Max(0, area.Left); x <= Math.Min(Width - 1, area.Right); x++)
            {
                if (Pixels[(y * Width) + x] != 255)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>True when both images have the same size and every pixel is equal.</summary>
    public static bool Identical(PgmImage a, PgmImage b) =>
        a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    /// <summary>Counts pixels darker than white inside a rectangle (used to check a drawn image).</summary>
    public long NonWhite(int left, int top, int width, int height)
    {
        long count = 0;
        for (int y = Math.Max(0, top); y < Math.Min(Height, top + height); y++)
        {
            for (int x = Math.Max(0, left); x < Math.Min(Width, left + width); x++)
            {
                if (Pixels[(y * Width) + x] < 250)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static string Token(byte[] data, ref int position)
    {
        while (position < data.Length)
        {
            if (data[position] == '#')
            {
                while (position < data.Length && data[position] != '\n')
                {
                    position++;
                }
            }
            else if (char.IsWhiteSpace((char)data[position]))
            {
                position++;
            }
            else
            {
                break;
            }
        }

        var token = new StringBuilder();
        while (position < data.Length && !char.IsWhiteSpace((char)data[position]))
        {
            token.Append((char)data[position]);
            position++;
        }

        return token.ToString();
    }
}
