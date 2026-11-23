using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The size limits verification enforces and the deadline an upload's verification
/// is given: each is required and bounded, a refusal names the setting and the rule
/// and never the value, and the deadline is refused when it is shorter than one
/// attempt of the verification it has to cover.
/// </summary>
public sealed class VerificationOptionsTests
{
    private static VerificationOptions Valid() => new()
    {
        MaxStoredBytes = 1,
        MaxUncompressedBytes = 1,
        VerificationDeadline = TimeSpan.FromHours(6),
    };

    [Fact]
    public void APositiveLimitOfEachKindIsValid()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AStoredLimitThatIsNotPositiveIsRefusedByName(long value)
    {
        var options = Valid();
        options.MaxStoredBytes = value;

        var problem = Assert.Single(options.Validate());
        Assert.Equal("MaxStoredBytes", problem.Setting);
        Assert.DoesNotContain(value.ToString(), problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ADecompressedLimitThatIsNotPositiveIsRefusedByName(long value)
    {
        var options = Valid();
        options.MaxUncompressedBytes = value;

        var problem = Assert.Single(options.Validate());
        Assert.Equal("MaxUncompressedBytes", problem.Setting);
        Assert.DoesNotContain(value.ToString(), problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySettingMissingIsReportedTogether()
    {
        var options = new VerificationOptions();

        Assert.Equal(3, options.Validate().Count());
    }

    [Fact]
    public void ADeadlineLongerThanOneAttemptFits()
    {
        var options = new UploadVerificationOptions { StartToClose = TimeSpan.FromHours(1) };

        Assert.Null(options.DeadlineProblem(TimeSpan.FromHours(6)));
        Assert.Null(options.DeadlineProblem(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ADeadlineShorterThanOneAttemptIsRefusedNamingBothSettings()
    {
        var options = new UploadVerificationOptions { StartToClose = TimeSpan.FromHours(2) };

        var problem = options.DeadlineProblem(TimeSpan.FromMinutes(30));

        Assert.NotNull(problem);
        Assert.Equal("Uploads:VerificationDeadline", problem!.Setting);
        Assert.Contains("Uploads:Verification:StartToClose", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("00:30:00", problem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("02:00:00", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultDeadlineIsSixHoursAndCoversTheDefaultAttempt()
    {
        var options = new UploadVerificationOptions { StartToClose = TimeSpan.FromHours(1) };

        Assert.Null(options.DeadlineProblem(TimeSpan.FromHours(6)));
    }
}
