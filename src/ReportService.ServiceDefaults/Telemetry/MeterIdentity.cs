namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The name of the meter one host creates its instruments on, and the name its
/// metric provider subscribes to. A process that owns its host supplies
/// <see cref="TelemetryDefaults.MeterName"/>; a composition that shares a process with
/// another host supplies a distinct identity per host, so one host's provider never
/// enables another host's instruments.
/// <para>
/// The identity is deliberately a value with no process-wide instance: there is no
/// default and no conversion from a string, so a composition that shares a process
/// must spell its own identity and one that forgets it does not compile.
/// </para>
/// </summary>
public sealed record MeterIdentity
{
    private MeterIdentity(string name) => Name = name;

    /// <summary>The name the host's meter is created with and its provider subscribes to.</summary>
    public string Name { get; }

    /// <summary>The identity a host carries.</summary>
    /// <param name="name">The name the host's meter is created with.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static MeterIdentity ForHost(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new MeterIdentity(name);
    }
}
