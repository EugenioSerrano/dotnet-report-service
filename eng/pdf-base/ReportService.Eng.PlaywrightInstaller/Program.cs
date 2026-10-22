namespace ReportService.Eng.PlaywrightInstaller;

/// <summary>
/// Runs the browser installer of the browser automation package, at the
/// version pinned for the whole repository. Used only while building the PDF
/// base image.
/// </summary>
internal static class Program
{
    public static int Main(string[] args) => Microsoft.Playwright.Program.Main(args);
}
