using System.Text;
using ReportService.Data;
using ReportService.Testing;

namespace ReportService.Data.Tests;

/// <summary>
/// The classifier of an image data URI, held to the shared conformance cases of the definition:
/// every case is read from the value as it is written in JSON, escapes included, and must be
/// classified as the case says.
/// </summary>
public sealed class JsonImageDataUriTests
{
    public static TheoryData<ImageDataUriVector> Vectors => new(ImageDataUriVectors.All);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void EachConformanceCaseIsClassifiedAsTheDefinitionSays(ImageDataUriVector vector)
    {
        var written = Encoding.UTF8.GetBytes(vector.Written);
        var escaped = vector.Written.Contains('\\', StringComparison.Ordinal);

        Assert.Equal(vector.IsImageDataUri, JsonImageDataUri.Is(written, escaped));
    }

    [Fact]
    public void TheCasesCoverBothOutcomesAndEveryListedMediaType()
    {
        Assert.Contains(ImageDataUriVectors.All, vector => vector.IsImageDataUri);
        Assert.Contains(ImageDataUriVectors.All, vector => !vector.IsImageDataUri);
        foreach (var type in new[] { "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml" })
        {
            Assert.Contains(
                ImageDataUriVectors.All,
                vector => vector.IsImageDataUri && vector.Decoded.StartsWith("data:" + type + ";", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ALargeImageIsClassifiedWithoutAllocating()
    {
        var value = Encoding.UTF8.GetBytes("data:image/png;base64," + new string('A', 1_000_000));
        Assert.True(JsonImageDataUri.Is(value, isEscaped: false));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = JsonImageDataUri.Is(value, isEscaped: false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result);
        Assert.Equal(0, allocated);
    }
}
