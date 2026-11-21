namespace ReportService.Storage;

/// <summary>The text form of a SHA-256 used by the port: 64 lower-case hexadecimal characters.</summary>
public static class Sha256Text
{
    /// <summary>The length of the text form.</summary>
    public const int Length = 64;

    /// <summary>True when <paramref name="value"/> is 64 lower-case hexadecimal characters.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
