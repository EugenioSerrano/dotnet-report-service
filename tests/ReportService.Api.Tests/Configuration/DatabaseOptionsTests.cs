using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Configuration;

/// <summary>
/// The two settings of the metadata database: each is required, the connection
/// string must name a host and a database, the expected version must be a
/// script name. A problem names the full setting path and never a value.
/// </summary>
public sealed class DatabaseOptionsTests
{
    private const string PlantedSecret = "planted-secret-4c81ba";
    private const string Baseline = "0001_baseline.sql";
    private const string ValidConnectionString =
        "Host=127.0.0.1;Port=1;Username=nobody;Password=" + PlantedSecret + ";Database=nothing";

    [Fact]
    public void BothSettingsValidHaveNoProblem()
    {
        var problems = Options(ValidConnectionString, Baseline).Validate().ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void AMissingConnectionStringNamesTheFullSettingPath()
    {
        var problems = Options(string.Empty, Baseline).Validate().ToList();

        var problem = Assert.Single(problems);
        Assert.Equal("setting 'ConnectionStrings:reportservice': is required", problem.ToString());
    }

    [Fact]
    public void AConnectionStringWithoutAHostIsRefusedWithoutItsValue()
    {
        var problems = Options("Port=1;Database=nothing;Password=" + PlantedSecret, Baseline).Validate().ToList();

        var problem = Assert.Single(problems);
        Assert.Equal("setting 'ConnectionStrings:reportservice': is not a valid connection string", problem.ToString());
        Assert.DoesNotContain(PlantedSecret, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionStringWithoutADatabaseIsRefused()
    {
        var problems = Options("Host=127.0.0.1;Username=nobody", Baseline).Validate().ToList();

        Assert.Equal("setting 'ConnectionStrings:reportservice': is not a valid connection string", Assert.Single(problems).ToString());
    }

    [Fact]
    public void AMissingExpectedVersionNamesTheFullSettingPath()
    {
        var problems = Options(ValidConnectionString, string.Empty).Validate().ToList();

        Assert.Equal("setting 'Database:ExpectedSchemaVersion': is required", Assert.Single(problems).ToString());
    }

    [Fact]
    public void AnExpectedVersionThatIsNotAScriptNameIsRefusedWithoutItsValue()
    {
        var problems = Options(ValidConnectionString, "planted-" + PlantedSecret).Validate().ToList();

        var problem = Assert.Single(problems);
        Assert.Equal("setting 'Database:ExpectedSchemaVersion': must be a migration script name", problem.ToString());
        Assert.DoesNotContain(PlantedSecret, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoneIsAValidExpectedVersionForAnEmptyScriptSet()
    {
        Assert.Empty(Options(ValidConnectionString, SchemaVersion.None).Validate());
    }

    [Fact]
    public void BothSettingsMissingAreReportedTogether()
    {
        var problems = Options(string.Empty, string.Empty).Validate().ToList();

        Assert.Equal(2, problems.Count);
        Assert.Equal(
            "setting 'ConnectionStrings:reportservice': is required; setting 'Database:ExpectedSchemaVersion': is required",
            string.Join("; ", problems.Select(problem => problem.ToString())));
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=nothing", true)]
    [InlineData("Server=127.0.0.1;Database=nothing", true)]
    [InlineData("Database=nothing", false)]
    [InlineData("Host=127.0.0.1", false)]
    [InlineData("   ", false)]
    [InlineData("not a connection string", false)]
    public void AConnectionStringNamesAHostAndADatabase(string value, bool valid) =>
        Assert.Equal(valid, DatabaseOptions.IsConnectionString(value));

    [Fact]
    public void TheSettingsAreReadFromTheirFullPaths()
    {
        Assert.Equal("ConnectionStrings:reportservice", DatabaseOptions.ConnectionStringSetting);
        Assert.Equal("Database:ExpectedSchemaVersion", DatabaseOptions.ExpectedSchemaVersionSetting);
    }

    private static DatabaseOptions Options(string connectionString, string expectedVersion) =>
        new() { ConnectionString = connectionString, ExpectedSchemaVersion = expectedVersion };
}
