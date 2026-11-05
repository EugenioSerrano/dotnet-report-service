using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>
/// One printed unit per row: a row whose fragment composes no text at all cannot be
/// proven printed, so it fails the block closed instead of passing as "nothing to
/// compare".
/// </summary>
public sealed class RowTextCheck
{
    /// <summary>Checks that every row fragment holds at least one non-empty unit.</summary>
    /// <param name="input">The composition and its units.</param>
    public SourceCheckOutcome Check(SourceCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        for (var fragmentIndex = 0; fragmentIndex < input.Composition.Fragments.Count; fragmentIndex++)
        {
            var fragment = input.Composition.Fragments[fragmentIndex];
            if (fragment.Kind != FragmentKind.Row)
            {
                continue;
            }

            var hasText = input.Text.Units.Any(unit => unit.FragmentIndex == fragmentIndex && unit.Normalised.Length > 0);
            if (!hasText)
            {
                return SourceCheckOutcome.Fail(VerificationReasons.RowWithoutText, fragment.RecordOrdinal);
            }
        }

        return SourceCheckOutcome.Pass;
    }
}
