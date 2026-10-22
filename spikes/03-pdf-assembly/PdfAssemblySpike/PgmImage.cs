using System.Globalization;
using System.Text;

namespace PdfAssemblySpike;

/// <summary>Result of comparing two greyscale pages pixel by pixel.</summary>
internal sealed record PixelDiff(bool SameSize, long ChangedInside, long ChangedOutside, int BandTopRow);

/// <summary>
/// A binary greyscale image (PGM, "P5", maximum value 255) as written by the
/// renderer, and a strict pixel-by-pixel comparison that splits the changes
/// between the footer band and the rest of the page.
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

    /// <summary>
    /// Compares two pages. The band is the bottom bandHeight / pageHeight share of
    /// the image, rounded outward by one pixel.
    /// </summary>
    public static PixelDiff Compare(PgmImage reference, PgmImage candidate, double pageHeightPoints, double bandHeightPoints)
    {
        int bandTop = Math.Max(0, (int)Math.Floor((pageHeightPoints - bandHeightPoints) / pageHeightPoints * reference.Height) - 1);
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            return new PixelDiff(false, 0, 0, bandTop);
        }

        long inside = 0;
        long outside = 0;
        int width = reference.Width;
        for (int i = 0; i < reference.Pixels.Length; i++)
        {
            if (reference.Pixels[i] != candidate.Pixels[i])
            {
                if (i / width >= bandTop)
                {
                    inside++;
                }
                else
                {
                    outside++;
                }
            }
        }

        return new PixelDiff(true, inside, outside, bandTop);
    }

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
