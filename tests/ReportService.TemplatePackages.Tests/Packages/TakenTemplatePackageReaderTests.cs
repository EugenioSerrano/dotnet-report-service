using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplatePackages.Tests.Packages;

/// <summary>
/// Reading a taken copy back: the bytes are verified before anything is handed
/// out, an entry that is not a package file is refused, and a copy that cannot
/// be read leaves nothing behind.
/// </summary>
public sealed class TakenTemplatePackageReaderTests
{
    private static readonly TemplateManifest Manifest = TemplateManifestJson.ReadTyped(
        """{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0","engine":"scriban"}""");

    [Fact]
    public async Task ATakenCopyIsExtractedAndHandedOutWithItsManifest()
    {
        using var root = new TestDirectory();
        var store = Open(root);
        var bytes = await ArchiveAsync(("template.yaml", "manifest"), ("html/row.html.sbn", "a row"));
        var record = await RecordASync(store, bytes);

        var reader = new TakenTemplatePackageReader(store);
        await using var package = await reader.OpenAsync(record, root.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["html/row.html.sbn", "template.yaml"], package.Files);
        Assert.Equal("a row", await File.ReadAllTextAsync(Path.Combine(package.Root, "html", "row.html.sbn"), TestContext.Current.CancellationToken));
        Assert.Equal("manifest", await File.ReadAllTextAsync(Path.Combine(package.Root, "template.yaml"), TestContext.Current.CancellationToken));
        Assert.Same(Manifest, package.Manifest);
        Assert.Equal(record, package.Record);

        var directory = package.ScratchDirectory;
        await package.DisposeAsync();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AnObjectThatNoLongerVerifiesIsRefusedAndLeavesNothing()
    {
        using var root = new TestDirectory();
        var store = Open(root);
        var bytes = await ArchiveAsync(("template.yaml", "manifest"));
        var record = await RecordASync(store, bytes);
        var stored = StoredPath(root, record.ObjectKey);
        bytes[^1] ^= 0xff;
        await File.WriteAllBytesAsync(stored, bytes, TestContext.Current.CancellationToken);

        var reader = new TakenTemplatePackageReader(store);

        await Assert.ThrowsAsync<TemplatePackageCorruptException>(
            () => reader.OpenAsync(record, root.Path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root.Path, "taken-*"));
    }

    [Fact]
    public async Task ATruncatedObjectIsRefusedAndLeavesNothing()
    {
        using var root = new TestDirectory();
        var store = Open(root);
        var bytes = await ArchiveAsync(("template.yaml", "manifest"));
        var record = await RecordASync(store, bytes);
        await File.WriteAllBytesAsync(StoredPath(root, record.ObjectKey), bytes[..^100], TestContext.Current.CancellationToken);

        var reader = new TakenTemplatePackageReader(store);

        await Assert.ThrowsAsync<TemplatePackageCorruptException>(
            () => reader.OpenAsync(record, root.Path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root.Path, "taken-*"));
    }

    [Fact]
    public async Task AnEntryThatWouldEscapeTheDirectoryIsRefused()
    {
        using var root = new TestDirectory();
        var store = Open(root);
        var bytes = await ArchiveAsync(("template.yaml", "manifest"));
        Encoding.ASCII.GetBytes("../outside.txt").CopyTo(bytes, 0);

        var packageHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var record = RecordFor(packageHash, bytes.Length);
        var written = await store.WriteAsync(record.ObjectKey, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.True(written.Succeeded);

        var reader = new TakenTemplatePackageReader(store);

        await Assert.ThrowsAsync<TemplatePackageCorruptException>(
            () => reader.OpenAsync(record, root.Path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root.Path, "taken-*"));
    }

    [Fact]
    public async Task AnObjectThatWasNeverWrittenIsReportedUnavailable()
    {
        using var root = new TestDirectory();
        var store = Open(root);
        var record = RecordFor(new string('a', 64), 10, new string('b', 64));

        var reader = new TakenTemplatePackageReader(store);

        var unavailable = await Assert.ThrowsAsync<TemplatePackageUnavailableException>(
            () => reader.OpenAsync(record, root.Path, TestContext.Current.CancellationToken));
        Assert.Equal(StorageOutcome.NotFound, unavailable.Outcome);
    }

    private static FilesystemObjectStore Open(TestDirectory root) =>
        FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root.Path, CreateIfMissing = true },
            root.Path,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

    private static async Task<byte[]> ArchiveAsync(params (string Path, string Content)[] files)
    {
        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(
            [.. files.Select(file => new PackageFileSource(
                file.Path,
                _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file.Content), writable: false))))],
            sink);
        return sink.ToArray();
    }

    private static async Task<TemplateVersionRecord> RecordASync(IObjectStore store, byte[] bytes)
    {
        var record = RecordFor(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        var written = await store.WriteAsync(record.ObjectKey, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.True(written.Succeeded);
        return record;
    }

    private static TemplateVersionRecord RecordFor(string packageHash, long size, string? commitSha = null) =>
        new(
            "invoice-energy",
            "1.1.0",
            "billing-system",
            Shared: false,
            "1.0",
            TemplateEngine.Scriban,
            TemplateEngineCatalog.ScribanVersion,
            "schema.json",
            commitSha ?? new string('1', 40),
            new string('2', 40),
            packageHash,
            size,
            FileCount: 1,
            TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", packageHash),
            Manifest,
            """{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0","engine":"scriban"}""",
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private static string StoredPath(TestDirectory root, ObjectKey key) =>
        Path.Combine(root.Path, key.ToString().Replace('/', Path.DirectorySeparatorChar));
}
