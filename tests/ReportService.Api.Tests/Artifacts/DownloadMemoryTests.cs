using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Tests.Jobs;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;

namespace ReportService.Api.Tests.Artifacts;

/// <summary>
/// The bound a download has to keep: a gibibyte is delivered with a working set that grows
/// no more than a sixty-four mebibyte download's growth plus thirty-two mebibytes. Each case
/// writes its object through the storage port from a generated body that is never held
/// whole, downloads it through the API's own pipeline with a client that hashes the bytes
/// and discards them, and removes its storage root afterwards. The figures — the two
/// growths, the elapsed times and the peak temporary storage — are reported in the case's
/// output, so the record of a run carries them.
/// </summary>
public sealed class DownloadMemoryTests : IDisposable
{
    /// <summary>The most the large object's growth may exceed the small object's: thirty-two mebibytes.</summary>
    public const long MemoryAllowance = 32L * 1024 * 1024;

    private const long SmallBody = 64L * 1024 * 1024;

    private const long LargeBody = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly List<JobApiHost> _hosts = [];

    [Fact]
    public async Task ADownloadOfOneGibibyteGrowsTheApiNoMoreThanTheSmallOnePlusThirtyTwoMebibytes()
    {
        var small = await MeasureAsync(SmallBody);
        var large = await MeasureAsync(LargeBody);

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Download of a stored artifact — peak working-set growth: 64 MiB object {small.Growth / 1024.0 / 1024.0:F1} MiB "
            + $"in {small.Elapsed.TotalSeconds:F1} s with a peak of {small.PeakDisk / 1024.0 / 1024.0:F1} MiB of temporary storage; "
            + $"1 GiB object {large.Growth / 1024.0 / 1024.0:F1} MiB in {large.Elapsed.TotalSeconds:F1} s with a peak of "
            + $"{large.PeakDisk / 1024.0 / 1024.0:F1} MiB of temporary storage; bound: the 1 GiB download's growth is at most "
            + $"the 64 MiB download's + 32 MiB.");
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        TestContext.Current.AddAttachment("download-memory", text);

        Assert.Equal(LargeBody, large.Bytes);
        Assert.True(large.Growth <= small.Growth + MemoryAllowance, text);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<Figure> MeasureAsync(long bodyBytes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = new JobApiHost();
        _hosts.Add(host);
        var (jobId, outputId) = await AcceptedAsync(host, cancellationToken);

        // The object is generated into the store, so neither the test nor the API ever holds it.
        var objectKey = $"outputs/{jobId}/{outputId}/{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        var expectedHash = GeneratedBodyStream.Sha256Of(bodyBytes);
        using (var generated = new GeneratedBodyStream(bodyBytes))
        {
            var written = await host.Objects.WriteAsync(ObjectKey.Create(objectKey), generated, cancellationToken);
            Assert.True(written.Succeeded, $"the case's object had to be stored: {written.Outcome}");
            Assert.Equal(bodyBytes, written.Value!.Size);
            Assert.Equal(expectedHash, written.Value.Sha256);
        }

        Assert.Equal(TransitionOutcome.Applied, await host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken));
        var artifact = new ArtifactRecord(
            ArtifactIdentifier.Create(),
            jobId,
            outputId,
            OutputFormat.Pdf,
            "application/pdf",
            objectKey,
            expectedHash,
            bodyBytes,
            null,
            Now,
            Now.Add(Retention));
        Assert.Equal(PublishOutcome.Published, (await host.Jobs.PublishAsync(artifact, cancellationToken)).Outcome);

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
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{jobId}{ArtifactListEndpoint.RouteSuffix}/{artifact.ArtifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, JobApiHost.Consumer);
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bodyBytes, response.Content.Headers.ContentLength);

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var received = 0L;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                received += read;
            }
        }

        elapsed.Stop();
        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);

        Assert.Equal(bodyBytes, received);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(hash.GetHashAndReset()));

        return new Figure(peak - baseline, elapsed.Elapsed, PeakDiskOf(host.StorageRoot), received);
    }

    private static async Task<(string JobId, string OutputId)> AcceptedAsync(JobApiHost host, CancellationToken cancellationToken)
    {
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            JobApiHost.Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        Assert.IsType<JobAcceptanceResult.Accepted>(await host.Jobs.AcceptAsync(acceptance, Now, cancellationToken));
        return (acceptance.ReportJobId, acceptance.OutputId);
    }

    private static long PeakDiskOf(string root)
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
                    // A file the store removed between the listing and the read.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The root is being emptied as the case ends.
        }

        return total;
    }

    /// <summary>What one download of one object size cost.</summary>
    /// <param name="Growth">The peak working-set growth over the baseline, in bytes.</param>
    /// <param name="Elapsed">How long the download took.</param>
    /// <param name="PeakDisk">The most the storage root held, in bytes.</param>
    /// <param name="Bytes">The bytes the client received.</param>
    private sealed record Figure(long Growth, TimeSpan Elapsed, long PeakDisk, long Bytes);
}
