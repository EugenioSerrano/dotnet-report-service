using ReportService.Api.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The deployment's restriction of the <c>high</c> priority: what each kind admits, the
/// settings a valid policy has, and the start that stops when the policy is invalid or names
/// a consumer the registry does not declare. The policy is static: nothing here counts work.
/// </summary>
public sealed class HighPriorityPolicyTests
{
    private const string Restriction = HighPriorityPolicy.SectionPath + ":" + HighPriorityPolicy.RestrictionSetting;

    private const string Allowed = HighPriorityPolicy.SectionPath + ":" + HighPriorityPolicy.AllowedConsumersSetting;

    [Fact]
    public void ByDefaultTheRestrictionIsNoneAndAdmitsEveryConsumer()
    {
        var policy = new HighPriorityPolicy();

        Assert.Equal(HighPriorityPolicy.NoRestriction, policy.Restriction);
        Assert.True(policy.AdmitsHigh("any-consumer"));
        Assert.Empty(policy.Validate());
    }

    [Fact]
    public void AnAllowListAdmitsExactlyTheConsumersItNames()
    {
        var policy = new HighPriorityPolicy { Restriction = HighPriorityPolicy.AllowListRestriction };
        policy.AllowedConsumers.Add("consumer-a");

        Assert.True(policy.AdmitsHigh("consumer-a"));
        Assert.False(policy.AdmitsHigh("consumer-b"));
        Assert.False(policy.AdmitsHigh("Consumer-A"));
        Assert.Empty(policy.Validate());
    }

    [Fact]
    public void ADisabledHighAdmitsNoConsumer()
    {
        var policy = new HighPriorityPolicy { Restriction = HighPriorityPolicy.DisabledRestriction };

        Assert.False(policy.AdmitsHigh("consumer-a"));
        Assert.Empty(policy.Validate());
    }

    [Fact]
    public void AnUnknownRestrictionIsInvalid()
    {
        var problem = Assert.Single(new HighPriorityPolicy { Restriction = "cap" }.Validate());

        Assert.Equal(HighPriorityPolicy.RestrictionSetting, problem.Setting);
    }

    [Fact]
    public void AnEmptyAllowListIsInvalid()
    {
        var problem = Assert.Single(new HighPriorityPolicy { Restriction = HighPriorityPolicy.AllowListRestriction }.Validate());

        Assert.Equal(HighPriorityPolicy.AllowedConsumersSetting, problem.Setting);
    }

    [Fact]
    public void AnAllowListWithABlankEntryIsInvalid()
    {
        var policy = new HighPriorityPolicy { Restriction = HighPriorityPolicy.AllowListRestriction };
        policy.AllowedConsumers.Add(" ");

        Assert.Equal(HighPriorityPolicy.AllowedConsumersSetting, Assert.Single(policy.Validate()).Setting);
    }

    [Theory]
    [InlineData(HighPriorityPolicy.NoRestriction)]
    [InlineData(HighPriorityPolicy.DisabledRestriction)]
    public void ConsumersListedWithoutAnAllowListAreInvalid(string restriction)
    {
        var policy = new HighPriorityPolicy { Restriction = restriction };
        policy.AllowedConsumers.Add("consumer-a");

        Assert.Equal(HighPriorityPolicy.AllowedConsumersSetting, Assert.Single(policy.Validate()).Setting);
    }

    [Fact]
    public void AnAllowListNamingAnUndeclaredConsumerFailsTheRegistryCheckWithoutListingIt()
    {
        var policy = new HighPriorityPolicy { Restriction = HighPriorityPolicy.AllowListRestriction };
        policy.AllowedConsumers.Add("ghost-consumer");

        var problem = Assert.Single(policy.CheckAgainstRegistry(["consumer-a"]));

        Assert.DoesNotContain("ghost-consumer", problem.Reason, StringComparison.Ordinal);
        Assert.Empty(new HighPriorityPolicy().CheckAgainstRegistry([]));
    }

    [Fact]
    public void TheApiStopsAtStartForAnUnknownRestriction()
    {
        using var probe = Probe(new Dictionary<string, string?>(StringComparer.Ordinal) { [Restriction] = "cap" });

        Assert.Contains(HighPriorityPolicy.RestrictionSetting, probe.Refusal().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApiStopsAtStartForAnAllowListNamingAConsumerTheRegistryDoesNotDeclare()
    {
        using var probe = Probe(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Restriction] = HighPriorityPolicy.AllowListRestriction,
            [Allowed + ":0"] = "ghost-consumer",
        });

        var error = probe.Refusal();

        Assert.Contains(HighPriorityPolicy.AllowedConsumersSetting, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghost-consumer", error.Message, StringComparison.Ordinal);
    }

    private static StartFailureProbe Probe(Dictionary<string, string?> settings)
    {
        settings["Storage:Filesystem:Root"] = Path.Combine(Path.GetTempPath(), "priority-policy-" + Guid.NewGuid().ToString("N"));
        settings[ReportService.ServiceDefaults.DatabaseOptions.ConnectionStringSetting] = MetadataDatabaseStub.ConnectionString;
        settings[ReportService.ServiceDefaults.DatabaseOptions.ExpectedSchemaVersionSetting] = MetadataDatabaseStub.ExpectedVersion;
        return StartFailureProbe.Create(settings, replacements: services => MetadataDatabaseStub.UseHealthy(services));
    }
}
