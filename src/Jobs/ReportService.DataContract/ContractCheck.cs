namespace ReportService.DataContract;

/// <summary>
/// The result of evaluating one value against one root of a prepared contract:
/// either the value conforms, or it breaks the contract at a path and a keyword.
/// The path is relative to the value that was evaluated, in the <c>$.…</c> form the
/// diagnostics use, and the keyword is the rule that failed. Neither carries a value.
/// </summary>
public sealed class ContractCheck
{
    private ContractCheck(string? path, string? keyword)
    {
        Path = path;
        Keyword = keyword;
    }

    /// <summary>The value conforms to the contract.</summary>
    public static ContractCheck Valid { get; } = new(null, null);

    /// <summary>The value conforms to the contract.</summary>
    public bool IsValid => Keyword is null;

    /// <summary>Where the value breaks the contract, relative to the evaluated value.</summary>
    public string? Path { get; }

    /// <summary>The keyword of the contract the value breaks.</summary>
    public string? Keyword { get; }

    /// <summary>Reports a violation at a path and a keyword.</summary>
    /// <param name="path">The path of the value that broke the contract.</param>
    /// <param name="keyword">The keyword that failed.</param>
    public static ContractCheck Violation(string path, string keyword) => new(path, keyword);
}
