namespace ReportService.Storage;

/// <summary>What a successful write stored.</summary>
/// <param name="Key">The key written.</param>
/// <param name="Size">The number of bytes actually written.</param>
/// <param name="Sha256">The SHA-256 of the bytes actually written, lower-case hexadecimal.</param>
/// <param name="ProviderVersion">The provider's version or entity tag of the object, when it has one.</param>
public sealed record ObjectWrite(ObjectKey Key, long Size, string Sha256, string? ProviderVersion);
