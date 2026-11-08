namespace ReportService.TemplatePackages.Records;

/// <summary>
/// One file of a template package as the take verified it: its package-relative
/// path, its size in bytes and the SHA-256 of its bytes, lower-case hexadecimal.
/// </summary>
/// <param name="Path">The package-relative path, with <c>/</c> separators.</param>
/// <param name="Size">The size of the file's bytes.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, lower-case hexadecimal.</param>
public sealed record TemplateSourceObject(string Path, long Size, string Sha256);
