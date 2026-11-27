using ReportService.Workers.Merge.Assembly;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The assembly's execution settings: the merge queue, the architecture's heartbeat,
/// start-to-close, backoff and attempt budget, the never-retried codes, and the
/// attempt identity that makes one attempt's object key its own.
/// </summary>
public sealed class AssemblyStepOptionsTests
{
    [Fact]
    public void TheQueueAndThePolicyAreTheArchitectures()
    {
        var options = AssemblyStepOptions.Create();

        Assert.Equal("report-pdf-merge", AssemblyStepOptions.Queue);
        Assert.Equal(AssemblyStepOptions.Queue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), AssemblyStepOptions.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), AssemblyStepOptions.StartToClose);
        Assert.Equal(TimeSpan.FromMinutes(15), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), AssemblyStepOptions.HeartbeatEvery);
    }

    [Fact]
    public void TheNeverRetriedCodesAreTheAssemblysOwnRefusals()
    {
        Assert.Equal(
            ["ARTIFACT_INTEGRITY_FAILED", "OUTPUT_LIMIT_EXCEEDED", "TEMPLATE_INVALID", "EXECUTION_FAILED"],
            AssemblyStepOptions.NonRetryableErrorTypes);
        Assert.DoesNotContain("QUOTA_EXCEEDED", AssemblyStepOptions.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheAttemptIdentityIsOneStableDigestPerAttempt()
    {
        var first = AssemblyStepOptions.AttemptId("job", "run", "activity", 1);
        var again = AssemblyStepOptions.AttemptId("job", "run", "activity", 1);
        var second = AssemblyStepOptions.AttemptId("job", "run", "activity", 2);

        Assert.Equal(first, again);
        Assert.NotEqual(first, second);
        Assert.Equal(24, first.Length);
        Assert.All(first, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
    }

    [Fact]
    public void TheTemporaryFolderIsRemovedAndItsQuotaIsThreeTimesTheParts()
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = new AssemblyTemporaryDirectory(root, 1_000);
            Assert.Equal(3_000, folder.QuotaBytes);
            Assert.True(Directory.Exists(folder.Root));
            Assert.Equal(0, folder.CurrentBytes());
            folder.Dispose();
            Assert.False(Directory.Exists(folder.Root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TheTemporaryFolderRefusesMoreThanItsQuota()
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var folder = new AssemblyTemporaryDirectory(root, 1);
            File.WriteAllBytes(Path.Combine(folder.Root, "over.bin"), new byte[folder.QuotaBytes + 1]);

            var refused = Assert.Throws<AssemblyQuotaExceededException>(() => folder.EnsureWithinQuota());

            Assert.Equal(folder.QuotaBytes, refused.LimitBytes);
            Assert.True(refused.ReachedBytes > folder.QuotaBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TheMetricsRecordAVerdictAndTheAbsenceOfOne()
    {
        using var metrics = new AssemblyJobMetrics(ServiceDefaults.MeterIdentity.ForHost("test"));
        var measured = new Pdf.Assembly.AssemblyMetrics();
        measured.Begin([new Pdf.Assembly.AssemblyPart(0, "a.pdf", 1, 1)], new Pdf.Assembly.PageNumbering(
            new Pdf.Assembly.NumberingArea(0, 0, 10, 10),
            new Pdf.Assembly.NumberingFont("f", "fonts/f.ttf", 9, [1, 2, 3]),
            "{X} {Y}",
            "en-US"));

        metrics.Record(measured, null);
        metrics.Record(measured, Pdf.Assembly.AssemblyVerdict.Failed(Pdf.Assembly.AssemblyFailureReasons.Stamp, 0, 1));
        metrics.Record(measured, Pdf.Assembly.AssemblyVerdict.Good);
    }
}
