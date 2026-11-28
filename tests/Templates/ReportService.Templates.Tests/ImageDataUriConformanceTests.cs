using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The image-source reading of an image data URI holds to the same conformance cases as the row
/// bound's reading of a record value: one definition, two readers. The image-source reader first
/// prepares the value as a browser prepares a URL - tabs and line breaks removed, leading and
/// trailing controls and spaces stripped - so the cases that preparation changes are outside its
/// domain and are not compared; every other case must be classified as the definition says.
/// </summary>
public sealed class ImageDataUriConformanceTests
{
    public static TheoryData<ImageDataUriVector> Vectors => new(ImageDataUriVectors.All.Where(UnchangedByUrlPreparation));

    [Theory]
    [MemberData(nameof(Vectors))]
    public void TheImageSourceReadingClassifiesEachCaseAsTheDefinitionSays(ImageDataUriVector vector)
    {
        Assert.Equal(vector.IsImageDataUri, ImageDataUri.Is(vector.Decoded));
    }

    [Fact]
    public void OnlyTheCasesUrlPreparationWouldChangeAreLeftOut()
    {
        var left = ImageDataUriVectors.All.Where(vector => !UnchangedByUrlPreparation(vector)).Select(vector => vector.Clause).ToList();

        Assert.Equal(["a line break inside the body", "whitespace before the scheme"], left.Order(StringComparer.Ordinal));
    }

    private static bool UnchangedByUrlPreparation(ImageDataUriVector vector) =>
        vector.Decoded.IndexOfAny(['\t', '\n', '\r']) < 0
        && (vector.Decoded.Length == 0 || (vector.Decoded[0] > ' ' && vector.Decoded[^1] > ' '));
}
