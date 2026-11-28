namespace ReportService.Templates;

/// <summary>One presentation function, bound to a block's settings, taking the arguments a template passed.</summary>
/// <param name="arguments">The arguments, in the order the template wrote them.</param>
public delegate PresentationResult PresentationFunction(IReadOnlyList<object?> arguments);

/// <summary>
/// The one table of presentation function names: the only place a name is written.
/// <para>
/// A table is bound to one block's settings, so the culture, the zone and the clock a
/// function presents with are the block's and nothing a template can pass. The three
/// engines read this table and add no function of their own.
/// </para>
/// </summary>
public static class PresentationFunctionTable
{
    /// <summary>The number of names the table holds.</summary>
    public const int FunctionCount = 10;

    /// <summary>Presents a number in the job's culture.</summary>
    public const string FormatNumberName = "format_number";

    /// <summary>Presents a date or an instant in the job's culture and zone.</summary>
    public const string FormatDateName = "format_date";

    /// <summary>Whether the left number is greater than the right one.</summary>
    public const string NumGreaterThanName = "num_gt";

    /// <summary>Whether the left number is greater than or equal to the right one.</summary>
    public const string NumGreaterOrEqualName = "num_ge";

    /// <summary>Whether the left number is less than the right one.</summary>
    public const string NumLessThanName = "num_lt";

    /// <summary>Whether the left number is less than or equal to the right one.</summary>
    public const string NumLessOrEqualName = "num_le";

    /// <summary>Whether the left number equals the right one.</summary>
    public const string NumEqualName = "num_eq";

    /// <summary>Whether the left number differs from the right one.</summary>
    public const string NumNotEqualName = "num_ne";

    /// <summary>The sign of a number as text for printing.</summary>
    public const string NumSignName = "num_sign";

    /// <summary>Writes a number culture-invariantly, for an attribute or a CSS length.</summary>
    public const string NumInvariantName = "num_invariant";

    /// <summary>The names, in the order they are documented.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        FormatNumberName,
        FormatDateName,
        NumGreaterThanName,
        NumGreaterOrEqualName,
        NumLessThanName,
        NumLessOrEqualName,
        NumEqualName,
        NumNotEqualName,
        NumSignName,
        NumInvariantName,
    ];

    /// <summary>Binds the table to <paramref name="settings"/>.</summary>
    /// <param name="settings">The block's presentation settings.</param>
    public static IReadOnlyDictionary<string, PresentationFunction> For(PresentationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var functions = new PresentationFunctions(settings);
        return new Dictionary<string, PresentationFunction>(StringComparer.Ordinal)
        {
            [FormatNumberName] = arguments => Arity(arguments, 2, () => functions.FormatNumber(arguments[0], Text(arguments[1]))),
            [FormatDateName] = arguments => Arity(arguments, 2, () => functions.FormatDate(arguments[0], Text(arguments[1]))),
            [NumGreaterThanName] = arguments => Arity(arguments, 2, () => functions.NumGreaterThan(arguments[0], arguments[1])),
            [NumGreaterOrEqualName] = arguments => Arity(arguments, 2, () => functions.NumGreaterOrEqual(arguments[0], arguments[1])),
            [NumLessThanName] = arguments => Arity(arguments, 2, () => functions.NumLessThan(arguments[0], arguments[1])),
            [NumLessOrEqualName] = arguments => Arity(arguments, 2, () => functions.NumLessOrEqual(arguments[0], arguments[1])),
            [NumEqualName] = arguments => Arity(arguments, 2, () => functions.NumEqual(arguments[0], arguments[1])),
            [NumNotEqualName] = arguments => Arity(arguments, 2, () => functions.NumNotEqual(arguments[0], arguments[1])),
            [NumSignName] = arguments => Arity(arguments, 1, () => functions.NumSign(arguments[0])),
            [NumInvariantName] = arguments => Arity(arguments, 1, 2, () => functions.NumInvariant(arguments[0], arguments.Count == 2 ? arguments[1] : null)),
        };
    }

    private static PresentationResult Arity(IReadOnlyList<object?> arguments, int expected, Func<PresentationResult> call) =>
        arguments.Count == expected
            ? call()
            : new PresentationResult.Refused(PresentationRefusal.BadFormat);

    private static PresentationResult Arity(IReadOnlyList<object?> arguments, int fewest, int most, Func<PresentationResult> call) =>
        arguments.Count >= fewest && arguments.Count <= most
            ? call()
            : new PresentationResult.Refused(PresentationRefusal.BadFormat);

    private static string? Text(object? value) => value as string;
}
