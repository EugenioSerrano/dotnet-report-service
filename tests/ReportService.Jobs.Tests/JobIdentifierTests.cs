using ReportService.Jobs;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The identifiers a job and its outputs carry: the form this service creates and
/// the form it admits from a caller, at both ends of the length and across the
/// characters that would make a reference unusable.
/// </summary>
public sealed class JobIdentifierTests
{
    [Fact]
    public void ANewJobIdentifierHasThePrefixAndThirtyTwoHexadecimalCharacters()
    {
        var created = JobIdentifier.Create();

        Assert.StartsWith(JobIdentifier.JobPrefix, created, StringComparison.Ordinal);
        Assert.Equal(JobIdentifier.JobPrefix.Length + JobIdentifier.RandomHexLength, created.Length);
        Assert.True(JobIdentifier.IsWellFormed(created));
        Assert.False(JobIdentifier.IsWellFormedOutput(created));
    }

    [Fact]
    public void ANewOutputIdentifierIsWellFormedAndIsNotAJobIdentifier()
    {
        var created = JobIdentifier.CreateOutput();

        Assert.StartsWith(JobIdentifier.OutputPrefix, created, StringComparison.Ordinal);
        Assert.True(JobIdentifier.IsWellFormedOutput(created));
        Assert.False(JobIdentifier.IsWellFormed(created));
    }

    [Fact]
    public void TwoIdentifiersOfOneKindDiffer()
    {
        Assert.NotEqual(JobIdentifier.Create(), JobIdentifier.Create());
        Assert.NotEqual(JobIdentifier.CreateOutput(), JobIdentifier.CreateOutput());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("job-")]
    [InlineData("job-0123456789abcdef0123456789abcde")]
    [InlineData("job-0123456789abcdef0123456789abcdef0")]
    [InlineData("job-0123456789ABCDEF0123456789abcdef")]
    [InlineData("job-0123456789abcdef0123456789abcdeg")]
    [InlineData("job 0123456789abcdef0123456789abcdef")]
    [InlineData("Job-0123456789abcdef0123456789abcdef")]
    public void AValueOutsideTheGrammarIsRefused(string? value)
    {
        Assert.False(JobIdentifier.IsWellFormed(value));
        Assert.False(JobIdentifier.IsWellFormedOutput(value));
    }

    [Theory]
    [InlineData("job-0123456789abcdef0123456789abcdef")]
    [InlineData("job-00000000000000000000000000000000")]
    [InlineData("job-ffffffffffffffffffffffffffffffff")]
    public void AValueInTheGrammarIsAdmitted(string value) => Assert.True(JobIdentifier.IsWellFormed(value));

    [Theory]
    [InlineData("output-0123456789abcdef0123456789abcdef")]
    [InlineData("output-ffffffffffffffffffffffffffffffff")]
    public void AnOutputValueInTheGrammarIsAdmitted(string value) => Assert.True(JobIdentifier.IsWellFormedOutput(value));
}
