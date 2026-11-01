using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The bound the verification itself has to keep: a generated body of one gibibyte
/// is verified with a working set that grows no more than a sixty-four mebibyte
/// body's growth plus thirty-two mebibytes — plain, and compressed while it is
/// written — with the bytes produced block by block and never held whole. There is
/// no web host in this path, so nothing here can stand between the case and the
/// verifier's own memory. Each case writes and removes its own storage root.
/// </summary>
public sealed class UploadVerificationMemoryTests : IDisposable
{
    /// <summary>The most the large body's growth may exceed the small body's: thirty-two mebibytes.</summary>
    public const long MemoryAllowance = 32L * 1024 * 1024;

    private const long SmallBody = 64L * 1024 * 1024;

    private const long LargeBody = 1024L * 1024 * 1024;

    private const string ConsumerId = "dev-consumer";

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private readonly List<string> _roots = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APlainBodyOfOneGibibyteIsVerifiedWithBoundedMemory()
    {
        var small = await MeasureAsync(SmallBody, UploadContentEncoding.None, "upload-00000000000000000000000a0001");
        var large = await MeasureAsync(LargeBody, UploadContentEncoding.None, "upload-00000000000000000000000b0001");

        AssertWithinTheBound("plain", small, large);
    }

    [Fact]
    public async Task ACompressedBodyOfOneGibibyteIsVerifiedWithBoundedMemory()
    {
        var small = await MeasureAsync(SmallBody, UploadContentEncoding.Gzip, "upload-00000000000000000000000a0002");
        var large = await MeasureAsync(LargeBody, UploadContentEncoding.Gzip, "upload-00000000000000000000000b0002");

        AssertWithinTheBound("compressed", small, large);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }

        GC.SuppressFinalize(this);
    }

    private static void AssertWithinTheBound(string form, Figure small, Figure large)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Verification of a {form} body — peak working-set growth: 64 MiB body {small.Growth / 1024.0 / 1024.0:F1} MiB "
            + $"in {small.Elapsed.TotalSeconds:F1} s with a peak of {small.PeakDisk / 1024.0 / 1024.0:F1} MiB of temporary storage; "
            + $"1 GiB body {large.Growth / 1024.0 / 1024.0:F1} MiB in {large.Elapsed.TotalSeconds:F1} s with a peak of "
            + $"{large.PeakDisk / 1024.0 / 1024.0:F1} MiB of temporary storage; bound: the 1 GiB body's growth is at most the "
            + $"64 MiB body's + 32 MiB.");
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        TestContext.Current.AddAttachment($"verification-memory-{form}", text);

        Assert.True(large.Growth <= small.Growth + MemoryAllowance, text);
        Assert.Equal(LargeBody, large.Uncompressed);
    }

    private async Task<Figure> MeasureAsync(long bodyBytes, UploadContentEncoding encoding, string uploadId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        using var metrics = new OrchestrationMetrics();
        var filesystem = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new InMemoryUploadStore();
        var verifier = new UploadVerifier(
            uploads,
            filesystem,
            new VerificationOptions { MaxStoredBytes = 4L * 1024 * 1024 * 1024, MaxUncompressedBytes = 4L * 1024 * 1024 * 1024 },
            TimeProvider.System,
            NullLogger<UploadVerifier>.Instance,
            metrics);

        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var staged = new GeneratedBodyStream(bodyBytes);
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024));
        Task compression = Task.CompletedTask;
        Stream source = staged;
        if (encoding == UploadContentEncoding.Gzip)
        {
            // Compressed as it is written: the generated body goes through the compressor
            // into a bounded pipe, so neither the body nor its compressed form is ever held whole.
            compression = Task.Run(
                async () =>
                {
                    var writer = pipe.Writer;
                    try
                    {
                        using (var gzip = new GZipStream(writer.AsStream(), CompressionLevel.Fastest, leaveOpen: true))
                        {
                            await staged.CopyToAsync(gzip, CancellationToken.None);
                        }

                        await writer.CompleteAsync();
                    }
                    catch (Exception exception)
                    {
                        await writer.CompleteAsync(exception);
                        throw;
                    }
                },
                CancellationToken.None);
            source = pipe.Reader.AsStream();
        }

        var written = await filesystem.WriteAsync(stagingKey, source, Cancellation);
        await source.DisposeAsync();
        await compression;

        Assert.True(written.Succeeded, "the body of the case had to be staged");
        var record = new UploadRecord(
            uploadId,
            ConsumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            CreatedAt,
            CreatedAt.AddDays(1));
        await uploads.CreateAsync(record, Cancellation);
        await uploads.TryStartTransferAsync(uploadId, ConsumerId, encoding, Cancellation);
        await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            bodyBytes,
            written.Value!.Size,
            written.Value.Sha256,
            Cancellation);
        await uploads.ConfirmAsync(uploadId, ConsumerId, null, ConfirmedAt, Cancellation);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = Environment.WorkingSet;
        var peak = baseline;
        var peakDisk = TotalBytes(root);
        using var stop = new CancellationTokenSource();
        var sampler = Task.Factory.StartNew(
            () =>
            {
                var tick = 0;
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, Environment.WorkingSet);
                    if (tick++ % 10 == 0)
                    {
                        peakDisk = Math.Max(peakDisk, TotalBytes(root));
                    }

                    Thread.Sleep(2);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var elapsed = Stopwatch.StartNew();
        var outcome = await verifier.VerifyAsync(
            uploadId,
            ObjectKey.FromSegments("snapshots", uploadId, "attempt"),
            null,
            Cancellation);
        elapsed.Stop();
        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);
        peakDisk = Math.Max(peakDisk, TotalBytes(root));

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(bodyBytes, outcome.Snapshot!.SizeUncompressed);
        Assert.Equal(GeneratedBodyStream.Sha256Of(bodyBytes), outcome.Snapshot.Sha256Uncompressed);

        return new Figure(peak - baseline, elapsed.Elapsed, peakDisk, outcome.Snapshot.SizeUncompressed);
    }

    private static long TotalBytes(string root)
    {
        var total = 0L;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (FileNotFoundException)
                {
                    // A file the verification removed between the listing and the read.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The root is being emptied as the case ends.
        }

        return total;
    }

    /// <summary>What one verification of one body size cost.</summary>
    /// <param name="Growth">The peak working-set growth over the baseline, in bytes.</param>
    /// <param name="Elapsed">How long the verification took.</param>
    /// <param name="PeakDisk">The most the storage root held at once, in bytes.</param>
    /// <param name="Uncompressed">The size of the content the snapshot recorded.</param>
    private sealed record Figure(long Growth, TimeSpan Elapsed, long PeakDisk, long Uncompressed);
}
