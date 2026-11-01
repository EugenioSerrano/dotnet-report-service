using ReportService.ServiceDefaults;

namespace ReportService.Migrations;

/// <summary>One row of the journal as it is read: the script it names and the checksum recorded with it.</summary>
public sealed record JournalEntry(string? ScriptName, string? Checksum);

/// <summary>What one run would do: the scripts to apply, those already applied and the refusals.</summary>
public sealed record MigrationPlan(
    IReadOnlyList<MigrationScript> ToApply,
    IReadOnlyList<MigrationScript> AlreadyApplied,
    IReadOnlyList<string> Refusals);

/// <summary>
/// Decides what a run applies, from the scripts the code embeds and the journal
/// the database reports. Any refusal means nothing is applied: a changed
/// checksum, a row the code does not embed (including a version ahead of it) and
/// a row without a name or a checksum are each refused.
/// </summary>
public static class MigrationPlanner
{
    /// <summary>Plans one run over <paramref name="known"/> against <paramref name="journal"/>.</summary>
    public static MigrationPlan Plan(IReadOnlyList<MigrationScript> known, IReadOnlyList<JournalEntry> journal)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(journal);

        var scripts = known.OrderBy(script => script.Name, StringComparer.Ordinal).ToList();
        var refusals = new List<string>();
        if (journal.Any(entry => string.IsNullOrWhiteSpace(entry.ScriptName) || string.IsNullOrWhiteSpace(entry.Checksum)))
        {
            refusals.Add("journal row without a script name or checksum");
        }

        var appliedNames = journal
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ScriptName))
            .Select(entry => entry.ScriptName!)
            .ToList();

        // The same comparison the health check uses, with the known set, so an
        // unknown and an ahead entry are classified in one place.
        var assessment = SchemaVersion.Assess(
            MigrationCatalog.ExpectedVersionOf(scripts),
            appliedNames,
            [.. scripts.Select(script => script.Name)]);
        if (assessment.Status == SchemaStatus.Unknown)
        {
            refusals.Add($"unknown applied migration '{assessment.Offender}'");
        }

        var toApply = new List<MigrationScript>();
        var alreadyApplied = new List<MigrationScript>();
        foreach (var script in scripts)
        {
            var entry = journal.FirstOrDefault(row => string.Equals(row.ScriptName, script.Name, StringComparison.Ordinal));
            if (entry is null)
            {
                toApply.Add(script);
                continue;
            }

            if (entry.Checksum is null || string.IsNullOrWhiteSpace(entry.Checksum))
            {
                // Already refused above: a journal row the code cannot compare.
                continue;
            }

            if (!string.Equals(entry.Checksum.Trim().ToLowerInvariant(), script.Checksum, StringComparison.Ordinal))
            {
                refusals.Add($"migration '{script.Name}' changed after it was applied");
                continue;
            }

            alreadyApplied.Add(script);
        }

        return new MigrationPlan(toApply, alreadyApplied, refusals);
    }
}
