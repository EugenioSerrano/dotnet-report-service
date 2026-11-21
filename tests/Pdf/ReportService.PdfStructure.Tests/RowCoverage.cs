namespace ReportService.PdfStructure.Tests;

/// <summary>
/// The row-coverage comparison at the reading level: whether the row keys a
/// document carries are the source's keys, each once and in the source order. It
/// names the first key that is lost and the first that is out of order, so a
/// defect is reported as the specific row it is, never as a bare failure.
/// </summary>
internal static class RowCoverage
{
    /// <summary>
    /// The source keys that <paramref name="printed"/> does not carry, counting
    /// multiplicity: a key the source holds twice and the document once is reported
    /// once. In source order.
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> expected, IReadOnlyList<string> printed)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in printed)
        {
            remaining[key] = remaining.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var missing = new List<string>();
        foreach (var key in expected)
        {
            if (remaining.TryGetValue(key, out var count) && count > 0)
            {
                remaining[key] = count - 1;
            }
            else
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    /// <summary>
    /// The first printed key that breaks the source order, or <see langword="null"/>
    /// when the printed keys appear in the source order. Only meaningful once coverage
    /// holds (no key lost and none extra); the caller checks coverage first.
    /// </summary>
    public static string? FirstOutOfOrder(IReadOnlyList<string> expected, IReadOnlyList<string> printed)
    {
        for (var index = 0; index < expected.Count && index < printed.Count; index++)
        {
            if (!string.Equals(expected[index], printed[index], StringComparison.Ordinal))
            {
                return printed[index];
            }
        }

        return null;
    }
}
