using System.Globalization;
using Microsoft.Extensions.Logging;
using ReportService.Storage.Filesystem;
using ReportService.Testing;

namespace ReportService.Storage.Tests;

/// <summary>The large-source memory case measures the whole process, so this class runs alone.</summary>
[CollectionDefinition(nameof(MeasuredAlone), DisableParallelization = true)]
public sealed class MeasuredAlone;

/// <summary>
/// The Filesystem provider: the whole conformance suite, plus what only this
/// provider has — its exact capabilities, its pages, its paths and links.
/// </summary>
[Collection(nameof(MeasuredAlone))]
public sealed class FilesystemObjectStoreTests : ObjectStoreConformanceTests
{
    /// <inheritdoc />
    protected override StorageCapabilities ExpectedCapabilities =>
        new(SignedUpload: false, SignedDownload: false, NativeCreateOnly: true, RangeReads: false);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private FilesystemObjectStore Filesystem => (FilesystemObjectStore)Store;

    [Fact]
    public void CapabilitiesAreExactlyCreateOnlyAndNothingElse()
    {
        Assert.True(Store.Capabilities.NativeCreateOnly);
        Assert.False(Store.Capabilities.SignedUpload);
        Assert.False(Store.Capabilities.SignedDownload);
        Assert.False(Store.Capabilities.RangeReads);
        Assert.Equal(FilesystemObjectStore.DeclaredCapabilities, Store.Capabilities);
        Assert.False(Store is ISignedUrlStore);
    }

    [Fact]
    public async Task ListingComesInPagesOfAtMost256()
    {
        for (var i = 0; i < 600; i++)
        {
            await WriteAsync(string.Create(CultureInfo.InvariantCulture, $"pages/object-{i:d4}"), 1, seed: 20);
        }

        var pages = new List<int>();
        await foreach (var page in Filesystem.ListPagesAsync("pages", Cancellation))
        {
            pages.Add(page.Count);
        }

        Assert.Equal([256, 256, 88], pages);
    }

    [Fact]
    public async Task ListingSkipsTemporaryFilesLinksAndNamesOutsideTheGrammar()
    {
        await WriteAsync("skip/object", 1, seed: 21);
        var directory = Path.Combine(Root.Path, "skip");
        await File.WriteAllTextAsync(Path.Combine(directory, ".object.tmp-0123456789abcdef0123456789abcdef"), "x", Cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "Upper.txt"), "x", Cancellation);
        using var outside = new TemporaryRoot();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret"), "x", Cancellation);
        Links.CreateDirectoryLink(Path.Combine(directory, "linked"), outside.Path);

        var listed = new List<string>();
        await foreach (var item in Store.ListAsync("skip", Cancellation))
        {
            listed.Add(item.Key.ToString());
        }

