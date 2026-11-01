namespace ReportService.Storage;

/// <summary>What the store knows about an object without reading it.</summary>
/// <param name="Key">The object's key.</param>
/// <param name="Size">The object's size in bytes.</param>
/// <param name="LastModified">When the object was last written, in UTC.</param>
public sealed record ObjectInfo(ObjectKey Key, long Size, DateTimeOffset LastModified);
