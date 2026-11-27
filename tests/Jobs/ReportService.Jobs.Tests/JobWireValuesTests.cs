using ReportService.Jobs;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The text forms of a job's values: every value the record store can hold is
/// written and read back, an unknown text is refused instead of defaulted, and the
/// answer text of a state is the word a consumer reads.
/// </summary>
public sealed class JobWireValuesTests
{
    [Fact]
    public void EveryJobStatusRoundTripsThroughItsStoredText()
    {
        foreach (var status in Enum.GetValues<ReportJobStatus>())
        {
            Assert.Equal(status, JobWireValues.ParseStoredStatus(JobWireValues.ToStoredText(status)));
        }
    }

    [Fact]
    public void EveryOutputStatusFormatAndReferenceFormRoundTripsThroughItsStoredText()
    {
        foreach (var status in Enum.GetValues<OutputStatus>())
        {
            Assert.Equal(status, JobWireValues.ParseStoredOutputStatus(JobWireValues.ToStoredText(status)));
        }

        foreach (var format in Enum.GetValues<OutputFormat>())
        {
            Assert.Equal(format, JobWireValues.ParseStoredFormat(JobWireValues.ToStoredText(format)));
        }

        foreach (var form in Enum.GetValues<TemplateReferenceForm>())
        {
            Assert.Equal(form, JobWireValues.ParseStoredForm(JobWireValues.ToStoredText(form)));
        }
    }

    [Fact]
    public void TheStoredTextOfAQueuedJobAndOfAPdfOutputIsTheDomainWord()
    {
        Assert.Equal("QUEUED", JobWireValues.ToStoredText(ReportJobStatus.Queued));
        Assert.Equal("PDF", JobWireValues.ToStoredText(OutputFormat.Pdf));
        Assert.Equal("PENDING", JobWireValues.ToStoredText(OutputStatus.Pending));
        Assert.Equal("EXACT", JobWireValues.ToStoredText(TemplateReferenceForm.Exact));
    }

    [Fact]
    public void AnAnswerNamesTheQueuedStateInLowerCase() => Assert.Equal("queued", JobWireValues.ToWire(ReportJobStatus.Queued));

    [Fact]
    public void EveryStageRoundTripsThroughItsStoredTextAndTheWaitingStageIsTheInputWaitStage()
    {
        foreach (var stage in Enum.GetValues<ReportJobStage>())
        {
            Assert.Equal(stage, JobWireValues.ParseStoredStage(JobWireValues.ToStoredText(stage)));
        }

        // The waiting stage the delivered input wait writes is one value of the stage set,
        // so a row it wrote reads back as the same step.
        Assert.Equal(InputWaitStage.WaitingForInput, JobWireValues.ToStoredText(ReportJobStage.WaitingForInput));
        Assert.Equal(ReportJobStage.WaitingForInput, JobWireValues.ParseStoredStage(InputWaitStage.WaitingForInput));
    }

