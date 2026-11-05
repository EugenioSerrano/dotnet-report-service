using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>
/// The source anchors: every field a package prints as it is must appear at its
/// value, unchanged, in the text its own row (or its document parts, for a header
/// field) prints. A composition defect that alters a value consistently on both
/// sides of the printed-text check passes that check - an escaping, a truncation, a
/// context that cuts a value - and this one catches it, for every field the package
/// declares. Missing values, nulls, booleans, objects and arrays are not checked: a
/// package that declares one declares something no text can carry.
/// </summary>
public sealed class AnchorCheck
{
    private const string RootPrefix = "$.";

    /// <summary>Checks every declared field against the text of its own fragment.</summary>
    /// <param name="input">The composition, its units, the block's records and its header.</param>
    public SourceCheckOutcome Check(SourceCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Anchors is null)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.AnchorsUndeclared);
        }

        if (input.IsFirstBlock)
        {
            var documentText = DocumentText(input);
            foreach (var path in input.Anchors.Header)
            {
                var outcome = CheckValue(input.Block.Header, path, documentText, recordOrdinal: 0);
                if (!outcome.Passed)
                {
                    return outcome;
                }
            }
        }

        foreach (var record in input.Block.Records)
        {
            var rowText = RowText(input, record.Ordinal);
            if (rowText is null)
            {
                continue;
            }

            foreach (var path in input.Anchors.Item)
            {
                var outcome = CheckValue(record.Values, path, rowText, record.Ordinal);
                if (!outcome.Passed)
                {
                    return outcome;
                }
            }
        }

        return SourceCheckOutcome.Pass;
    }

    private static SourceCheckOutcome CheckValue(
        IReadOnlyDictionary<string, object?> values,
        string path,
        string fragmentText,
        long recordOrdinal)
    {
        if (!RenderContextFactory.TryResolve(values, Trim(path), out var value))
        {
            return SourceCheckOutcome.Pass;
        }

        if (value is not string text || text.Length == 0)
        {
            return SourceCheckOutcome.Pass;
        }

        var expected = Normalisation.Normalise(text);
        return expected.Length == 0 || fragmentText.Contains(expected, StringComparison.Ordinal)
            ? SourceCheckOutcome.Pass
            : SourceCheckOutcome.Fail(VerificationReasons.AnchorMismatch, recordOrdinal, path);
    }

    private static string Trim(string path) =>
        path.StartsWith(RootPrefix, StringComparison.Ordinal) ? path[RootPrefix.Length..] : path;

    /// <summary>The text of the document parts: the document's own start and end, groups excluded.</summary>
    private static string DocumentText(SourceCheckInput input)
    {
        var builder = new System.Text.StringBuilder();
        for (var fragmentIndex = 0; fragmentIndex < input.Composition.Fragments.Count; fragmentIndex++)
        {
            var kind = input.Composition.Fragments[fragmentIndex].Kind;
            if (kind is not (FragmentKind.DocumentStart or FragmentKind.DocumentEnd))
            {
                continue;
            }

            foreach (var unit in input.Text.Units)
            {
                if (unit.FragmentIndex == fragmentIndex)
                {
                    builder.Append(unit.Normalised);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>The text of one record's row, or <see langword="null"/> when the block does not print it.</summary>
    private static string? RowText(SourceCheckInput input, long recordOrdinal)
    {
        var builder = new System.Text.StringBuilder();
        var found = false;
        for (var fragmentIndex = 0; fragmentIndex < input.Composition.Fragments.Count; fragmentIndex++)
        {
            var fragment = input.Composition.Fragments[fragmentIndex];
            if (fragment.Kind != FragmentKind.Row || fragment.RecordOrdinal != recordOrdinal)
            {
                continue;
            }

            found = true;
            foreach (var unit in input.Text.Units)
            {
                if (unit.FragmentIndex == fragmentIndex)
                {
                    builder.Append(unit.Normalised);
                }
            }
        }

        return found ? builder.ToString() : null;
    }
}
