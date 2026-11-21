namespace ReportService.Api.Consumers;

/// <summary>
/// The state of a request's declared consumer identity, as the answer and the
/// telemetry may name it. An identity that was never sent, one that cannot be a
/// registry entry, one the registry does not carry and one that is registered but
/// inactive are all refused, and a caller is told only the code; this state exists
/// so the service's own log and counter can tell the four apart.
/// </summary>
public enum ConsumerIdentityState
{
    /// <summary>No identity was declared.</summary>
    Missing,

    /// <summary>An identity was declared but is not usable: empty, repeated or outside the grammar.</summary>
    Malformed,

    /// <summary>The declared identity is not in the registry.</summary>
    Unregistered,

    /// <summary>The declared identity is in the registry and is not active.</summary>
    Inactive,
}

/// <summary>
/// What the gate decided about one request: the consumer it was allowed to act
/// as, or the state that refused it. It carries nothing from the caller but a
/// value the registry already holds, so a refusal can be logged and counted
/// without ever repeating unverified caller text.
/// </summary>
public sealed record ConsumerAccessResult
{
    private ConsumerAccessResult(string? consumerId, ConsumerIdentityState? state)
    {
        ConsumerId = consumerId;
        State = state;
    }

    /// <summary>The registered consumer the request acts as; null when the identity is unknown.</summary>
    public string? ConsumerId { get; }

    /// <summary>The state that refused the request; null when the request is allowed.</summary>
    public ConsumerIdentityState? State { get; }

    /// <summary>Whether the request may go on.</summary>
    public bool Allowed => State is null;

    /// <summary>Whether the request may not go on because the consumer is registered and inactive.</summary>
    public bool Inactive => State == ConsumerIdentityState.Inactive;

    /// <summary>The state of a known, admitted consumer.</summary>
    /// <param name="consumerId">The identity the registry carries.</param>
    public static ConsumerAccessResult Allow(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        return new ConsumerAccessResult(consumerId, state: null);
    }

    /// <summary>A refused identity the registry knows nothing about.</summary>
    /// <param name="state">Missing, malformed or unregistered.</param>
    public static ConsumerAccessResult Unknown(ConsumerIdentityState state)
    {
        if (state == ConsumerIdentityState.Inactive)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "an inactive consumer is registered, not unknown.");
        }

        return new ConsumerAccessResult(consumerId: null, state);
    }

    /// <summary>A registered consumer that may not perform the operation.</summary>
    /// <param name="consumerId">The identity the registry carries.</param>
    public static ConsumerAccessResult RefuseInactive(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        return new ConsumerAccessResult(consumerId, ConsumerIdentityState.Inactive);
    }

    /// <summary>The wire text of <paramref name="state"/>, as the log and the counter carry it.</summary>
    /// <param name="state">The state to name.</param>
    public static string Text(ConsumerIdentityState state) => state switch
    {
        ConsumerIdentityState.Missing => "missing",
        ConsumerIdentityState.Malformed => "malformed",
        ConsumerIdentityState.Unregistered => "unregistered",
        ConsumerIdentityState.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(state), "the consumer identity state has no text"),
    };

    /// <summary>The state of this result, in its wire text; null when the request is allowed.</summary>
    public string? IdentityStateText => State is { } state ? Text(state) : null;
}
