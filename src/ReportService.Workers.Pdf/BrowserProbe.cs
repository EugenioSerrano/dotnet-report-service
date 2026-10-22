using Microsoft.Playwright;

namespace ReportService.Workers.Pdf;

/// <summary>
/// Launches the installed Chromium once at start-up, reads its version and
/// closes it again. No browser stays running and nothing is rendered.
/// </summary>
public sealed partial class BrowserProbe(ILogger<BrowserProbe> logger) : BackgroundService
{
    private BrowserProbeResult? result;

    /// <summary>The outcome of the start-up probe, or <c>null</c> while it is still running.</summary>
    public BrowserProbeResult? Result => Volatile.Read(ref result);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            string chromiumVersion;
            try
            {
                chromiumVersion = browser.Version;
            }
            finally
            {
                await browser.CloseAsync();
            }

            var playwrightVersion = typeof(IPlaywright).Assembly.GetName().Version?.ToString() ?? "unknown";
            Volatile.Write(ref result, BrowserProbeResult.Success(chromiumVersion));
            LogReady(chromiumVersion, playwrightVersion, Environment.Version.ToString());
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            Volatile.Write(ref result, BrowserProbeResult.Failure(exception.Message));
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chromium {ChromiumVersion} ready (Playwright {PlaywrightVersion}, .NET {RuntimeVersion})")]
    private partial void LogReady(string chromiumVersion, string playwrightVersion, string runtimeVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Chromium could not be launched")]
    private partial void LogFailed(Exception exception);
}

/// <summary>Outcome of the start-up browser probe.</summary>
/// <param name="Succeeded">Whether Chromium launched.</param>
/// <param name="ChromiumVersion">The browser version reported by Chromium, when it launched.</param>
/// <param name="Error">The failure message, when it did not.</param>
public sealed record BrowserProbeResult(bool Succeeded, string? ChromiumVersion, string? Error)
{
    /// <summary>Creates a successful outcome.</summary>
    public static BrowserProbeResult Success(string chromiumVersion) => new(true, chromiumVersion, null);

    /// <summary>Creates a failed outcome.</summary>
    public static BrowserProbeResult Failure(string error) => new(false, null, error);
}
