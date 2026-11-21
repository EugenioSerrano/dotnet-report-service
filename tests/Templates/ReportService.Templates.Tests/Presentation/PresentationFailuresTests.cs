namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The one place a presentation refusal becomes a code and a reason: the three mappings,
/// the record and the function the diagnostic names, and the fact that no value and no
/// format expansion ever reaches it.
/// </summary>
public sealed class PresentationFailuresTests
{
    [Fact]
    public void ANumberWithoutAnExactDecimalIsADataInvalidRefusal()
    {
        var failure = PresentationFailures.Describe(PresentationRefusal.NoDecimal, "format_number", 7);

        Assert.Equal("DATA_INVALID", failure.Code);
        Assert.Equal("number-out-of-range", failure.Reason);
        Assert.Equal("format_number", failure.Function);
        Assert.Equal(7, failure.RecordOrdinal);
    }

    [Fact]
    public void AValueOfAnotherTypeIsADataInvalidRefusal()
    {
        var failure = PresentationFailures.Describe(PresentationRefusal.NotADate, "format_date", 3);

        Assert.Equal("DATA_INVALID", failure.Code);
        Assert.Equal("type-mismatch", failure.Reason);
    }

    [Fact]
    public void AFormatTheRuntimeRejectsIsATemplateInvalidRefusal()
    {
        var failure = PresentationFailures.Describe(PresentationRefusal.BadFormat, "format_date", null);

        Assert.Equal("TEMPLATE_INVALID", failure.Code);
        Assert.Equal("format-string", failure.Reason);
    }

    [Fact]
    public void TheDiagnosticNamesTheCodeTheFunctionAndTheRecordOnly()
    {
        var withRecord = PresentationFailures.Describe(PresentationRefusal.NoDecimal, "num_sign", 12);
        var withoutRecord = PresentationFailures.Describe(PresentationRefusal.BadFormat, "format_number", null);

        Assert.Equal("DATA_INVALID reason=number-out-of-range function=num_sign record=12", withRecord.Message);
        Assert.Equal("TEMPLATE_INVALID reason=format-string function=format_number", withoutRecord.Message);
        Assert.DoesNotContain("0.50", withRecord.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("N2", withoutRecord.Message, StringComparison.Ordinal);
    }
}
