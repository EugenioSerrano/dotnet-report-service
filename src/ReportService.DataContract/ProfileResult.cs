namespace ReportService.DataContract;

/// <summary>
/// The verdict of the profile check over a schema document: either the document is
/// inside the profile, or it is refused by one rule at one place inside the schema.
/// A refusal names the rule and a pointer into the schema, never a value of it.
/// </summary>
public sealed class ProfileResult
{
    private ProfileResult(bool isValid, string? rule, string? schemaPointer)
    {
        IsValid = isValid;
        Rule = rule;
        SchemaPointer = schemaPointer;
    }

    /// <summary>The document is inside the profile.</summary>
    public static ProfileResult Valid { get; } = new(true, null, null);

    /// <summary>The document is inside the profile.</summary>
    public bool IsValid { get; }

    /// <summary>The rule that refused the document, or <see langword="null"/> when it was admitted.</summary>
    public string? Rule { get; }

    /// <summary>A JSON pointer to the place in the schema the rule refused.</summary>
    public string? SchemaPointer { get; }

    /// <summary>Refuses a document by rule and place.</summary>
    /// <param name="rule">The rule of the profile that refused it.</param>
    /// <param name="schemaPointer">The JSON pointer into the schema.</param>
    public static ProfileResult Refused(string rule, string schemaPointer) => new(false, rule, schemaPointer);
}
