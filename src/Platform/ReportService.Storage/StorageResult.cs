namespace ReportService.Storage;

/// <summary>
/// The result of a storage operation: an outcome, a value on success and an
/// error otherwise. A success has a value and no error; every other outcome
/// has an error and no value.
/// </summary>
/// <typeparam name="T">The type of the value of a successful operation.</typeparam>
public sealed record StorageResult<T>
{
    internal StorageResult(StorageOutcome outcome, T? value, StorageError? error)
    {
        Outcome = outcome;
        Value = value;
        Error = error;
    }

    /// <summary>The outcome of the operation.</summary>
    public StorageOutcome Outcome { get; }

    /// <summary>The value of a successful operation; default otherwise.</summary>
    public T? Value { get; }

    /// <summary>Why the operation did not succeed; null on success.</summary>
    public StorageError? Error { get; }

    /// <summary>True when <see cref="Outcome"/> is <see cref="StorageOutcome.Success"/>.</summary>
    public bool Succeeded => Outcome == StorageOutcome.Success;
}

/// <summary>Creates <see cref="StorageResult{T}"/> values.</summary>
public static class StorageResult
{
    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static StorageResult<T> Success<T>(T value) => new(StorageOutcome.Success, value, null);

    /// <summary>A failed result; <paramref name="outcome"/> may not be <see cref="StorageOutcome.Success"/>.</summary>
    public static StorageResult<T> Failure<T>(StorageOutcome outcome, StorageError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (outcome == StorageOutcome.Success)
        {
            throw new ArgumentException("A failure needs an outcome other than success.", nameof(outcome));
        }

        return new(outcome, default, error);
    }

    /// <summary>A failed result carrying the outcome and error of <paramref name="exception"/>.</summary>
    public static StorageResult<T> Failure<T>(StorageException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Failure<T>(exception.Outcome, exception.Error);
    }
}
