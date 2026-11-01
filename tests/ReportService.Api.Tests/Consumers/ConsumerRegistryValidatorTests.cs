using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ReportService.Api.Consumers;

namespace ReportService.Api.Tests.Consumers;

/// <summary>Validation of the consumer registry: each faulty case is named by entry, never by a value.</summary>
public sealed class ConsumerRegistryValidatorTests : IDisposable
{
    private const string PlantedSecret = "planted-secret-c41e07";
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ValidEntriesBecomeASnapshotInFileOrder()
    {
        var result = Validate(
            ("0:consumerId", "b-consumer"), ("0:displayName", "B"), ("0:active", "true"),
            ("1:consumerId", "a.consumer_1-x"), ("1:active", "false"), ("1:authenticationSubject", "subject"), ("1:createdAt", "2026-11-01"));

        Assert.Empty(result.Problems);
        var snapshot = Assert.IsType<ConsumerRegistrySnapshot>(result.Snapshot);
        Assert.Equal(["b-consumer", "a.consumer_1-x"], snapshot.Entries.Select(e => e.ConsumerId));
        Assert.Equal(new ConsumerEntry("b-consumer", "B", true, null, null), snapshot.Entries[0]);
        Assert.Equal(new ConsumerEntry("a.consumer_1-x", null, false, "subject", "2026-11-01"), snapshot.Entries[1]);
    }

    [Fact]
    public void FingerprintIsTheHashOfTheSortedIdentitiesAndActiveStates()
    {
        var result = Validate(("0:consumerId", "b"), ("0:active", "true"), ("1:consumerId", "a"), ("1:active", "false"), ("1:displayName", "ignored by the hash"));

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a\tfalse\nb\ttrue")));
        Assert.Equal(expected, result.Snapshot!.Fingerprint);
    }

    [Fact]
    public void EmptyIdentityIsNamedByIndex()
    {
        var problem = Assert.Single(Validate(("0:consumerId", "  "), ("0:active", "true")).Problems);

        Assert.Equal("consumers[0]", problem.Setting);
        Assert.Equal("consumerId is empty", problem.Reason);
    }

    [Fact]
    public void MissingIdentityIsNamedByIndex()
    {
        var problem = Assert.Single(Validate(("0:active", "true")).Problems);

        Assert.Equal("consumers[0]", problem.Setting);
        Assert.Equal("consumerId is empty", problem.Reason);
    }

    [Fact]
    public void DuplicateIdentityNamesBothEntries()
    {
        var problem = Assert.Single(Validate(
            ("0:consumerId", "dev-consumer"), ("0:active", "true"),
            ("1:consumerId", "dev-consumer"), ("1:active", "false")).Problems);

        Assert.Equal("consumers[1]", problem.Setting);
        Assert.Equal("consumer 'dev-consumer' duplicates consumers[0]", problem.Reason);
    }

