using Npgsql;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The two settings of the metadata database: the application role's connection
/// string and the schema version the code expects. They live in different
/// configuration sections, so every problem names the setting's full path and
/// never its value.
/// </summary>
public sealed class DatabaseOptions : IValidatedSettings
{
    /// <summary>The setting of the connection string the process connects with.</summary>
    public const string ConnectionStringSetting = "ConnectionStrings:reportservice";

    /// <summary>The setting of the schema version the code expects.</summary>
    public const string ExpectedSchemaVersionSetting = "Database:ExpectedSchemaVersion";

    /// <summary>The application role's connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The highest migration script the code embeds.</summary>
    public string ExpectedSchemaVersion { get; set; } = string.Empty;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            yield return new ConfigurationProblem(ConnectionStringSetting, "is required");
        }
        else if (!IsConnectionString(ConnectionString))
        {
            yield return new ConfigurationProblem(ConnectionStringSetting, "is not a valid connection string");
        }

        if (string.IsNullOrWhiteSpace(ExpectedSchemaVersion))
        {
            yield return new ConfigurationProblem(ExpectedSchemaVersionSetting, "is required");
        }
        else if (!string.Equals(ExpectedSchemaVersion, SchemaVersion.None, StringComparison.Ordinal)
            && !SchemaVersion.IsScriptName(ExpectedSchemaVersion))
        {
            yield return new ConfigurationProblem(ExpectedSchemaVersionSetting, "must be a migration script name");
        }
    }

    /// <summary>
    /// Whether the value names a host and a database. The rest of the string is
    /// the server's to refuse at open time, where the message never leaves the
    /// process.
    /// </summary>
    public static bool IsConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            return !string.IsNullOrWhiteSpace(builder.Host) && !string.IsNullOrWhiteSpace(builder.Database);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
