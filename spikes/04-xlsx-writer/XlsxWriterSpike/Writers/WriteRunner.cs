using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Writers;

/// <summary>What to write.</summary>
/// <param name="Candidate">Candidate name.</param>
/// <param name="InputPath">Source JSON file.</param>
/// <param name="Profile">Column profile.</param>
/// <param name="OutputPath">Output XLSX file (ignored by <c>read-only</c>).</param>
/// <param name="Rows">Optional row cap.</param>
/// <param name="Policy">Row-limit policy.</param>
/// <param name="SheetRowLimit">Data rows per sheet.</param>
/// <param name="IgnoreHeaderCount">Skip the pre-check from the header count.</param>
internal sealed record WriteRequest(
    string Candidate,
    string InputPath,
    ColumnProfile Profile,
    string OutputPath,
    long? Rows,
    RowLimitPolicy Policy,
    long SheetRowLimit,
    bool IgnoreHeaderCount);

/// <summary>How a write ended.</summary>
internal sealed class WriteOutcome
{
    /// <summary>0 ok, 1 failed, 2 invalid arguments, 3 <c>OUTPUT_LIMIT_EXCEEDED</c>.</summary>
    public int ExitCode { get; init; }

    /// <summary><c>ok</c>, <c>OUTPUT_LIMIT_EXCEEDED</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The limit error, when the outcome is <c>OUTPUT_LIMIT_EXCEEDED</c>.</summary>
    public OutputLimitExceededException? LimitError { get; init; }

    /// <summary>The error message (one line), when not ok.</summary>
    public string? Message { get; init; }

    /// <summary>Exception type of a failure (for diagnosis).</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Data rows yielded to the writer (last enumeration of each sheet).</summary>
    public long RowsWritten { get; init; }

    /// <summary>Sheets in the plan.</summary>
    public int Sheets { get; init; }

    /// <summary>Times the source was enumerated.</summary>
    public int SourceEnumerations { get; init; }

    /// <summary>Whether the output file exists after the run.</summary>
    public bool OutputExists { get; init; }

    /// <summary>Output size in bytes (0 when absent).</summary>
    public long OutputBytes { get; init; }
}

/// <summary>Runs one candidate; on any error the partial output file is deleted.</summary>
internal static class WriteRunner
{
    /// <summary>Executes the request and maps the result to an exit code.</summary>
    public static WriteOutcome Execute(WriteRequest request)
    {
        var candidate = Candidates.Create(request.Candidate);
        var writes = candidate is not ReadOnlyCandidate;
        if (writes && File.Exists(request.OutputPath))
        {
            return Failed(1, new SpikeException("The output file already exists."), null, request);
        }

        var source = new RecordSource(request.InputPath, request.Profile);
        SheetPlan? plan = null;
        try
        {
            plan = SheetPlan.Create(
                request.Profile.SheetName,
                request.IgnoreHeaderCount ? null : source.ReadHeaderCount(),
                request.Rows,
                request.SheetRowLimit,
                request.Policy,
                request.IgnoreHeaderCount);
            candidate.Write(source, plan, request.OutputPath);
            var exists = writes && File.Exists(request.OutputPath);
            return new WriteOutcome
            {
                ExitCode = 0,
                Outcome = "ok",
                RowsWritten = source.RowsYielded,
                Sheets = plan.Sheets.Count,
                SourceEnumerations = source.Enumerations,
                OutputExists = exists,
                OutputBytes = exists ? new FileInfo(request.OutputPath).Length : 0,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (writes)
            {
                TryDelete(request.OutputPath);
            }

            var limit = Find<OutputLimitExceededException>(exception);
            if (limit is not null)
            {
                return new WriteOutcome
                {
                    ExitCode = 3,
                    Outcome = OutputLimitExceededException.ErrorCode,
                    LimitError = limit,
                    Message = limit.Message,
                    RowsWritten = source.RowsYielded,
                    Sheets = plan?.Sheets.Count ?? 0,
                    SourceEnumerations = source.Enumerations,
                    OutputExists = writes && File.Exists(request.OutputPath),
                };
            }

            var usage = Find<UsageException>(exception);
            return Failed(usage is null ? 1 : 2, Find<SpikeException>(exception) ?? exception, plan, request, source);
        }
    }

    private static WriteOutcome Failed(int exitCode, Exception exception, SheetPlan? plan, WriteRequest request, RecordSource? source = null) => new()
    {
        ExitCode = exitCode,
        Outcome = "failed",
        Message = OneLine(exception.Message),
        ExceptionType = exception.GetType().FullName,
        RowsWritten = source?.RowsYielded ?? 0,
        Sheets = plan?.Sheets.Count ?? 0,
        SourceEnumerations = source?.Enumerations ?? 0,
        OutputExists = File.Exists(request.OutputPath),
    };

    private static T? Find<T>(Exception exception)
        where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                var inner = Find<T>(aggregate.InnerExceptions[0]);
                if (inner is not null)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
