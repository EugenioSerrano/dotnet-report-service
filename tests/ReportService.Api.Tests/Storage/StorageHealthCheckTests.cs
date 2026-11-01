using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;

namespace ReportService.Api.Tests.Storage;

/// <summary>
/// The storage health check: a probe object written, read back verified and
/// deleted; outcome codes, never keys or paths; one log line per change.
/// </summary>
public sealed class StorageHealthCheckTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly LogCapture _logs = new();

    public void Dispose()
    {
        _logs.Dispose();
        _root.Dispose();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WritableRootIsHealthyAndLeavesNoProbeObject()
    {
        var check = Check(OpenStore());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Cancellation);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("filesystem", result.Data["provider"]);
        Assert.Equal(true, result.Data["writable"]);
        Assert.Equal(true, result.Data["readable"]);
        Assert.True(Directory.Exists(Path.Combine(_root.Path, "staging", "probe", "test-process")));
        Assert.Empty(Directory.EnumerateFiles(_root.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnwritableRootIsUnhealthyAndRecoversWithoutANewCheck()
    {
        var store = OpenStore();
        var check = Check(store);
        var probeDirectory = Path.Combine(_root.Path, "staging", "probe", "test-process");
        Directory.CreateDirectory(probeDirectory);

        HealthCheckResult failed;
        using (new UnwritableDirectory(probeDirectory))
        {
            failed = await check.CheckHealthAsync(new HealthCheckContext(), Cancellation);
            await check.CheckHealthAsync(new HealthCheckContext(), Cancellation);
        }

        var recovered = await check.CheckHealthAsync(new HealthCheckContext(), Cancellation);

        Assert.Equal(HealthStatus.Unhealthy, failed.Status);
        Assert.Equal(StorageErrorCodes.PermissionDenied, failed.Data["error"]);
        Assert.Equal(["provider", "error"], failed.Data.Keys);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.Single(_logs.Lines, l => l.Contains("Object storage check failed: storage.permission-denied", StringComparison.Ordinal));
        Assert.Single(_logs.Lines, l => l.Contains("Object storage check recovered", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(_root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ScriptedStore.Step.Write, StorageErrorCodes.IoError)]
    [InlineData(ScriptedStore.Step.Read, StorageErrorCodes.NotFound)]
    [InlineData(ScriptedStore.Step.Content, StorageErrorCodes.HashMismatch)]
    [InlineData(ScriptedStore.Step.Throw, StorageErrorCodes.Unexpected)]
    public async Task EachFailingStepReportsItsCode(ScriptedStore.Step failing, string code)
    {
        var store = new ScriptedStore(failing);

        var result = await Check(store).CheckHealthAsync(new HealthCheckContext(), Cancellation);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(code, result.Data["error"]);
        Assert.Equal(failing is ScriptedStore.Step.Write or ScriptedStore.Step.Throw ? 0 : 1, store.Deletes);
    }

    [Fact]
    public async Task CancelledProbeIsReportedAsATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await Check(OpenStore()).CheckHealthAsync(new HealthCheckContext(), cancelled.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(StorageErrorCodes.Timeout, result.Data["error"]);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        var store = new ScriptedStore(ScriptedStore.Step.None);
        var logger = NullLogger<StorageHealthCheck>.Instance;

        Assert.Throws<ArgumentNullException>(() => new StorageHealthCheck(null!, "filesystem", "api", logger));
        Assert.Throws<ArgumentException>(() => new StorageHealthCheck(store, " ", "api", logger));
        Assert.Throws<InvalidObjectKeyException>(() => new StorageHealthCheck(store, "filesystem", "Api", logger));
        Assert.Throws<ArgumentNullException>(() => new StorageHealthCheck(store, "filesystem", "api", null!));
    }

    private FilesystemObjectStore OpenStore() => FilesystemObjectStore.Open(
        new FilesystemStorageOptions { Root = _root.Path },
        _root.Path,
        TimeProvider.System,
        NullLogger<FilesystemObjectStore>.Instance);

    private StorageHealthCheck Check(IObjectStore store)
    {
        var factory = new LoggerFactory([_logs]);
        return new StorageHealthCheck(store, "filesystem", "test-process", factory.CreateLogger<StorageHealthCheck>());
    }

    /// <summary>A store whose probe fails at a chosen step.</summary>
    public sealed class ScriptedStore(ScriptedStore.Step failing) : IObjectStore
    {
        private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        /// <summary>The step that fails.</summary>
        public enum Step
        {
            None,
            Write,
            Read,
            Content,
            Throw,
        }

        public int Deletes { get; private set; }

        public StorageCapabilities Capabilities => StorageCapabilities.None;

        public async Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return failing switch
            {
                Step.Write => StorageResult.Failure<ObjectWrite>(StorageOutcome.Transient, new StorageError(StorageErrorCodes.IoError, "failed")),
                Step.Throw => throw new InvalidOperationException("unexpected"),
                _ => StorageResult.Success(new ObjectWrite(key, 3, Sha, null)),
            };
        }

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            Task.FromResult(failing switch
            {
                Step.Read => StorageResult.Failure<VerifiedRead>(StorageOutcome.NotFound, new StorageError(StorageErrorCodes.NotFound, "missing")),
                Step.Content => StorageResult.Success(new VerifiedRead(new MemoryStream("abd"u8.ToArray()), expectedSize, expectedSha256)),
                _ => StorageResult.Success(new VerifiedRead(new MemoryStream("abc"u8.ToArray()), expectedSize, expectedSha256)),
            });

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.FromResult(StorageResult.Success(true));
        }

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
