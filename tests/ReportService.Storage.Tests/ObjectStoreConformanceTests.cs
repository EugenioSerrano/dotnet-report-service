using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace ReportService.Storage.Tests;

/// <summary>
/// The provider-conformance suite: the behaviour every provider of
/// <see cref="IObjectStore"/> must show, run unchanged against each one. A
/// provider's test project derives this class, supplies its store, its
/// declared capabilities and a way to change stored bytes behind the store's
/// back, and passes every case. A provider that fails any case is not
/// shipped.
/// </summary>
public abstract class ObjectStoreConformanceTests : IDisposable
{
    /// <summary>The bound of the large-source case: growth for 1 GiB may exceed growth for 64 MiB by at most this.</summary>
    public const long MemoryAllowance = 32L * 1024 * 1024;

    private const long SmallSource = 64L * 1024 * 1024;
    private const long LargeSource = 1024L * 1024 * 1024;
    private const string PlantedSegment = "planted-secret-7c1e40";

    private IObjectStore? _store;

    /// <summary>The test's own storage root.</summary>
    protected TemporaryRoot Root { get; } = new();

    /// <summary>The store under test, created on first use over <see cref="Root"/>.</summary>
    protected IObjectStore Store => _store ??= CreateStore(Root);

    /// <summary>The capabilities the provider must declare.</summary>
    protected abstract StorageCapabilities ExpectedCapabilities { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose()
    {
        (_store as IDisposable)?.Dispose();
        Root.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StoreDeclaresItsCapabilities() => Assert.Equal(ExpectedCapabilities, Store.Capabilities);

    [Fact]
    public async Task WriteReturnsTheTrueHashAndSizeOfTheBytesWritten()
    {
        var key = ObjectKey.Create("conformance/write/object");
        const long length = 300_001;

        var result = await Store.WriteAsync(key, new GeneratedContent(length, seed: 1), Cancellation);

        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(key, result.Value!.Key);
        Assert.Equal(length, result.Value.Size);
        Assert.Equal(GeneratedContent.Sha256Of(length, seed: 1), result.Value.Sha256);
        Assert.Equal(result.Value.Sha256, await ReadBackSha256Async(key, result.Value.Size, result.Value.Sha256));
    }

    [Fact]
    public async Task EmptyObjectIsWrittenAndReadBackVerified()
    {
        var key = ObjectKey.Create("conformance/write/empty");

        var result = await Store.WriteAsync(key, new GeneratedContent(0, seed: 2), Cancellation);

        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Value!.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty)), result.Value.Sha256);
        Assert.Equal(result.Value.Sha256, await ReadBackSha256Async(key, 0, result.Value.Sha256));
    }

    [Fact]
    public async Task SecondWriteToTheSameKeyReportsAlreadyExistsAndLeavesTheFirstObjectUnchanged()
    {
        var key = ObjectKey.Create("conformance/write-once/object");
        var first = await Store.WriteAsync(key, new GeneratedContent(50_000, seed: 3), Cancellation);

        var second = await Store.WriteAsync(key, new GeneratedContent(70_000, seed: 4), Cancellation);

        if (ExpectedCapabilities.NativeCreateOnly)
        {
            Assert.Equal(StorageOutcome.AlreadyExists, second.Outcome);
            Assert.Equal(StorageErrorCodes.AlreadyExists, second.Error!.Code);
        }
        else
        {
            // A provider without native create-only detects it as well as it can; correctness never relies on it.
            Assert.Contains(second.Outcome, new[] { StorageOutcome.AlreadyExists, StorageOutcome.Success });
        }

        Assert.Equal(first.Value!.Sha256, await ReadBackSha256Async(key, first.Value.Size, first.Value.Sha256));
    }

    [Fact]
    public async Task VerifyingReadDetectsAFlippedByte()
    {
        var (key, written) = await WriteAsync("conformance/integrity/flipped", 10_000, seed: 5);
        await TamperAsync(Root, key, bytes =>
        {
            bytes[5_000] ^= 0x01;
            return bytes;
        });

        var failure = await ConsumeExpectingFailureAsync(key, written);

        Assert.Equal(StorageOutcome.IntegrityFailure, failure.Outcome);
        Assert.Equal(StorageErrorCodes.HashMismatch, failure.Error.Code);
        Assert.Equal(written.Sha256, failure.ExpectedSha256);
        Assert.NotEqual(written.Sha256, failure.ActualSha256);
        Assert.Equal(written.Size, failure.ActualSize);
    }

    [Fact]
    public async Task VerifyingReadDetectsATruncatedObject()
    {
        var (key, written) = await WriteAsync("conformance/integrity/truncated", 10_000, seed: 6);
        await TamperAsync(Root, key, bytes => bytes[..9_900]);

        var failure = await ConsumeExpectingFailureAsync(key, written);

        Assert.Equal(StorageErrorCodes.SizeMismatch, failure.Error.Code);
        Assert.Equal(10_000, failure.ExpectedSize);
        Assert.Equal(9_900, failure.ActualSize);
    }

    [Fact]
    public async Task VerifyingReadDetectsAnExtraByte()
    {
        var (key, written) = await WriteAsync("conformance/integrity/extra", 10_000, seed: 7);
        await TamperAsync(Root, key, bytes => [.. bytes, 0x00]);

        var failure = await ConsumeExpectingFailureAsync(key, written);

        Assert.Equal(StorageErrorCodes.SizeMismatch, failure.Error.Code);
        Assert.Equal(10_000, failure.ExpectedSize);
        Assert.Equal(10_001, failure.ActualSize);
    }

    [Fact]
    public async Task ReadWithAnotherExpectedHashIsAnIntegrityFailure()
    {
        var (key, written) = await WriteAsync("conformance/integrity/expected", 1_000, seed: 8);
        var other = GeneratedContent.Sha256Of(1_000, seed: 9);

        var failure = await ConsumeExpectingFailureAsync(key, written with { Sha256 = other });

        Assert.Equal(StorageErrorCodes.HashMismatch, failure.Error.Code);
        Assert.Equal(written.Sha256, failure.ActualSha256);
    }

    [Fact]
    public async Task DeleteOfAMissingKeySucceeds()
    {
        var result = await Store.DeleteAsync(ObjectKey.Create("conformance/delete/missing"), Cancellation);

        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task DeleteRemovesAnObjectAndIsIdempotent()
    {
        var (key, _) = await WriteAsync("conformance/delete/object", 100, seed: 10);

        var first = await Store.DeleteAsync(key, Cancellation);
        var info = await Store.GetInfoAsync(key, Cancellation);
        var second = await Store.DeleteAsync(key, Cancellation);

        Assert.True(first.Value);
        Assert.Equal(StorageOutcome.NotFound, info.Outcome);
        Assert.Equal(StorageOutcome.Success, second.Outcome);
        Assert.False(second.Value);
    }

    [Fact]
    public async Task ReadAndInfoOfAMissingKeyAreNotFound()
    {
        var key = ObjectKey.Create("conformance/missing/object");

        var read = await Store.OpenReadAsync(key, 0, GeneratedContent.Sha256Of(0, 0), Cancellation);
        var info = await Store.GetInfoAsync(key, Cancellation);

        Assert.Equal(StorageOutcome.NotFound, read.Outcome);
        Assert.Equal(StorageErrorCodes.NotFound, read.Error!.Code);
        Assert.Null(read.Value);
        Assert.Equal(StorageOutcome.NotFound, info.Outcome);
    }

    [Fact]
    public async Task InfoReportsTheSizeAndALastModifiedTimeInUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var (key, written) = await WriteAsync("conformance/info/object", 4_096, seed: 11);
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        var info = await Store.GetInfoAsync(key, Cancellation);

        Assert.Equal(StorageOutcome.Success, info.Outcome);
        Assert.Equal(key, info.Value!.Key);
        Assert.Equal(written.Size, info.Value.Size);
        Assert.Equal(TimeSpan.Zero, info.Value.LastModified.Offset);
        Assert.InRange(info.Value.LastModified, before, after);
    }

    [Fact]
    public async Task ListingIsCompleteInOrdinalOrderAndFreeOfDuplicates()
    {
        // Names chosen so that a directory sorts against a file of a similar name ('/' sorts after '.' and '-').
        var keys = new List<string> { "conformance/list/a/b", "conformance/list/a.x", "conformance/list/a-b", "conformance/list/a0" };
        for (var i = 0; keys.Count < 600; i++)
        {
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"conformance/list/group-{i % 7}/object-{i:d4}"));
        }

        foreach (var key in keys)
        {
            await WriteAsync(key, 16, seed: 12);
        }

        await WriteAsync("conformance/other/object", 16, seed: 13);

        var listed = new List<ObjectInfo>();
        await foreach (var item in Store.ListAsync("conformance/list", Cancellation))
        {
            listed.Add(item);
        }

        var expected = keys.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, listed.Select(i => i.Key.ToString()));
        Assert.All(listed, i => Assert.Equal(16, i.Size));
    }

    [Fact]
    public async Task ListingOfAPrefixWithoutObjectsIsEmpty()
    {
        var listed = new List<ObjectInfo>();
        await foreach (var item in Store.ListAsync("conformance/nothing-here", Cancellation))
        {
            listed.Add(item);
        }

        Assert.Empty(listed);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("conformance/*")]
    [InlineData("/conformance")]
    [InlineData("")]
    public async Task ListingRefusesAPrefixOutsideTheKeyGrammar(string prefix) =>
        await Assert.ThrowsAsync<InvalidObjectKeyException>(async () =>
        {
            await foreach (var unused in Store.ListAsync(prefix, Cancellation))
            {
                Assert.Fail("A refused prefix lists nothing.");
            }
        });

    [Fact]
    public async Task LargeNonSeekableSourceIsWrittenWithBoundedMemory()
    {
        var small = await MeasureWriteAsync("conformance/memory/small", SmallSource);
        var large = await MeasureWriteAsync("conformance/memory/large", LargeSource);

        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"Peak working-set growth: 64 MiB source {small.Growth / 1024.0 / 1024.0:F1} MiB in {small.Elapsed.TotalSeconds:F1} s; " +
            $"1 GiB source {large.Growth / 1024.0 / 1024.0:F1} MiB in {large.Elapsed.TotalSeconds:F1} s; " +
            $"bound: 1 GiB growth <= 64 MiB growth + 32 MiB.");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("memory-figure", figure);
        Assert.True(large.Growth <= small.Growth + MemoryAllowance, figure);
    }

    [Theory]
    [InlineData("..", "a segment is '.' or '..'")]
    [InlineData("a/../b", "a segment is '.' or '..'")]
    [InlineData("/etc/passwd", "the key starts or ends with '/'")]
    [InlineData("c:\\windows", "the key contains a backslash")]
    [InlineData("c:", "the key contains ':'")]
    [InlineData("CON", "a segment contains an upper-case letter")]
    [InlineData("con", "a segment is a reserved device name")]
    [InlineData("lpt1.txt", "a segment is a reserved device name")]
    [InlineData("caf\u00e9", "the key contains a character outside ASCII")]
    [InlineData("a//b", "the key has an empty segment")]
    [InlineData("a b", "a segment contains a character other than a-z, 0-9, '.', '_' or '-'")]
    [InlineData("-a", "a segment does not start and end with a lower-case letter or a digit")]
    public void KeyGrammarRefusesTraversalAndForeignCharacters(string value, string reason)
    {
        Assert.False(ObjectKey.TryCreate(value, out _, out var first));
        Assert.False(ObjectKey.TryCreate(value, out _, out var second));

        Assert.Equal(reason, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void KeyGrammarRefusesKeysBeyondItsLengthAndSegmentLimits()
    {
        var longSegment = new string('a', ObjectKey.MaxSegmentLength + 1);
        var tooManySegments = string.Join('/', Enumerable.Repeat("a", ObjectKey.MaxSegments + 1));
        var tooLong = string.Join('/', Enumerable.Repeat(new string('b', 127), 4)) + "/b";

        Assert.False(ObjectKey.TryCreate(longSegment, out _, out var segmentReason));
        Assert.False(ObjectKey.TryCreate(tooManySegments, out _, out var countReason));
        Assert.False(ObjectKey.TryCreate(tooLong, out _, out var lengthReason));

        Assert.Equal("a segment is longer than 128 characters", segmentReason);
        Assert.Equal("the key has more than 16 segments", countReason);
        Assert.Equal(513, tooLong.Length);
        Assert.Equal("the key is longer than 512 characters", lengthReason);
    }

    [Fact]
    public async Task NoKeyPathUrlOrCredentialAppearsInLogsOrErrors()
    {
        var key = ObjectKey.FromSegments("conformance", PlantedSegment, "object");
        var missing = ObjectKey.FromSegments("conformance", PlantedSegment, "missing");
        var (_, written) = await WriteAsync(key.ToString(), 1_000, seed: 14);
        var texts = new List<string>();

        var again = await Store.WriteAsync(key, new GeneratedContent(10, seed: 15), Cancellation);
        texts.Add(again.Error!.Message);
        var read = await Store.OpenReadAsync(missing, 1, written.Sha256, Cancellation);
        texts.Add(read.Error!.Message);
        var info = await Store.GetInfoAsync(missing, Cancellation);
        texts.Add(info.Error!.Message);
        var failure = await ConsumeExpectingFailureAsync(key, written with { Size = 999 });
        texts.Add(failure.Message);
        texts.Add(failure.ToString());
        var keyError = Assert.Throws<InvalidObjectKeyException>(() => ObjectKey.Create(PlantedSegment.ToUpperInvariant()));
        texts.Add(keyError.Message);
        texts.AddRange(Root.Logs.Lines);

        Assert.NotEmpty(Root.Logs.Lines);
        foreach (var text in texts)
        {
            Assert.DoesNotContain(PlantedSegment, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Root.Path, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedUrlsGrantOneKeyAndOneVerbWhereDeclared()
    {
        if (!ExpectedCapabilities.SignedUpload && !ExpectedCapabilities.SignedDownload)
        {
            Assert.False(Store is ISignedUrlStore, "A provider that declares no signed URL capability must not offer signed URLs.");
            Assert.Skip("The provider declares neither signed uploads nor signed downloads; there are no signed URLs to check.");
        }

        var signer = Assert.IsAssignableFrom<ISignedUrlStore>(Store);
        await VerifySignedUrlsAsync(signer);
    }

    [Fact]
    public async Task WriteWhoseSourceFailsLeavesNoObject()
    {
        var key = ObjectKey.Create("conformance/failed-source/object");

        await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync(key, new GeneratedContent(100_000, seed: 16, failAfter: 30_000), Cancellation));

        Assert.Equal(StorageOutcome.NotFound, (await Store.GetInfoAsync(key, Cancellation)).Outcome);
    }

    [Fact]
    public async Task CancelledWriteLeavesNoObject()
    {
        var key = ObjectKey.Create("conformance/cancelled/object");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.WriteAsync(key, new GeneratedContent(100_000, seed: 17), cancelled.Token));

        Assert.Equal(StorageOutcome.NotFound, (await Store.GetInfoAsync(key, Cancellation)).Outcome);
    }

    /// <summary>Creates the provider's store over <paramref name="root"/>.</summary>
    protected abstract IObjectStore CreateStore(TemporaryRoot root);

    /// <summary>Replaces the stored bytes of <paramref name="key"/> with <paramref name="change"/>'s result, behind the store's back.</summary>
    protected abstract Task TamperAsync(TemporaryRoot root, ObjectKey key, Func<byte[], byte[]> change);

    /// <summary>
    /// The signed URL cases of a provider that declares a signed URL
    /// capability: a URL grants exactly one key and one verb, and expires. A
    /// provider that declares the capability overrides this.
    /// </summary>
    protected virtual Task VerifySignedUrlsAsync(ISignedUrlStore signer) =>
        throw new NotImplementedException("A provider that declares signed URLs must supply its signed URL cases.");

    /// <summary>Writes generated content of <paramref name="length"/> bytes at <paramref name="key"/>; asserts success.</summary>
    protected async Task<(ObjectKey Key, ObjectWrite Written)> WriteAsync(string key, long length, ulong seed)
    {
        var objectKey = ObjectKey.Create(key);
        var result = await Store.WriteAsync(objectKey, new GeneratedContent(length, seed), Cancellation);
        Assert.Equal(StorageOutcome.Success, result.Outcome);
        return (objectKey, result.Value!);
    }

    private async Task<string> ReadBackSha256Async(ObjectKey key, long size, string sha256)
    {
        var read = await Store.OpenReadAsync(key, size, sha256, Cancellation);
        Assert.Equal(StorageOutcome.Success, read.Outcome);
        await using var verified = read.Value!;
        Assert.Equal(size, verified.Size);
        Assert.Equal(sha256, verified.Sha256);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(verified.Content, Cancellation));
    }

    private async Task<StorageIntegrityException> ConsumeExpectingFailureAsync(ObjectKey key, ObjectWrite expected)
    {
        var read = await Store.OpenReadAsync(key, expected.Size, expected.Sha256, Cancellation);
        Assert.Equal(StorageOutcome.Success, read.Outcome);
        await using var verified = read.Value!;
        return await Assert.ThrowsAsync<StorageIntegrityException>(() => verified.Content.CopyToAsync(Stream.Null, Cancellation));
    }

    private async Task<(long Growth, TimeSpan Elapsed)> MeasureWriteAsync(string key, long length)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = Environment.WorkingSet;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, Environment.WorkingSet);
                    Thread.Sleep(2);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var elapsed = Stopwatch.StartNew();
        var result = await Store.WriteAsync(ObjectKey.Create(key), new GeneratedContent(length, seed: 18), Cancellation);
        elapsed.Stop();
        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);

        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.Equal(length, result.Value!.Size);
        await Store.DeleteAsync(ObjectKey.Create(key), Cancellation);
        return (peak - baseline, elapsed.Elapsed);
    }
}
