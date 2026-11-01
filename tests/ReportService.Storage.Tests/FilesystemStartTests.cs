using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage.Filesystem;
using ReportService.Testing;

namespace ReportService.Storage.Tests;

/// <summary>
/// Opening the Filesystem provider: its settings, the root checks that stop a
/// start (naming the setting, never its value), the stale temporary-file
/// sweep and its registration.
/// </summary>
public sealed class FilesystemStartTests : IDisposable
{
    private const string RootSetting = "Storage:Filesystem:Root";
    private readonly TemporaryRoot _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ValidSettingsHaveNoProblem()
    {
        var options = new FilesystemStorageOptions { Root = "data" };

        Assert.Empty(options.Validate());
        Assert.Equal(64 * 1024, options.BufferBytes);
        Assert.False(options.CreateIfMissing);
    }

    [Theory]
    [InlineData(null, 65536, "Root", "is required")]
    [InlineData(" ", 65536, "Root", "is required")]
    [InlineData("data", 4095, "BufferBytes", "must be between 4096 and 1048576")]
    [InlineData("data", 1048577, "BufferBytes", "must be between 4096 and 1048576")]
    public void FaultySettingsAreNamed(string? root, int bufferBytes, string setting, string reason)
    {
        var problem = Assert.Single(new FilesystemStorageOptions { Root = root, BufferBytes = bufferBytes }.Validate());

        Assert.Equal(setting, problem.Setting);
        Assert.Equal(reason, problem.Reason);
    }

