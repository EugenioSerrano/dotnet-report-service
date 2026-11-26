namespace ReportService.Pdf.Assembly;

/// <summary>What one assembly produced: the saved document, its page count and its measurements.</summary>
/// <param name="OutputPath">The file the assembled document was saved to.</param>
/// <param name="PageCount">How many pages the assembled document holds.</param>
/// <param name="Metrics">The measurements recorded for the assembly.</param>
public sealed record AssemblyResult(string OutputPath, int PageCount, AssemblyMetrics Metrics);
