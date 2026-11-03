namespace ReportService.DataContract;

/// <summary>
/// A value could not be decided against the contract at all — the contract needed
/// more time than a pattern may take, more nesting than the service evaluates, or the
/// evaluator refused the work. It is not a violation of the contract: nothing about
/// the value is known. The message names the kind and the keyword, never a value.
/// </summary>
public sealed class ContractEvaluationFailed : Exception
{
    /// <summary>A pattern needed more time than the contract allows for one match.</summary>
    public const string PatternTime = "pattern-time";

    /// <summary>The value is nested deeper than the contract evaluates.</summary>
    public const string Depth = "depth";

    /// <summary>The evaluator failed for a reason the contract does not classify.</summary>
    public const string Evaluator = "evaluator";

    /// <summary>Describes a value the contract could not decide.</summary>
    /// <param name="kind">One of <see cref="PatternTime"/>, <see cref="Depth"/> or <see cref="Evaluator"/>.</param>
    /// <param name="keyword">The keyword the evaluation stopped at, when it is known.</param>
    /// <param name="inner">The evaluator's own failure, kept off the message.</param>
    public ContractEvaluationFailed(string kind, string? keyword, Exception? inner = null)
        : base(Describe(kind, keyword), inner)
    {
        Kind = kind;
        Keyword = keyword;
    }

    /// <summary>What the evaluator could not do.</summary>
    public string Kind { get; }

    /// <summary>The keyword the evaluation stopped at, when it is known.</summary>
    public string? Keyword { get; }

    private static string Describe(string kind, string? keyword) =>
        keyword is null
            ? "the value could not be decided: kind=" + kind
            : "the value could not be decided: kind=" + kind + " keyword=" + keyword;
}
