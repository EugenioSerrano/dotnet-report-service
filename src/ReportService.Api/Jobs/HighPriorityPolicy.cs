using Microsoft.Extensions.Options;
using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Jobs;

/// <summary>
/// A deployment's restriction of the <c>high</c> priority: none (any declared consumer may ask
/// for it), an allow-list of the consumers that may, or disabled (the deployment does not offer
/// it at all). The restriction is a static policy of the deployment, read at start: it never
/// counts jobs, never reads a queue and never depends on how busy the service is, so a request
/// it admits is admitted whatever the load. It is a policy over a declared identity, not a
/// security boundary.
/// </summary>
public sealed class HighPriorityPolicy : IValidatedSettings
{
    /// <summary>The configuration section the policy is read from.</summary>
    public const string SectionPath = "Jobs:Priority:High";

    /// <summary>The setting naming the kind of restriction.</summary>
    public const string RestrictionSetting = "Restriction";

    /// <summary>The setting listing the consumers an allow-list admits.</summary>
    public const string AllowedConsumersSetting = "AllowedConsumers";

    /// <summary>No restriction: any declared consumer may ask for <c>high</c>. The default.</summary>
    public const string NoRestriction = "none";

    /// <summary>Only the listed consumers may ask for <c>high</c>.</summary>
    public const string AllowListRestriction = "allow-list";

    /// <summary>The deployment does not offer <c>high</c> to anyone.</summary>
    public const string DisabledRestriction = "disabled";

    /// <summary>The kind of restriction, as configured.</summary>
    public string Restriction { get; set; } = NoRestriction;

    /// <summary>The consumers an allow-list admits; empty for the other kinds.</summary>
    public IList<string> AllowedConsumers { get; } = [];

    /// <summary>Whether <paramref name="consumerId"/> may declare the <c>high</c> priority under this policy.</summary>
    /// <param name="consumerId">The declared consumer of the request.</param>
    public bool AdmitsHigh(string consumerId)
    {
        ArgumentNullException.ThrowIfNull(consumerId);
        return Restriction switch
        {
            DisabledRestriction => false,
            AllowListRestriction => AllowedConsumers.Contains(consumerId, StringComparer.Ordinal),
            _ => true,
        };
    }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (Restriction is not (NoRestriction or AllowListRestriction or DisabledRestriction))
        {
            yield return new ConfigurationProblem(RestrictionSetting, "must be 'none', 'allow-list' or 'disabled'");
            yield break;
        }

        if (Restriction == AllowListRestriction)
        {
            if (AllowedConsumers.Count == 0)
            {
                yield return new ConfigurationProblem(AllowedConsumersSetting, "must name at least one consumer when the restriction is 'allow-list'");
            }
            else if (AllowedConsumers.Any(string.IsNullOrWhiteSpace))
            {
                yield return new ConfigurationProblem(AllowedConsumersSetting, "must not hold an empty consumer identifier");
            }
        }
        else if (AllowedConsumers.Count > 0)
        {
            yield return new ConfigurationProblem(AllowedConsumersSetting, "is only allowed when the restriction is 'allow-list'");
        }
    }

    /// <summary>
    /// The allow-list's members that the consumer registry does not declare. A policy that names
    /// a consumer the service does not know is a deployment error, reported at start without
    /// listing the identifiers.
    /// </summary>
    /// <param name="declaredConsumers">The consumer identifiers the registry declares.</param>
    public IEnumerable<ConfigurationProblem> CheckAgainstRegistry(IReadOnlyCollection<string> declaredConsumers)
    {
        ArgumentNullException.ThrowIfNull(declaredConsumers);
        if (Restriction == AllowListRestriction
            && AllowedConsumers.Any(consumer => !declaredConsumers.Contains(consumer, StringComparer.Ordinal)))
        {
            yield return new ConfigurationProblem(
                SectionPath + ":" + AllowedConsumersSetting,
                "names a consumer the consumer registry does not declare");
        }
    }
}

/// <summary>Registers the deployment's restriction of the <c>high</c> priority.</summary>
public static class HighPriorityPolicyHosting
{
    /// <summary>
    /// Binds <see cref="HighPriorityPolicy"/> from its section and validates it at start: its own
    /// settings, and an allow-list against the consumers the registry declares. The consumer
    /// registry must be registered first.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    public static IHostApplicationBuilder AddHighPriorityPolicy(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddValidatedConfiguration<HighPriorityPolicy>(HighPriorityPolicy.SectionPath);
        builder.AddSettingsCheck(services => services.GetRequiredService<IOptions<HighPriorityPolicy>>().Value
            .CheckAgainstRegistry([.. services.GetRequiredService<ConsumerRegistry>().Current.Entries
                .Select(entry => entry.ConsumerId)]));
        return builder;
    }
}