    [Fact]
    public void EveryStageOutputStatusAndFormatHasAnAnswerTextOfItsOwn()
    {
        var stageTexts = Enum.GetValues<ReportJobStage>().Select(JobWireValues.ToWire).ToList();
        Assert.Equal(Enum.GetValues<ReportJobStage>().Length, stageTexts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(stageTexts, text => Assert.Equal(text.ToLowerInvariant(), text));
        Assert.Equal("validating-data", JobWireValues.ToWire(ReportJobStage.ValidatingData));
        Assert.Equal("rendering-pdf", JobWireValues.ToWire(ReportJobStage.RenderingPdf));

        Assert.Equal(
            ["pending", "running", "succeeded", "failed", "cancelled"],
            Enum.GetValues<OutputStatus>().Select(JobWireValues.ToWire).ToArray());
        Assert.Equal(
            ["pdf", "html", "xlsx", "markdown"],
            Enum.GetValues<OutputFormat>().Select(JobWireValues.ToWire).ToArray());
    }

    [Fact]
    public void AValueOutsideTheEnumerationsHasNoAnswerTextAndNoStoredTextIsDefaulted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobWireValues.ToWire((ReportJobStage)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobWireValues.ToStoredText((ReportJobStage)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobWireValues.ToWire((OutputStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobWireValues.ToWire((OutputFormat)99));
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredStage("validating-data"));
    }

    [Fact]
    public void EveryJobStatusHasAnAnswerTextOfItsOwn()
    {
        var texts = Enum.GetValues<ReportJobStatus>().Select(JobWireValues.ToWire).ToList();

        Assert.Equal(Enum.GetValues<ReportJobStatus>().Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(texts, text => Assert.Equal(text.ToLowerInvariant(), text));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("")]
    [InlineData("running")]
    public void AnUnknownStoredStatusIsRefused(string text) =>
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredStatus(text));

    [Fact]
    public void AnUnknownStoredOutputValueIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredOutputStatus("queued"));
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredFormat("pdf"));
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredForm("exact"));
    }

    /// <summary>
    /// The tri-state terminal status ADR-048 adds (US-008.BOLT-002, SPEC-261125-1224 §8.1,
    /// Phase D.2): the wire form is the hyphenated multi-word word and the stored form is the
    /// UPPER_SNAKE word, both SPEC-fixed; the stored form round-trips, and a partially-succeeded
    /// job is the mixed-outcome state, never failed or succeeded. The spellings are the approved
    /// intent (SPEC §6 D.2), never the implementation (G06).
    /// </summary>
    [Fact]
    public void ThePartiallySucceededStatusCarriesItsSpecFixedWireAndStoredForms()
    {
        Assert.Equal("partially-succeeded", JobWireValues.ToWire(ReportJobStatus.PartiallySucceeded));
        Assert.Equal("PARTIALLY_SUCCEEDED", JobWireValues.ToStoredText(ReportJobStatus.PartiallySucceeded));
        Assert.Equal(ReportJobStatus.PartiallySucceeded, JobWireValues.ParseStoredStatus("PARTIALLY_SUCCEEDED"));
    }

    [Fact]
    public void AnInstantIsTruncatedToWholeMillisecondsInUtc()
    {
        var instant = new DateTimeOffset(2026, 11, 1, 4, 41, 2, 456, TimeSpan.FromHours(-3)).AddTicks(9876);

        var truncated = JobWireValues.TruncateToMillisecond(instant);

        Assert.Equal(TimeSpan.Zero, truncated.Offset);
        Assert.Equal(0, truncated.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMillisecond)), truncated);
    }

    [Theory]
    [InlineData(JobPriority.Low, "low")]
    [InlineData(JobPriority.Normal, "normal")]
    [InlineData(JobPriority.High, "high")]
    public void EveryPriorityRoundTripsThroughItsText(JobPriority priority, string text)
    {
        Assert.Equal(text, JobWireValues.ToWire(priority));
        Assert.True(JobWireValues.TryParsePriority(text, out var read));
        Assert.Equal(priority, read);
        Assert.Equal(priority, JobWireValues.ParseStoredPriority(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("High")]
    [InlineData("critical")]
    [InlineData(" low")]
    public void AnyOtherTextIsNoPriority(string? text)
    {
        Assert.False(JobWireValues.TryParsePriority(text, out var read));
        Assert.Equal(JobPriority.Normal, read);
    }

    [Fact]
    public void AStoredPriorityThisBuildDoesNotKnowIsRefusedInsteadOfDefaulted() =>
        Assert.Throws<InvalidOperationException>(() => JobWireValues.ParseStoredPriority("urgent"));

    [Fact]
    public void APriorityOutsideTheThreeHasNoText() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JobWireValues.ToWire((JobPriority)99));

    [Fact]
    public void AnAcceptanceAndARecordDefaultToNormalForBothFacts()
    {
        var record = new ReportJobRecord("job", "consumer", "upload", ReportJobStatus.Queued, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal(JobPriority.Normal, record.DeclaredPriority);
        Assert.Equal(JobPriority.Normal, record.EffectivePriorityClass);
    }
}
