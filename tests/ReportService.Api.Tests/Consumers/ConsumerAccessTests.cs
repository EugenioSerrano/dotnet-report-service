using Microsoft.AspNetCore.Http;
using ReportService.Api.Consumers;
using ReportService.Api.Http;

namespace ReportService.Api.Tests.Consumers;

/// <summary>
/// The gate every upload operation passes first, asserted on its own: what a request
/// may act as, decided from its declared identity and one snapshot of the registry,
/// for each of the two requirements the operations have. A declaration that is not
/// there, not usable or not in the registry is answered the same way; one that is
/// there and inactive is only refused where the operation needs it to be active.
/// </summary>
public sealed class ConsumerAccessTests
{
    private static readonly ConsumerRegistrySnapshot Registry = ConsumerRegistrySnapshot.Create(
    [
        new ConsumerEntry("dev-consumer", null, Active: true, null, null),
        new ConsumerEntry("dev-consumer-disabled", null, Active: false, null, null),
        new ConsumerEntry("Dev-Consumer", null, Active: true, null, null),
    ]);

    [Theory]
    [InlineData(ConsumerRequirement.Registered)]
    [InlineData(ConsumerRequirement.Active)]
    public void AnAbsentDeclarationIsAMissingIdentity(ConsumerRequirement requirement)
    {
        var result = Resolve(requirement);

        Assert.False(result.Allowed);
        Assert.Equal(ConsumerIdentityState.Missing, result.State);
        Assert.Null(result.ConsumerId);
        Assert.Equal("missing", result.IdentityStateText);
    }

    [Theory]
    [InlineData(ConsumerRequirement.Registered, "not a consumer")]
    [InlineData(ConsumerRequirement.Registered, "consumer/../id")]
    [InlineData(ConsumerRequirement.Registered, "two values, joined")]
    [InlineData(ConsumerRequirement.Active, "not a consumer")]
    [InlineData(ConsumerRequirement.Active, "two values, joined")]
    public void ADeclarationThatCannotBeAnEntryIsMalformed(ConsumerRequirement requirement, string declared)
    {
        var result = Resolve(requirement, declared);

        Assert.False(result.Allowed);
        Assert.Equal(ConsumerIdentityState.Malformed, result.State);
        Assert.Equal("malformed", result.IdentityStateText);
    }

    [Theory]
    [InlineData(ConsumerRequirement.Registered)]
    [InlineData(ConsumerRequirement.Active)]
    public void AnEmptyDeclarationIsRefusedAndNeverAdmitted(ConsumerRequirement requirement)
    {
        var result = Resolve(requirement, string.Empty);

        Assert.False(result.Allowed);
        Assert.Null(result.ConsumerId);
        Assert.True(result.State is ConsumerIdentityState.Missing or ConsumerIdentityState.Malformed);
    }

    [Theory]
    [InlineData(ConsumerRequirement.Registered)]
    [InlineData(ConsumerRequirement.Active)]
    public void ADeclarationRepeatedIntoTwoValuesIsMalformed(ConsumerRequirement requirement)
    {
        var result = Resolve(requirement, "dev-consumer", "dev-consumer");

        Assert.Equal(ConsumerIdentityState.Malformed, result.State);
    }

    [Theory]
    [InlineData(ConsumerRequirement.Registered)]
    [InlineData(ConsumerRequirement.Active)]
    public void AnIdentityTheRegistryDoesNotCarryIsUnregistered(ConsumerRequirement requirement)
    {
        var result = Resolve(requirement, "someone-else");

        Assert.False(result.Allowed);
        Assert.Equal(ConsumerIdentityState.Unregistered, result.State);
        Assert.Equal("unregistered", result.IdentityStateText);
    }

    [Fact]
    public void TheLookupIsOrdinalSoACaseDifferenceIsNotTheSameConsumer()
    {
        Assert.Equal(ConsumerIdentityState.Unregistered, Resolve(ConsumerRequirement.Registered, "DEV-CONSUMER").State);
        Assert.True(Resolve(ConsumerRequirement.Registered, "Dev-Consumer").Allowed);
    }

    [Theory]
    [InlineData(ConsumerRequirement.Registered)]
    [InlineData(ConsumerRequirement.Active)]
    public void AnActiveConsumerIsAdmitted(ConsumerRequirement requirement)
    {
        var result = Resolve(requirement, "dev-consumer");

        Assert.True(result.Allowed);
        Assert.Equal("dev-consumer", result.ConsumerId);
        Assert.Null(result.State);
    }

    [Fact]
    public void AnInactiveConsumerMayReadButNotOpenAnUpload()
    {
        var reading = Resolve(ConsumerRequirement.Registered, "dev-consumer-disabled");
        var opening = Resolve(ConsumerRequirement.Active, "dev-consumer-disabled");

        Assert.True(reading.Allowed);
        Assert.False(opening.Allowed);
        Assert.True(opening.Inactive);
        Assert.Equal("dev-consumer-disabled", opening.ConsumerId);
        Assert.Equal("inactive", opening.IdentityStateText);
    }

    [Fact]
    public void ASnapshotSwappedBetweenTwoCallsIsSeenOnlyByTheSecond()
    {
        var first = Resolve(ConsumerRequirement.Active, "dev-consumer", registry: Registry);
        var afterChange = ConsumerRegistrySnapshot.Create([new ConsumerEntry("dev-consumer", null, Active: false, null, null)]);
        var second = Resolve(ConsumerRequirement.Active, "dev-consumer", registry: afterChange);

        Assert.True(first.Allowed);
        Assert.True(second.Inactive);
    }

    private static ConsumerAccessResult Resolve(
        ConsumerRequirement requirement,
        string declared,
        ConsumerRegistrySnapshot registry) => Resolve(requirement, [declared], registry);

    private static ConsumerAccessResult Resolve(
        ConsumerRequirement requirement,
        string[] declared,
        ConsumerRegistrySnapshot registry)
    {
        var context = new DefaultHttpContext();
        foreach (var value in declared)
        {
            context.Request.Headers.Append(ConsumerIdentity.HeaderName, value);
        }

        return ConsumerAccess.Resolve(context.Request, registry, requirement);
    }

    private static ConsumerAccessResult Resolve(ConsumerRequirement requirement, params string[] declared) =>
        Resolve(requirement, declared, Registry);
}