    [Fact]
    public void IdentitiesDifferingOnlyInCaseAreDistinct()
    {
        var result = Validate(("0:consumerId", "Dev"), ("0:active", "true"), ("1:consumerId", "dev"), ("1:active", "true"));

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Snapshot!.Entries.Count);
    }

    [Fact]
    public void MissingActiveStateIsNamedWithTheIdentity()
    {
        var problem = Assert.Single(Validate(("0:consumerId", "dev-consumer")).Problems);

        Assert.Equal("consumers[0]", problem.Setting);
        Assert.Equal("consumer 'dev-consumer': active is required", problem.Reason);
    }

    [Fact]
    public void NonBooleanActiveStateIsRefusedWithoutEchoingIt()
    {
        var problem = Assert.Single(Validate(("0:consumerId", "dev-consumer"), ("0:active", PlantedSecret)).Problems);

        Assert.Equal("consumer 'dev-consumer': active must be true or false", problem.Reason);
        Assert.DoesNotContain(PlantedSecret, problem.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev consumer")]
    [InlineData("dev/consumer")]
    [InlineData("dév")]
    [InlineData(" dev")]
    public void ForbiddenCharacterIsRefusedWithoutEchoingTheIdentity(string identity)
    {
        var problem = Assert.Single(Validate(("0:consumerId", identity), ("0:active", "true")).Problems);

        Assert.Equal("consumers[0]", problem.Setting);
        Assert.Equal("consumerId has a character outside A-Z, a-z, 0-9, '.', '_' and '-'", problem.Reason);
    }

    [Fact]
    public void IdentityOfOneHundredTwentyEightCharactersIsAccepted()
    {
        Assert.Empty(Validate(("0:consumerId", new string('a', 128)), ("0:active", "true")).Problems);
    }

    [Fact]
    public void IdentityAboveOneHundredTwentyEightCharactersIsRefused()
    {
        var problem = Assert.Single(Validate(("0:consumerId", new string('a', 129)), ("0:active", "true")).Problems);

        Assert.Equal("consumers[0]", problem.Setting);
        Assert.Equal("consumerId is longer than 128 characters", problem.Reason);
    }

    [Fact]
    public void DisplayNameOfTwoHundredCharactersIsAcceptedAndOneMoreIsRefused()
    {
        Assert.Empty(Validate(("0:consumerId", "a"), ("0:active", "true"), ("0:displayName", new string('x', 200))).Problems);

        var problem = Assert.Single(Validate(("0:consumerId", "a"), ("0:active", "true"), ("0:displayName", new string('x', 201))).Problems);
        Assert.Equal("consumer 'a': displayName is longer than 200 characters", problem.Reason);
    }

    [Fact]
    public void UnknownPropertyIsIgnoredWithOneWarningNamingItButNotItsValue()
    {
        var result = Validate(("0:consumerId", "a"), ("0:active", "true"), ("0:apiKey", PlantedSecret));

        Assert.Empty(result.Problems);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("consumers[0] (consumer 'a'): unknown property 'apiKey' is ignored", warning);
        Assert.DoesNotContain(PlantedSecret, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedUnknownPropertyIsIgnoredToo()
    {
        var result = Validate(("0:consumerId", "a"), ("0:active", "true"), ("0:extra:key", PlantedSecret));

        Assert.Empty(result.Problems);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void EveryFaultyEntryIsReported()
    {
        var result = Validate(("0:consumerId", ""), ("0:active", "true"), ("1:consumerId", "b"));

        Assert.Equal(["consumers[0]", "consumers[1]"], result.Problems.Select(p => p.Setting));
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void EmptyRegistryFileIsAValidEmptySnapshot()
    {
        var configuration = FromJson("""{ "consumers": [] }""");

        var result = ConsumerRegistryValidator.Validate(configuration);

        Assert.Empty(result.Problems);
        Assert.Empty(result.Snapshot!.Entries);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), result.Snapshot.Fingerprint);
    }

    [Fact]
    public void FileWithoutAConsumersListIsRefused()
    {
        var problem = Assert.Single(ConsumerRegistryValidator.Validate(FromJson("""{ "other": 1 }""")).Problems);

        Assert.Equal("consumers", problem.Setting);
        Assert.Equal("the registry has no consumers list (the file is missing, empty or not valid JSON)", problem.Reason);
    }

    [Fact]
    public void ConsumersThatIsNotAListIsRefused()
    {
        var problem = Assert.Single(ConsumerRegistryValidator.Validate(FromJson("""{ "consumers": "x" }""")).Problems);

        Assert.Equal("consumers", problem.Setting);
        Assert.Equal("consumers must be a list of entries", problem.Reason);
    }

    [Fact]
    public void ConsumersObjectWithNamedEntriesIsRefused()
    {
        var problem = Assert.Single(ConsumerRegistryValidator.Validate(FromJson("""{ "consumers": { "a": { "consumerId": "a", "active": true } } }""")).Problems);

        Assert.Equal("consumers must be a list of entries", problem.Reason);
    }

    [Fact]
    public void JsonFileWithTheDevelopmentShapeIsRead()
    {
        var configuration = FromJson("""
            {
              "consumers": [
                { "consumerId": "dev-consumer", "displayName": "Development consumer", "active": true },
                { "consumerId": "dev-consumer-disabled", "active": false }
              ]
            }
            """);

        var snapshot = ConsumerRegistryValidator.Validate(configuration).Snapshot!;

        Assert.Equal([true, false], snapshot.Entries.Select(e => e.Active));
    }

    [Fact]
    public void RepositoryDevelopmentRegistryIsValidAndHoldsOnlyDevelopmentEntries()
    {
        var path = Path.Combine(ReportService.Testing.RepositoryPaths.Root, "src", "ReportService.Api", "consumers.json");

        var snapshot = ConsumerRegistryValidator.Validate(new ConfigurationBuilder().AddJsonFile(path).Build()).Snapshot!;

        Assert.Equal(["dev-consumer", "dev-consumer-disabled"], snapshot.Entries.Select(e => e.ConsumerId));
        Assert.All(snapshot.Entries, e => Assert.Null(e.AuthenticationSubject));
    }

    private static ConsumerRegistryValidation Validate(params (string Key, string? Value)[] values) =>
        ConsumerRegistryValidator.Validate(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create("consumers:" + v.Key, v.Value)))
            .Build());

    private IConfiguration FromJson(string json) =>
        new ConfigurationBuilder().AddJsonFile(_directory.Write("consumers.json", json)).Build();
}
