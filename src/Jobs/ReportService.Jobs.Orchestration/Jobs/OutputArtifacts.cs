using System.Security.Cryptography;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The identifier every published artifact carries, whatever its output family: one rule, so two
/// families can never mint identifiers that look alike but differ.
/// </summary>
public static class OutputArtifacts
{
    /// <summary>The prefix of every artifact identifier.</summary>
    public const string ArtifactPrefix = "artifact-";

    /// <summary>Creates an artifact identifier: its prefix and 128 random bits, lower-case hexadecimal.</summary>
    /// <returns>The identifier.</returns>
    public static string NewArtifactId() =>
        ArtifactPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
