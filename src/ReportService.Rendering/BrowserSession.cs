using Microsoft.Playwright;

namespace ReportService.Rendering;

/// <summary>
/// One browser per process: launched once, on first use, and relaunched if it
/// disconnects. A document is never printed by a browser another document left in
/// an unknown state, and closing the session closes the driver with it.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPlaywright? playwright;
    private IBrowser? browser;
    private bool disposed;

    /// <summary>Whether a browser is launched and still connected.</summary>
    public bool IsReady => browser is { IsConnected: true };

    /// <summary>Returns the browser, launching it or replacing a disconnected one.</summary>
    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (browser is { IsConnected: true })
            {
                return browser;
            }

            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                browser = null;
            }

            playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
            return browser;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                browser = null;
            }

            playwright?.Dispose();
            playwright = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}
