namespace ReportService.Api.Consumers;

/// <summary>
/// What an upload operation asks of the declared consumer. A consumer that opens
/// a new upload has to be active; a consumer that reads its status, sends content
/// to it or confirms it only has to be registered, so an upload opened while it
/// was active stays usable after it is made inactive. The requirement is the only
/// difference between the four operations' gates.
/// </summary>
public enum ConsumerRequirement
{
    /// <summary>The consumer must be in the registry, whether it is active or not.</summary>
    Registered,

    /// <summary>The consumer must be in the registry and active.</summary>
    Active,
}
