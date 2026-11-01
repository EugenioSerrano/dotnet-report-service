using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ReportService.Storage;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The <c>storage</c> health check: on each probe it writes a small object
/// under <c>staging/probe/&lt;process&gt;/&lt;32 hex&gt;</c>, reads it back
/// through the verifying reader and deletes it, within
/// <see cref="TimeBound"/>. Healthy carries <c>provider</c>, <c>writable</c>
/// and <c>readable</c>; unhealthy carries <c>provider</c> and the
/// <c>error</c> code — never a key or a path. The check never throws; a
/// change to unhealthy is logged once, and so is the recovery.
/// </summary>
public sealed partial class StorageHealthCheck : IHealthCheck
{
    /// <summary>The name the check is registered under.</summary>
    public const string Name = "storage";

    /// <summary>The longest a probe may take.</summary>
    public static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(5);

    private const int ProbeSize = 64;
    private const int Unknown = 0;
    private const int Healthy = 1;
    private const int Unhealthy = 2;

    private readonly IObjectStore _store;
    private readonly string _provider;
    private readonly string _processSegment;
    private readonly ILogger<StorageHealthCheck> _logger;
    private int _state = Unknown;

    /// <summary>Creates the check for <paramref name="store"/>; probe objects go under <paramref name="processSegment"/>.</summary>
    public StorageHealthCheck(IObjectStore store, string provider, string processSegment, ILogger<StorageHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _provider = provider;
        _processSegment = ObjectKey.FromSegments(processSegment).ToString();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? error;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bound.CancelAfter(TimeBound);
            try
            {
                error = await ProbeAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                error = StorageErrorCodes.Timeout;
            }
            catch (StorageException exception)
            {
                error = exception.Error.Code;
            }
            catch (Exception)
            {
                // A health check reports; it never throws.
                error = StorageErrorCodes.Unexpected;
            }
        }

        if (error is null)
        {
            if (Interlocked.Exchange(ref _state, Healthy) == Unhealthy)
            {
                LogRecovered();
            }

            return HealthCheckResult.Healthy(data: new Dictionary<string, object>
            {
                ["provider"] = _provider,
                ["writable"] = true,
                ["readable"] = true,
            });
        }

        if (Interlocked.Exchange(ref _state, Unhealthy) != Unhealthy)
        {
            LogFailed(error);
        }

        return HealthCheckResult.Unhealthy(data: new Dictionary<string, object>
        {
            ["provider"] = _provider,
            ["error"] = error,
        });
    }

    // Returns null when the probe object was written, read back verified and deleted; otherwise the error code.
    private async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        var key = ObjectKey.FromSegments("staging", "probe", _processSegment, RandomNumberGenerator.GetHexString(32, lowercase: true));
        StorageResult<ObjectWrite> write;
        await using (var content = new ProbeContent(ProbeSize))
        {
            write = await _store.WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
        }

        if (!write.Succeeded)
        {
            return write.Error!.Code;
        }

        try
        {
            var read = await _store.OpenReadAsync(key, write.Value!.Size, write.Value.Sha256, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return read.Error!.Code;
            }

            await using var verified = read.Value!;
            await verified.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            await _store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Object storage check failed: {Code}")]
    private partial void LogFailed(string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Object storage check recovered")]
    private partial void LogRecovered();

    // A small, generated, read-only content for the probe object.
    private sealed class ProbeContent(int length) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, length - _position);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)('a' + ((_position + i) % 26));
            }

            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