        Assert.Equal(["skip/object"], listed);
    }

    [Fact]
    public async Task ListingOfAPrefixThatIsAnObjectIsEmpty()
    {
        await WriteAsync("single", 1, seed: 22);

        var count = 0;
        await foreach (var unused in Store.ListAsync("single", Cancellation))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ListingStopsWhenCancelled()
    {
        for (var i = 0; i < 300; i++)
        {
            await WriteAsync(string.Create(CultureInfo.InvariantCulture, $"cancel/object-{i:d3}"), 1, seed: 23);
        }

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in Store.ListAsync("cancel", cancellation.Token))
            {
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task ListingThroughALinkOutOfTheRootIsRefused()
    {
        using var outside = new TemporaryRoot();
        Links.CreateDirectoryLink(Path.Combine(Root.Path, "escape"), outside.Path);

        var failure = await Assert.ThrowsAsync<StorageException>(async () =>
        {
            await foreach (var unused in Store.ListAsync("escape", Cancellation))
            {
                Assert.Fail("Nothing is listed through a link out of the root.");
            }
        });

        Assert.Equal(StorageErrorCodes.PathOutsideRoot, failure.Error.Code);
        Assert.Contains(Root.Logs.Lines, l => l.Contains("Object storage list failed: storage.path-outside-root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryOperationThroughALinkOutOfTheRootIsRefused()
    {
        using var outside = new TemporaryRoot();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret"), "outside", Cancellation);
        Links.CreateDirectoryLink(Path.Combine(Root.Path, "escape"), outside.Path);
        var secret = ObjectKey.Create("escape/secret");
        var fresh = ObjectKey.Create("escape/new/object");

        var write = await Store.WriteAsync(fresh, new GeneratedContent(10, seed: 24), Cancellation);
        var read = await Store.OpenReadAsync(secret, 7, GeneratedContent.Sha256Of(7, 0), Cancellation);
        var info = await Store.GetInfoAsync(secret, Cancellation);
        var delete = await Store.DeleteAsync(secret, Cancellation);

        Assert.All(
            [write.Error, read.Error, info.Error, delete.Error],
            error => Assert.Equal(StorageErrorCodes.PathOutsideRoot, error!.Code));
        Assert.Equal(StorageOutcome.Permanent, write.Outcome);
        Assert.True(File.Exists(Path.Combine(outside.Path, "secret")));
        Assert.False(Directory.Exists(Path.Combine(outside.Path, "new")));
    }

    [Fact]
    public async Task FileLinkOutOfTheRootIsRefused()
    {
        using var outside = new TemporaryRoot();
        var target = Path.Combine(outside.Path, "secret");
        await File.WriteAllTextAsync(target, "outside", Cancellation);
        Directory.CreateDirectory(Path.Combine(Root.Path, "files"));
        if (!Links.TryCreateFileLink(Path.Combine(Root.Path, "files", "secret"), target))
        {
            Assert.Skip("This machine does not let the user create a file symbolic link; the directory-link cases cover the check here.");
        }

        var info = await Store.GetInfoAsync(ObjectKey.Create("files/secret"), Cancellation);

        Assert.Equal(StorageErrorCodes.PathOutsideRoot, info.Error!.Code);
    }

    [Fact]
    public async Task LinkThatStaysInsideTheRootIsFollowed()
    {
        Directory.CreateDirectory(Path.Combine(Root.Path, "real"));
        Links.CreateDirectoryLink(Path.Combine(Root.Path, "alias"), Path.Combine(Root.Path, "real"));

        var write = await Store.WriteAsync(ObjectKey.Create("alias/object"), new GeneratedContent(10, seed: 25), Cancellation);

        Assert.Equal(StorageOutcome.Success, write.Outcome);
        Assert.True(File.Exists(Path.Combine(Root.Path, "real", "object")));
    }

    [Fact]
    public async Task KeyResolvingToAPathLongerThan248CharactersIsRefused()
    {
        var room = FilesystemObjectStore.MaxPathLength - Filesystem.Root.Length - 1;
        var segments = new List<string>();
        for (var remaining = room + 1; remaining > 0; remaining -= ObjectKey.MaxSegmentLength + 1)
        {
            segments.Add(new string('l', Math.Clamp(remaining, 1, ObjectKey.MaxSegmentLength)));
        }

        var key = ObjectKey.FromSegments([.. segments]);
        Assert.True(Path.Combine([Filesystem.Root, .. segments]).Length > FilesystemObjectStore.MaxPathLength);

        var write = await Store.WriteAsync(key, new GeneratedContent(1, seed: 26), Cancellation);

        Assert.Equal(StorageOutcome.Permanent, write.Outcome);
        Assert.Equal(StorageErrorCodes.PathTooLong, write.Error!.Code);
        Assert.DoesNotContain(Root.Path, write.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyThatNamesADirectoryIsRefusedByEveryOperation()
    {
        await WriteAsync("folder/inner/object", 1, seed: 27);
        var folder = ObjectKey.Create("folder/inner");

        var write = await Store.WriteAsync(folder, new GeneratedContent(1, seed: 28), Cancellation);
        var read = await Store.OpenReadAsync(folder, 1, GeneratedContent.Sha256Of(1, 28), Cancellation);
        var info = await Store.GetInfoAsync(folder, Cancellation);
        var delete = await Store.DeleteAsync(folder, Cancellation);

        Assert.All(
            [write.Error, read.Error, info.Error, delete.Error],
            error => Assert.Equal(StorageErrorCodes.KeyIsDirectory, error!.Code));
        Assert.True(Directory.Exists(Path.Combine(Root.Path, "folder", "inner")));
    }

    [Fact]
    public async Task KeyUnderAnObjectIsRefused()
    {
        await WriteAsync("parent/object", 1, seed: 29);

        var write = await Store.WriteAsync(ObjectKey.Create("parent/object/child"), new GeneratedContent(1, seed: 30), Cancellation);

        Assert.Equal(StorageOutcome.Permanent, write.Outcome);
        Assert.Equal(StorageErrorCodes.KeyConflict, write.Error!.Code);
    }

    [Fact]
    public async Task InterruptedWriteLeavesNeitherAnObjectNorATemporaryFile()
    {
        var key = ObjectKey.Create("interrupted/object");

        await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync(key, new GeneratedContent(500_000, seed: 31, failAfter: 200_000), Cancellation));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Root.Path, "interrupted")));
    }

    [Fact]
    public async Task SuccessfulWriteLeavesOnlyTheObject()
    {
        await WriteAsync("clean/object", 70_000, seed: 32);

        Assert.Equal(["object"], Directory.EnumerateFileSystemEntries(Path.Combine(Root.Path, "clean")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task FailureIsLoggedOnceWithItsOperationAndCodeOnly()
    {
        await WriteAsync("logged/object", 1, seed: 33);

        await Store.WriteAsync(ObjectKey.Create("logged/object"), new GeneratedContent(1, seed: 34), Cancellation);

        var line = Assert.Single(Root.Logs.Lines);
        Assert.Equal("Warning: ReportService.Storage.Filesystem.FilesystemObjectStore: Object storage write failed: storage.already-exists", line);
    }

    [Fact]
    public async Task WriteIntoAnUnwritableDirectoryIsAPermanentPermissionFailure()
    {
        var directory = Path.Combine(Root.Path, "locked");
        Directory.CreateDirectory(directory);
        using (new UnwritableDirectory(directory))
        {
            var write = await Store.WriteAsync(ObjectKey.Create("locked/object"), new GeneratedContent(10, seed: 35), Cancellation);

            Assert.Equal(StorageOutcome.Permanent, write.Outcome);
            Assert.Equal(StorageErrorCodes.PermissionDenied, write.Error!.Code);
        }
    }

    [Fact]
    public async Task ObjectHeldOpenExclusivelyIsATransientFailureOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to open or delete a file another handle holds exclusively.");
        var (key, written) = await WriteAsync("held/object", 10, seed: 36);
        await using (new FileStream(Root.PathOf(key), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = await Store.OpenReadAsync(key, written.Size, written.Sha256, Cancellation);
            var delete = await Store.DeleteAsync(key, Cancellation);

            Assert.Equal(StorageOutcome.Transient, read.Outcome);
            Assert.Equal(StorageErrorCodes.IoError, read.Error!.Code);
            Assert.Equal(StorageOutcome.Transient, delete.Outcome);
            Assert.DoesNotContain(Root.Path, read.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OperationsRefuseAnEmptyKeyAndInvalidArguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.WriteAsync(default, new GeneratedContent(1, 0), Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetInfoAsync(default, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(default, Cancellation));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.WriteAsync(ObjectKey.Create("a"), null!, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.WriteAsync(ObjectKey.Create("a"), new WriteOnlyStream(), Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store.OpenReadAsync(ObjectKey.Create("a"), -1, GeneratedContent.Sha256Of(0, 0), Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.OpenReadAsync(ObjectKey.Create("a"), 0, "ABC", Cancellation));
    }

    /// <inheritdoc />
    protected override IObjectStore CreateStore(TemporaryRoot root) => FilesystemObjectStore.Open(
        new FilesystemStorageOptions { Root = root.Path },
        root.Path,
        TimeProvider.System,
        root.LoggerFactory.CreateLogger<FilesystemObjectStore>());

    /// <inheritdoc />
    protected override async Task TamperAsync(TemporaryRoot root, ObjectKey key, Func<byte[], byte[]> change)
    {
        var path = root.PathOf(key);
        var bytes = await File.ReadAllBytesAsync(path, Cancellation);
        await File.WriteAllBytesAsync(path, change(bytes), Cancellation);
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
