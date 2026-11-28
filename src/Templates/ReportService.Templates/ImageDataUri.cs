namespace ReportService.Templates;

/// <summary>
/// The one test of an image data URI every output shares: a base64 <c>data:</c> URI of
/// <c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c>, <c>image/webp</c> or
/// <c>image/svg+xml</c>, read after the preparation a browser applies to a URL (tabs and line
/// breaks removed, leading and trailing controls and spaces stripped). The output writer's
/// image-source check and the HTML assembly decide with the same reading.
/// </summary>
public static class ImageDataUri
{
    /// <summary>The most UTF-8 bytes one image data URI may hold in an output that bounds it.</summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>Whether <paramref name="value"/>, already decoded of character references, is an image data URI.</summary>
    /// <param name="value">The attribute value, decoded.</param>
    public static bool Is(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var reading = new ImageSourceScanner.ImageSourceValue();
        reading.Reset();
        reading.Read(value);
        return reading.IsImageDataUri;
    }
}
