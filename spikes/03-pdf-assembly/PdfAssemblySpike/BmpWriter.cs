using System.Globalization;

namespace PdfAssemblySpike;

/// <summary>
/// Writes a small uncompressed 24-bit Windows bitmap used as the logo of one
/// part: two colours, diagonal bands and the part number in a 5 x 7 pixel font,
/// all derived from the part's own random stream.
/// </summary>
internal static class BmpWriter
{
    public const int Size = 64;

    // 5 x 7 digits, one byte per row; the five low bits are the pixels (bit 4 = left).
    private static readonly byte[][] Digits =
    [
        [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
        [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
    ];

    public static byte[] CreateLogo(int partOrder, SplitMix64 random, int bitsPerPixel = 24)
    {
        var background = RandomColour(random);
        var band = RandomColour(random);
        int bandWidth = random.NextInt(4, 11);
        var pixels = new (byte R, byte G, byte B)[Size, Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                pixels[y, x] = (x + y) / bandWidth % 2 == 0 ? background : band;
            }
        }

        // Part number, four digits, two pixels per font pixel, black on a white box.
        string text = (partOrder % 10000).ToString("D4", CultureInfo.InvariantCulture);
        const int scale = 2;
        int left = (Size - (text.Length * 6 * scale)) / 2;
        int top = (Size - (7 * scale)) / 2;
        for (int y = top - 2; y < top + (7 * scale) + 2; y++)
        {
            for (int x = left - 2; x < left + (text.Length * 6 * scale); x++)
            {
                pixels[y, x] = (255, 255, 255);
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            byte[] glyph = Digits[text[i] - '0'];
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if ((glyph[row] & (0x10 >> col)) == 0)
                    {
                        continue;
                    }

                    for (int dy = 0; dy < scale; dy++)
                    {
                        for (int dx = 0; dx < scale; dx++)
                        {
                            pixels[top + (row * scale) + dy, left + (((i * 6) + col) * scale) + dx] = (0, 0, 0);
                        }
                    }
                }
            }
        }

        return Encode(pixels, bitsPerPixel);
    }

    private static (byte R, byte G, byte B) RandomColour(SplitMix64 random) =>
        ((byte)random.NextInt(0, 256), (byte)random.NextInt(0, 256), (byte)random.NextInt(0, 256));

    private static byte[] Encode((byte R, byte G, byte B)[,] pixels, int bitsPerPixel)
    {
        if (bitsPerPixel is not (24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerPixel));
        }

        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);
        int bytesPerPixel = bitsPerPixel / 8;
        int rowSize = ((width * bytesPerPixel) + 3) / 4 * 4;
        int imageSize = rowSize * height;
        const int headerSize = 14 + 40;
        byte[] file = new byte[headerSize + imageSize];
        using var stream = new MemoryStream(file);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(file.Length);
        writer.Write(0);
        writer.Write(headerSize);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)bitsPerPixel);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                writer.Write(pixels[y, x].B);
                writer.Write(pixels[y, x].G);
                writer.Write(pixels[y, x].R);
                if (bytesPerPixel == 4)
                {
                    writer.Write((byte)255);
                }
            }

            for (int pad = width * bytesPerPixel; pad < rowSize; pad++)
            {
                writer.Write((byte)0);
            }
        }

        return file;
    }
}
