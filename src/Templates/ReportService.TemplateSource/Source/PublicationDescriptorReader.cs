using System.Text.Json;

namespace ReportService.TemplateSource.Source;

/// <summary>The answer of one publication record parse: the record, or why it is not one this build reads.</summary>
public sealed record PublicationDescriptorRead
{
    private PublicationDescriptorRead(PublicationDescriptor? descriptor, string? reason)
    {
        Descriptor = descriptor;
        Reason = reason;
    }

    /// <summary>The parsed record, or <see langword="null"/> when the parse was refused.</summary>
    public PublicationDescriptor? Descriptor { get; }

    /// <summary>Why the record was refused, or <see langword="null"/> when it was parsed.</summary>
    public string? Reason { get; }

    /// <summary>True when the record was parsed.</summary>
    public bool Succeeded => Descriptor is not null;

    /// <summary>The record was parsed.</summary>
    /// <param name="descriptor">The parsed record.</param>
    public static PublicationDescriptorRead Ok(PublicationDescriptor descriptor) =>
        new(descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null);

    /// <summary>The record was refused, with the constant text that names the defect.</summary>
    /// <param name="reason">What the publisher has to fix.</param>
    public static PublicationDescriptorRead Refused(string reason) =>
        new(null, reason ?? throw new ArgumentNullException(nameof(reason)));
}

/// <summary>
/// The bounded, kind-strict reader of a publication record. It consumes only the
/// already-verified stream the port returned, never buffers more than
/// <see cref="MaxRecordBytes"/>, and refuses a member of the wrong JSON kind, a
/// duplicate member name and a missing required member instead of coercing any of
/// them. It builds no type from a value and carries the record's raw bytes nowhere.
/// </summary>
public static class PublicationDescriptorReader
{
    /// <summary>The most bytes a record may hold; a larger record is invalid.</summary>
    public const int MaxRecordBytes = 1024 * 1024;

    private const int BufferBytes = 16 * 1024;

    /// <summary>Parses one publication record from its verified bytes.</summary>
    /// <param name="content">The verified stream; it is read to its end.</param>
    public static PublicationDescriptorRead Read(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var bytes = ReadBounded(content);
        if (bytes is null)
        {
            return PublicationDescriptorRead.Refused(
                $"The publication record is larger than this service reads (at most {MaxRecordBytes} bytes).");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return PublicationDescriptorRead.Refused("The publication record is not a JSON document this service reads.");
        }
    }

    private static byte[]? ReadBounded(Stream content)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[BufferBytes];
        while (true)
        {
            if (buffer.Length > MaxRecordBytes)
            {
                return null;
            }

            var remaining = (int)Math.Min(chunk.Length, MaxRecordBytes + 1L - buffer.Length);
            var read = content.Read(chunk, 0, remaining);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length > MaxRecordBytes ? null : buffer.ToArray();
    }

    private static PublicationDescriptorRead Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Refused("The publication record is not a JSON object.");
        }

        if (DuplicateMember(root) is { } duplicate)
        {
            return Refused($"The publication record names the member '{duplicate}' more than once.");
        }

        if (RequiredString(root, "descriptorVersion") is not { } descriptorVersion
            || RequiredString(root, "templateId") is not { } templateId
            || RequiredString(root, "version") is not { } version)
        {
            return Refused("The publication record does not declare descriptorVersion, templateId and version as strings.");
        }

        if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            return Refused("The publication record does not declare 'files' as an array.");
        }

        var files = new List<SourceFile>(filesElement.GetArrayLength());
        foreach (var element in filesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Refused("A file entry of the publication record is not a JSON object.");
            }

            if (DuplicateMember(element) is { } fileDuplicate)
            {
                return Refused($"A file entry of the publication record names the member '{fileDuplicate}' more than once.");
            }

            if (RequiredString(element, "path") is not { } path
                || RequiredLong(element, "size") is not { } size
                || RequiredString(element, "sha256") is not { } sha256)
            {
                return Refused("A file entry of the publication record does not declare path, size and sha256 with the JSON kinds this service reads.");
            }

            files.Add(new SourceFile(path, size, sha256));
        }

        if (RequiredInt(root, "fileCount") is not { } fileCount
            || RequiredLong(root, "totalSize") is not { } totalSize)
        {
            return Refused("The publication record does not declare fileCount and totalSize as integers.");
        }

        return PublicationDescriptorRead.Ok(new PublicationDescriptor(
            descriptorVersion,
            templateId,
            version,
            files,
            fileCount,
            totalSize));
    }

    private static string? DuplicateMember(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
        {
            if (!names.Add(member.Name))
            {
                return member.Name;
            }
        }

        return null;
    }

    private static string? RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? RequiredLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static PublicationDescriptorRead Refused(string reason) => PublicationDescriptorRead.Refused(reason);
}
