namespace ReportService.DataContract;

/// <summary>
/// The outcome of compiling a schema: the profile's verdict over the document and, when
/// it was admitted and the library accepted it, the prepared contract. A document the
/// profile refuses or the library cannot compile leaves <see cref="Contract"/> empty.
/// </summary>
public sealed class ContractCompilation
{
    internal ContractCompilation(ProfileResult profile, PreparedDataContract? contract)
    {
        Profile = profile;
        Contract = contract;
    }

    /// <summary>The profile's verdict over the schema document.</summary>
    public ProfileResult Profile { get; }

    /// <summary>The prepared contract, or <see langword="null"/> when the schema was refused.</summary>
    public PreparedDataContract? Contract { get; }

    /// <summary>Whether a prepared contract came out of the compilation.</summary>
    public bool Succeeded => Contract is not null;
}
