using System.Text;
using ReportService.ServiceDefaults;

namespace ReportService.Migrations.Tests;

/// <summary>
/// What a run applies from the scripts the code embeds and the journal it finds:
/// a fresh database, a current one, a gap, and each refusal that applies
/// nothing.
/// </summary>
public sealed class MigrationPlannerTests
{
    private const string Baseline = "0001_baseline.sql";
    private const string Second = "0002_test-table.sql";
    private const string Third = "0003_views.sql";

    [Fact]
    public void AFreshDatabaseAppliesEveryScriptInNameOrder()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, []);

        Assert.Equal([Baseline, Second], plan.ToApply.Select(script => script.Name));
        Assert.Empty(plan.AlreadyApplied);
        Assert.Empty(plan.Refusals);
    }

    [Fact]
    public void ACurrentDatabaseAppliesNothing()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, [Applied(known, Baseline), Applied(known, Second)]);

        Assert.Empty(plan.ToApply);
        Assert.Equal([Baseline, Second], plan.AlreadyApplied.Select(script => script.Name));
        Assert.Empty(plan.Refusals);
    }

    [Fact]
    public void AGapBelowTheHighestAppliedScriptIsAppliedToo()
    {
        var known = Known(Baseline, Second, Third);

        var plan = MigrationPlanner.Plan(known, [Applied(known, Baseline), Applied(known, Third)]);

        Assert.Equal([Second], plan.ToApply.Select(script => script.Name));
        Assert.Equal([Baseline, Third], plan.AlreadyApplied.Select(script => script.Name));
    }

    [Fact]
    public void AChangedChecksumIsRefusedAndNothingIsApplied()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, [new JournalEntry(Baseline, new string('0', 64))]);

        Assert.Equal(["migration '" + Baseline + "' changed after it was applied"], plan.Refusals);
    }

    [Fact]
    public void AnAppliedChecksumIsComparedCaseInsensitively()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, [new JournalEntry(Baseline, Applied(known, Baseline).Checksum!.ToUpperInvariant())]);

        Assert.Empty(plan.Refusals);
        Assert.Equal([Baseline], plan.AlreadyApplied.Select(script => script.Name));
        Assert.Equal([Second], plan.ToApply.Select(script => script.Name));
    }

    [Fact]
    public void AnUnknownAppliedScriptBelowTheExpectedVersionIsRefused()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, [new JournalEntry("0000_old.sql", Applied(known, Baseline).Checksum), Applied(known, Baseline)]);

        Assert.Equal(["unknown applied migration '0000_old.sql'"], plan.Refusals);
    }

    [Fact]
    public void AnAppliedVersionAheadOfTheCodeIsRefused()
    {
        var known = Known(Baseline, Second);

        var plan = MigrationPlanner.Plan(known, [Applied(known, Baseline), new JournalEntry("0009_newer.sql", Applied(known, Baseline).Checksum)]);

        Assert.Equal(["unknown applied migration '0009_newer.sql'"], plan.Refusals);
    }

    [Fact]
    public void AJournalRowWithoutAScriptNameIsRefused()
    {
        var known = Known(Baseline);

        var plan = MigrationPlanner.Plan(known, [new JournalEntry(null, null), Applied(known, Baseline)]);

        Assert.Equal(["journal row without a script name or checksum"], plan.Refusals);
    }

    [Fact]
    public void AJournalRowWithoutAChecksumIsRefused()
    {
        var known = Known(Baseline);

        var plan = MigrationPlanner.Plan(known, [new JournalEntry(Baseline, " ")]);

        Assert.Equal(["journal row without a script name or checksum"], plan.Refusals);
    }

    [Fact]
    public void AnEmptyKnownSetRefusesEveryAppliedRow()
    {
        var plan = MigrationPlanner.Plan([], [new JournalEntry(Baseline, new string('a', 64))]);

        Assert.Equal(SchemaVersion.None, MigrationCatalog.ExpectedVersionOf([]));
        Assert.Equal(["unknown applied migration '" + Baseline + "'"], plan.Refusals);
    }

    [Fact]
    public void TheShippedCatalogHasAChecksumForEveryScriptItEmbeds() =>
        Assert.All(MigrationCatalog.Scripts, script => Assert.Matches("^[0-9a-f]{64}$", script.Checksum));

    [Fact]
    public void TheShippedCatalogIsAppliedFromAnEmptyJournal()
    {
        var plan = MigrationPlanner.Plan(MigrationCatalog.Scripts, []);

        Assert.Equal(MigrationCatalog.Scripts.Select(script => script.Name), plan.ToApply.Select(script => script.Name));
        Assert.Empty(plan.Refusals);
    }

    private static IReadOnlyList<MigrationScript> Known(params string[] names) =>
        MigrationCatalog.Create([.. names.Select(name => (Name: name, Bytes: Encoding.UTF8.GetBytes("SELECT 1 -- " + name)))]);

    private static JournalEntry Applied(IReadOnlyList<MigrationScript> known, string name) =>
        new(name, known.Single(script => script.Name == name).Checksum);
}
