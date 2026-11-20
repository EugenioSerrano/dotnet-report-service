using System.Reflection;
using ReportService.Api.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The obligation a later member of the job request carries: every leaf the request grows is
/// either encoded into the request's identity or listed as an exclusion with its reason. A
/// member added without a decision fails here, so a request the service treats as the same as
/// another cannot differ in something nobody remembered. The declared list is the encoder's
/// own table, so a name in it is always a member that is actually written.
/// </summary>
public sealed class RequestFingerprintCoverageTests
{
    // Members of the request that are deliberately not part of its identity, each with the
    // reason. Empty today: every member of the request is something the consumer asked for.
    private static readonly Dictionary<string, string> Exclusions = [];

    [Fact]
    public void EveryLeafOfTheRequestIsEncodedOrExcludedWithAReason()
    {
        var declared = RequestFingerprintEncoder.Members
            .Select(member => member.RequestProperty)
            .ToHashSet(StringComparer.Ordinal);
        var leaves = typeof(CreateReportJobRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.All(
            leaves,
            leaf => Assert.True(
                declared.Contains(leaf) || Exclusions.ContainsKey(leaf),
                $"the request member '{leaf}' is neither in the identity nor excluded with a reason"));
        Assert.All(
            Exclusions,
            exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Value), $"the exclusion '{exclusion.Key}' carries no reason"));
    }

    [Fact]
    public void EveryEncodedMemberReadsAPropertyTheRequestHas()
    {
        var leaves = typeof(CreateReportJobRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            RequestFingerprintEncoder.Members.Where(member => member.Name != RequestFingerprintEncoder.ConsumerMember),
            member => Assert.True(
                leaves.Contains(member.RequestProperty),
                $"the identity's member '{member.Name}' reads '{member.RequestProperty}', which the request does not carry"));
    }

    [Fact]
    public void EveryMemberIsNamedOnce()
    {
        Assert.Equal(
            RequestFingerprintEncoder.Members.Count,
            RequestFingerprintEncoder.Members.Select(member => member.Name).Distinct(StringComparer.Ordinal).Count());
    }
}
