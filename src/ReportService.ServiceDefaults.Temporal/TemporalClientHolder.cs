namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// The process's one engine connection, or none while it is connecting. Read
/// and replaced atomically; nothing else in the process creates a client.
/// </summary>
public sealed class TemporalClientHolder
{
    private IEngineConnection? _current;

    /// <summary>The current connection, or <c>null</c> before the first connection succeeds.</summary>
    public IEngineConnection? Current => Volatile.Read(ref _current);

    /// <summary>Publishes <paramref name="connection"/> and returns the connection it replaces.</summary>
    public IEngineConnection? Replace(IEngineConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Interlocked.Exchange(ref _current, connection);
    }
}
