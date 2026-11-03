using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplatePackages.Tests.Packages;

/// <summary>
/// The stored form of a package: one uncompressed archive whose bytes depend on
/// the files alone, so taking the same content twice gives the same hash and
/// different content gives a different one.
/// </summary>
public sealed class TemplatePackageArchiveTests
{
    [Fact]
    public async Task TwoWritesOfTheSameFilesAreByteIdentical()
    {
        var first = await WriteAsync(Files(("template.yaml", "manifest"), ("html/row.html.sbn", "row")));
        var second = await WriteAsync(Files(("template.yaml", "manifest"), ("html/row.html.sbn", "row")));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TheOrderTheFilesAreGivenInDoesNotChangeTheBytes()
    {
        var forward = await WriteAsync(Files(("template.yaml", "manifest"), ("html/row.html.sbn", "row"), ("schema.json", "schema")));
        var reversed = await WriteAsync(Files(("schema.json", "schema"), ("html/row.html.sbn", "row"), ("template.yaml", "manifest")));

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public async Task DifferentContentGivesDifferentBytes()
    {
        var one = await WriteAsync(Files(("template.yaml", "manifest")));
        var other = await WriteAsync(Files(("template.yaml", "manifest ")));

        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(one)), Convert.ToHexStringLower(SHA256.HashData(other)));
    }

    [Fact]
    public async Task EveryEntryIsARegularFileWithAFixedHeaderInOrdinalOrder()
    {
        var bytes = await WriteAsync(Files(("template.yaml", "manifest"), ("html/row.html.sbn", "row"), ("schema.json", "schema")));

        using var reader = new TarReader(new MemoryStream(bytes));
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, entry.Mode);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            var posix = Assert.IsAssignableFrom<PosixTarEntry>(entry);
            Assert.Equal(string.Empty, posix.UserName);
            Assert.Equal(string.Empty, posix.GroupName);
        }

        Assert.Equal(["html/row.html.sbn", "schema.json", "template.yaml"], names);
    }

    [Fact]
    public async Task TheBytesOfEveryFileAreTheBytesStored()
    {
        var bytes = await WriteAsync(Files(("template.yaml", "manifest"), ("html/row.html.sbn", "a row")));

        using var reader = new TarReader(new MemoryStream(bytes));
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            using var content = new StreamReader(entry.DataStream!, Encoding.UTF8);
            stored[entry.Name] = await content.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("manifest", stored["template.yaml"]);
        Assert.Equal("a row", stored["html/row.html.sbn"]);
    }

    [Fact]
    public async Task AFileTwiceInOnePackageIsRefused()
    {
        var files = Files(("template.yaml", "manifest"), ("template.yaml", "manifest"));

        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(files));
    }

    [Fact]
    public async Task APathThatIsNotAPackagePathIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(Files(("../outside.txt", "x"))));
    }

    [Fact]
    public async Task TheSamePathSpelledDifferentlyIsStillTheSamePath()
    {
        var one = await WriteAsync(Files(("html/row.html.sbn", "row")));
        var other = await WriteAsync(Files(("html/row.html.sbn", "row")));

        Assert.Equal(one, other);
    }

    /// <summary>
    /// The archive format splits a long path into a prefix and a name; the path
    /// rule's longest segment and longest path are both written, and a segment one
    /// character past the rule is refused by the rule itself — the format never
    /// meets a path it cannot hold.
    /// </summary>
    [Fact]
    public async Task ALongPathWithASlashIsWrittenAndASingleLongNameIsWrittenAtTheLimit()
    {
        var longButSplit = new string('a', PackagePath.MaxSegmentLength) + "/" + new string('b', PackagePath.MaxSegmentLength);
        var bytes = await WriteAsync(Files((longButSplit, "content")));
        using var reader = new TarReader(new MemoryStream(bytes));
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
        Assert.Equal(longButSplit, entry.Name);

        var longestName = new string('c', PackagePath.MaxSegmentLength);
        var written = await WriteAsync(Files((longestName, "content")));
        using var single = new TarReader(new MemoryStream(written));
        Assert.Equal(longestName, single.GetNextEntry()?.Name);

        var oneOver = new string('c', PackagePath.MaxSegmentLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(Files((oneOver, "content"))));
    }

    private static IReadOnlyList<PackageFileSource> Files(params (string Path, string Content)[] files) =>
    [
        .. files.Select(file => new PackageFileSource(
            file.Path,
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file.Content), writable: false)))),
    ];

    private static async Task<byte[]> WriteAsync(IReadOnlyList<PackageFileSource> files)
    {
        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, TestContext.Current.CancellationToken);
        return sink.ToArray();
    }
}
