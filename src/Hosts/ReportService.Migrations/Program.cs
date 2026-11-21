using Microsoft.Extensions.Configuration;
using ReportService.ServiceDefaults;

namespace ReportService.Migrations;

/// <summary>
/// The one-shot migrator: reads its connection string, applies the embedded
/// scripts and exits 0 when the schema is current, 1 when it refuses or fails.
/// It never writes a connection string, a host, a user or a password.
/// </summary>
internal static class Program
{
    /// <summary>The documented setting form, read from the environment.</summary>
    public const string ConnectionSetting = "ConnectionStrings:reportservice";

    /// <summary>The environment variable that carries the setting.</summary>
    public const string ConnectionEnvironmentVariable = "ConnectionStrings__reportservice";

    private const int ExitPassed = 0;
    private const int ExitFailed = 1;

    public static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var connectionString = configuration[ConnectionSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console.Error.WriteLineAsync($"setting '{ConnectionSetting}': is required").ConfigureAwait(false);
            return ExitFailed;
        }

        if (!IsConnectionString(connectionString))
        {
            await Console.Error.WriteLineAsync($"setting '{ConnectionSetting}': is not a valid connection string").ConfigureAwait(false);
            return ExitFailed;
        }

        IReadOnlyList<MigrationScript> scripts;
        try
        {
            scripts = MigrationCatalog.Scripts;
        }
        catch (InvalidOperationException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitFailed;
        }

        var result = await MigrationRunner.RunAsync(connectionString, scripts, CancellationToken.None).ConfigureAwait(false);
        foreach (var line in result.OutputLines)
        {
            await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        }

        foreach (var line in result.ErrorLines)
        {
            await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
        }

        return result.Succeeded ? ExitPassed : ExitFailed;
    }

    private static bool IsConnectionString(string value) => DatabaseOptions.IsConnectionString(value);
}
