using ReportService.DataContract;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks;

/// <summary>
/// The static checks' share of the take's deadline: 30 s in total, measured as
/// elapsed wall-clock time from the first check's start. It is a backstop far above
/// the measured cost of an admissible package, never the expected outcome, and it is
/// checked between checks and inside long loops. It is the eleven static checks'
/// boundary and is not widened: the sample render that runs after them is bounded by
/// the take's own deadline, and its trip is a retry, never a refusal.
/// </summary>
/// <param name="clock">The clock the elapsed time is read from.</param>
public sealed class TemplateCheckBudget(TimeProvider clock)
{
    /// <summary>The checks' total share of the take's deadline.</summary>
    public static readonly TimeSpan Total = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The instant the first check started, as the clock stamps it.</summary>
    public long Start() => _clock.GetTimestamp();

    /// <summary>Whether the checks have used their total share.</summary>
    /// <param name="started">The instant <see cref="Start"/> returned.</param>
    public bool Exceeded(long started) => _clock.GetElapsedTime(started) >= Total;
}

/// <summary>
/// What the checks share within one take: the candidate's package view, the data
/// contract the schema check prepared for the sample and anchor checks, and the
/// budget's start. A check reads it and never writes the package. The session is a
/// process singleton, so a candidate change disposes the previous contract and
/// nothing is kept across takes (a refused version is checked again by the next
/// take).
/// </summary>
public sealed class TemplateCheckSession : IDisposable
{
    private readonly object _gate = new();
    private readonly TemplateCheckBudget _budget;
    private TemplateCandidate? _candidate;
    private PackageView? _view;
    private PreparedDataContract? _contract;
    private long _started;
    private bool _disposed;

    /// <summary>Creates the session with the budget's clock.</summary>
    /// <param name="clock">The clock the budget reads.</param>
    public TemplateCheckSession(TimeProvider clock) => _budget = new TemplateCheckBudget(clock);

    /// <summary>The candidate's package view, built once per take.</summary>
    /// <param name="candidate">The version being taken.</param>
    public PackageView View(TemplateCandidate candidate)
    {
        lock (_gate)
        {
            Begin(candidate);
            return _view!;
        }
    }

    /// <summary>Keeps the contract the schema check prepared for the later checks.</summary>
    /// <param name="candidate">The version being taken.</param>
    /// <param name="contract">The prepared contract; the session owns it.</param>
    public void KeepContract(TemplateCandidate candidate, PreparedDataContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        lock (_gate)
        {
            Begin(candidate);
            _contract?.Dispose();
            _contract = contract;
        }
    }

    /// <summary>The contract prepared for this take, when the schema check kept one.</summary>
    /// <param name="candidate">The version being taken.</param>
    public PreparedDataContract? Contract(TemplateCandidate candidate)
    {
        lock (_gate)
        {
            Begin(candidate);
            return _contract;
        }
    }

    /// <summary>Whether the checks have used their total share of the deadline.</summary>
    /// <param name="candidate">The version being taken.</param>
    public bool BudgetExceeded(TemplateCandidate candidate)
    {
        lock (_gate)
        {
            Begin(candidate);
            return _budget.Exceeded(_started);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _contract?.Dispose();
            _contract = null;
            _disposed = true;
        }
    }

    private void Begin(TemplateCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(candidate, _candidate))
        {
            _contract?.Dispose();
            _contract = null;
            _candidate = candidate;
            _view = new PackageView(candidate);
            _started = _budget.Start();
        }
    }
}