    [Fact]
    public void MissingRootSettingStopsTheStartNamingIt()
    {
        var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions()));

        Assert.Equal(RootSetting, error.Setting);
        Assert.Equal("is required", error.Reason);
    }

    [Fact]
    public void FaultyBufferStopsTheStartNamingIt()
    {
        var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions { Root = _directory.Path, BufferBytes = 1 }));

        Assert.Equal("Storage:Filesystem:BufferBytes", error.Setting);
    }

    [Fact]
    public void MissingRootThatMayNotBeCreatedStopsTheStartWithoutItsPath()
    {
        var root = Path.Combine(_directory.Path, "missing-root");

        var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions { Root = root }));

        Assert.Equal(RootSetting, error.Setting);
        Assert.Equal("the directory does not exist", error.Reason);
        Assert.DoesNotContain(root, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MissingRootIsCreatedWhenAllowed()
    {
        var root = Path.Combine(_directory.Path, "created", "root");

        var store = Open(new FilesystemStorageOptions { Root = root, CreateIfMissing = true });

        Assert.True(Directory.Exists(root));
        Assert.Equal(root, store.Root);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void RelativeRootIsResolvedAgainstTheContentRoot()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Path, "relative"));

        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = "relative" },
            _directory.Path,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        Assert.Equal(Path.Combine(_directory.Path, "relative"), store.Root);
    }

    [Fact]
    public void RootThatIsAFileStopsTheStart()
    {
        var file = Path.Combine(_directory.Path, "a-file");
        File.WriteAllText(file, "x");

        var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions { Root = file, CreateIfMissing = true }));

        Assert.Equal("is not a directory", error.Reason);
    }

    [Fact]
    public void RootThatCannotBeCreatedStopsTheStart()
    {
        var file = Path.Combine(_directory.Path, "a-file");
        File.WriteAllText(file, "x");

        var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions { Root = Path.Combine(file, "below"), CreateIfMissing = true }));

        Assert.Equal("the directory cannot be created", error.Reason);
    }

    [Fact]
    public void UnwritableRootStopsTheStart()
    {
        var root = Path.Combine(_directory.Path, "read-only");
        Directory.CreateDirectory(root);
        using (new UnwritableDirectory(root))
        {
            var error = Assert.Throws<StorageConfigurationException>(() => Open(new FilesystemStorageOptions { Root = root }));

            Assert.Equal(RootSetting, error.Setting);
            Assert.Equal("the directory is not writable", error.Reason);
        }
    }

    [Fact]
    public void RootThatIsALinkIsResolvedOnceAtStart()
    {
        var real = Path.Combine(_directory.Path, "real-root");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_directory.Path, "linked-root");
        Links.CreateDirectoryLink(link, real);

        var store = Open(new FilesystemStorageOptions { Root = link });

        Assert.Equal(real, store.Root, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void StartLeavesNoProbeBehind()
    {
        Open(new FilesystemStorageOptions { Root = _directory.Path });

        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory.Path));
    }

    [Fact]
    public void StaleTemporaryFilesAreSweptAtStartAndNothingElse()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var nested = Path.Combine(_directory.Path, "staging", "upload");
        Directory.CreateDirectory(nested);
        var staleTop = Write(_directory.Path, ".object.tmp-0123456789abcdef0123456789abcdef", now.AddHours(-25));
        var staleNested = Write(nested, ".content.tmp-fedcba9876543210fedcba9876543210", now.AddDays(-3));
        var young = Write(nested, ".young.tmp-00000000000000000000000000000000", now.AddHours(-23));
        var oldObject = Write(nested, "content", now.AddDays(-30));
        var badSuffix = Write(nested, ".content.tmp-xyz", now.AddDays(-30));
        var upperSuffix = Write(nested, ".content.tmp-0123456789ABCDEF0123456789ABCDEF", now.AddDays(-30));
        var noMarker = Write(nested, ".content-0123456789abcdef0123456789abcdef", now.AddDays(-30));

        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _directory.Path },
            _directory.Path,
            new FixedTime(now),
            NullLogger<FilesystemObjectStore>.Instance);

        Assert.Equal(2, store.SweptTemporaryFiles);
        Assert.False(File.Exists(staleTop));
        Assert.False(File.Exists(staleNested));
        Assert.All([young, oldObject, badSuffix, upperSuffix, noMarker], path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void TemporaryNamesHaveOneShape()
    {
        Assert.Equal(".object.tmp-abc", CleanupTemporaryFiles.NameFor("object", "abc"));
        Assert.True(CleanupTemporaryFiles.IsTemporaryName(".object.tmp-0123456789abcdef0123456789abcdef"));
        Assert.False(CleanupTemporaryFiles.IsTemporaryName("object.tmp-0123456789abcdef0123456789abcdef"));
        Assert.False(CleanupTemporaryFiles.IsTemporaryName(".tmp-0123456789abcdef0123456789abcdef"));
        Assert.False(CleanupTemporaryFiles.IsTemporaryName(".a.tmp-0123456789abcdef0123456789abcdeg"));
    }

    [Fact]
    public void OpenChecksItsArguments()
    {
        var options = new FilesystemStorageOptions { Root = _directory.Path };
        var logger = NullLogger<FilesystemObjectStore>.Instance;

        Assert.Throws<ArgumentNullException>(() => FilesystemObjectStore.Open(null!, _directory.Path, TimeProvider.System, logger));
        Assert.Throws<ArgumentException>(() => FilesystemObjectStore.Open(options, " ", TimeProvider.System, logger));
        Assert.Throws<ArgumentNullException>(() => FilesystemObjectStore.Open(options, _directory.Path, null!, logger));
        Assert.Throws<ArgumentNullException>(() => FilesystemObjectStore.Open(options, _directory.Path, TimeProvider.System, null!));
    }

    [Fact]
    public void RegistrationProvidesOneStoreOpenedOnFirstUse()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddFilesystemObjectStore(_ => new FilesystemStorageOptions { Root = "registered", CreateIfMissing = true }, _directory.Path);
        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObjectStore>();

        Assert.Same(provider.GetRequiredService<FilesystemObjectStore>(), store);
        Assert.True(Directory.Exists(Path.Combine(_directory.Path, "registered")));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddFilesystemObjectStore(_ => new FilesystemStorageOptions(), "x"));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFilesystemObjectStore(null!, "x"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddFilesystemObjectStore(_ => new FilesystemStorageOptions(), " "));
    }

    private FilesystemObjectStore Open(FilesystemStorageOptions options) =>
        FilesystemObjectStore.Open(options, _directory.Path, TimeProvider.System, _directory.LoggerFactory.CreateLogger<FilesystemObjectStore>());

    private static string Write(string directory, string name, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
