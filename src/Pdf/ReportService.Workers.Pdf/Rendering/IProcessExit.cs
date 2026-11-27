namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// Ends the process a watchdog stopped. The production implementation is
/// <see cref="Environment.Exit"/>; a test replaces it, because the OS-level exit would end
/// the test process itself. It is internal, and only the test projects this assembly names
/// can see it.
/// </summary>
internal interface IProcessExit
{
    /// <summary>Ends the current process.</summary>
    /// <param name="exitCode">The code the process ends with.</param>
    void Exit(int exitCode);
}

/// <summary>The production exit: the process ends at once and the platform replaces it.</summary>
internal sealed class EnvironmentProcessExit : IProcessExit
{
    /// <inheritdoc />
    public void Exit(int exitCode) => Environment.Exit(exitCode);
}
