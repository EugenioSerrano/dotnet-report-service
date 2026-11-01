namespace ReportService.Storage.Tests;

/// <summary>Outcomes, errors, the port's exceptions, capabilities and the SHA-256 text form.</summary>
public sealed class StorageResultTests
{
    private static readonly StorageError SomeError = new(StorageErrorCodes.IoError, "The operation failed.");

    [Fact]
    public void SuccessHasAValueAndNoError()
    {
        var result = StorageResult.Success(42);

        Assert.True(result.Succeeded);
        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FailureHasAnErrorAndNoValue()
    {
        var result = StorageResult.Failure<string>(StorageOutcome.Transient, SomeError);

        Assert.False(result.Succeeded);
        Assert.Equal(StorageOutcome.Transient, result.Outcome);
        Assert.Null(result.Value);
        Assert.Same(SomeError, result.Error);
    }

    [Fact]
    public void FailureCarriesTheOutcomeAndErrorOfAStorageException()
    {
        var result = StorageResult.Failure<int>(new StorageException(StorageOutcome.Permanent, SomeError));

        Assert.Equal(StorageOutcome.Permanent, result.Outcome);
        Assert.Same(SomeError, result.Error);
    }

    [Fact]
    public void FailureNeedsAnErrorAndAnOutcomeOtherThanSuccess()
    {
        Assert.Throws<ArgumentException>(() => StorageResult.Failure<int>(StorageOutcome.Success, SomeError));
        Assert.Throws<ArgumentNullException>(() => StorageResult.Failure<int>(StorageOutcome.Permanent, null!));
        Assert.Throws<ArgumentNullException>(() => StorageResult.Failure<int>(null!));
    }

    [Fact]
    public void StorageExceptionUsesTheErrorMessageAndRefusesSuccess()
    {
        var inner = new IOException("inner");
        var exception = new StorageException(StorageOutcome.Transient, SomeError with { Inner = inner });

        Assert.Equal("The operation failed.", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal(StorageOutcome.Transient, exception.Outcome);
        Assert.Throws<ArgumentException>(() => new StorageException(StorageOutcome.Success, SomeError));
        Assert.Throws<ArgumentNullException>(() => new StorageException(StorageOutcome.Permanent, null!));
    }

    [Fact]
    public void IntegrityExceptionDescribesTheMismatchWithoutAKey()
    {
        var hash = new StorageIntegrityException(10, 10, new string('a', 64), new string('b', 64));
        var size = new StorageIntegrityException(10, 9, new string('a', 64), new string('b', 64));

        Assert.Equal(StorageErrorCodes.HashMismatch, hash.Error.Code);
        Assert.Equal($"The object's SHA-256 is {new string('b', 64)}; {new string('a', 64)} was expected.", hash.Message);
        Assert.Equal(StorageErrorCodes.SizeMismatch, size.Error.Code);
        Assert.Equal("The object has 9 bytes; 10 were expected.", size.Message);
        Assert.Equal(StorageOutcome.IntegrityFailure, size.Outcome);
    }

    [Fact]
    public void ConfigurationExceptionNamesTheSettingOnly()
    {
        var exception = new StorageConfigurationException("Storage:Filesystem:Root", "the directory does not exist");

        Assert.Equal("setting 'Storage:Filesystem:Root': the directory does not exist", exception.Message);
        Assert.Equal("Storage:Filesystem:Root", exception.Setting);
        Assert.Equal("the directory does not exist", exception.Reason);
    }

    [Fact]
    public void CapabilitiesHaveNoneAndAll()
    {
        Assert.Equal(new StorageCapabilities(false, false, false, false), StorageCapabilities.None);
        Assert.Equal(new StorageCapabilities(true, true, true, true), StorageCapabilities.All);
        Assert.True(StorageCapabilities.None with { NativeCreateOnly = true } is { NativeCreateOnly: true, SignedUpload: false });
    }

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", false)]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a", false)]
    [InlineData("ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", false)]
    [InlineData(null, false)]
    public void Sha256TextIs64LowerCaseHexadecimalCharacters(string? value, bool valid) =>
        Assert.Equal(valid, Sha256Text.IsValid(value));

    [Fact]
    public void RecordsCarryWhatTheyAreGiven()
    {
        var key = ObjectKey.Create("a/b");
        var modified = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new ObjectWrite(key, 3, "x", null), new ObjectWrite(key, 3, "x", null));
        Assert.Equal(modified, new ObjectInfo(key, 3, modified).LastModified);
        Assert.Equal("Root", new StorageSettingProblem("Root", "is required").Setting);
    }
}
