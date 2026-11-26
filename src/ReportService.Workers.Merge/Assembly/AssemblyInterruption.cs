namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// The deterministic interruption of one assembly, for the restart test only: the
/// worker process exits after a chosen staging batch, exactly where a real worker
/// loss would leave committed parts and no published output. The composition
/// installs it only when the process is told to; a normal run holds none.
/// </summary>
internal sealed class AssemblyInterruption
{
    /// <summary>The variable that makes a merge worker install the seam.</summary>
    public const string AfterBatchVariable = "REPORTSERVICE_ASSEMBLY_INTERRUPT_AFTER_BATCH";

    private readonly int _afterBatch;
    private readonly Action<int> _exit;

    /// <summary>Creates the seam for the given batch.</summary>
    /// <param name="afterBatch">The batch after which the process exits, from one.</param>
    /// <param name="exit">The exit action; the production composition passes the process exit.</param>
    public AssemblyInterruption(int afterBatch, Action<int> exit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(afterBatch, 1);
        ArgumentNullException.ThrowIfNull(exit);
        _afterBatch = afterBatch;
        _exit = exit;
    }

    /// <summary>The seam a process installs from its environment, or none when it is not told to interrupt.</summary>
    public static AssemblyInterruption? FromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable(AfterBatchVariable);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var batch)
            && batch > 0
            ? new AssemblyInterruption(batch, static code => Environment.Exit(code))
            : null;
    }

    /// <summary>Exits the process when <paramref name="batch"/> is the chosen batch.</summary>
    /// <param name="batch">The batch just written, from one.</param>
    public void AfterBatch(int batch)
    {
        if (batch == _afterBatch)
        {
            _exit(1);
        }
    }
}
