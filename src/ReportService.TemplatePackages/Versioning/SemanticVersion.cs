using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ReportService.TemplatePackages.Versioning;

/// <summary>
/// A semantic version's parts and its precedence, beside the delivered syntax
/// check. <see cref="CompareTo"/> answers precedence exactly as Semantic
/// Versioning 2.0.0 section 11 defines it and nothing else: build metadata is
/// never compared, so two versions that differ only in it compare equal. Which
/// of two versions of equal precedence a by-name reference uses is a rule of
/// the resolution, never of this type.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly ulong _major;
    private readonly ulong _minor;
    private readonly ulong _patch;
    private readonly string[] _preRelease;
    private readonly string[] _build;

    private SemanticVersion(
        string text,
        ulong major,
        ulong minor,
        ulong patch,
        string[] preRelease,
        string[] build)
    {
        Text = text;
        _major = major;
        _minor = minor;
        _patch = patch;
        _preRelease = preRelease;
        _build = build;
    }

    /// <summary>The version as it was written, which is the identity a store and a source use.</summary>
    public string Text { get; }

    /// <summary>The major version.</summary>
    public ulong Major => _major;

    /// <summary>The minor version.</summary>
    public ulong Minor => _minor;

    /// <summary>The patch version.</summary>
    public ulong Patch => _patch;

    /// <summary>The dot-separated pre-release identifiers; empty for a release version.</summary>
    public IReadOnlyList<string> PreRelease => _preRelease;

    /// <summary>The dot-separated build identifiers, which precedence never compares.</summary>
    public IReadOnlyList<string> Build => _build;

    /// <summary>Parses <paramref name="text"/>, refusing every string the syntax check refuses.</summary>
    /// <param name="text">The version as written, for example <c>1.1.0+build.7</c>.</param>
    /// <param name="version">The parsed version, or <see langword="null"/> when it was refused.</param>
    /// <returns>True when <paramref name="text"/> is a semantic version this type can order.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (text is null || !SemanticVersionSyntax.IsValid(text))
        {
            return false;
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var withoutBuild = plus >= 0 ? text[..plus] : text;
        var build = plus >= 0 ? text[(plus + 1)..].Split('.') : [];

        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var preRelease = dash >= 0 ? withoutBuild[(dash + 1)..].Split('.') : [];

        var parts = core.Split('.');
        if (!ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            // A component beyond the range of its declared type: the string is
            // syntactically a version, but not one this type can represent.
            return false;
        }

        version = new SemanticVersion(text, major, minor, patch, preRelease, build);
        return true;
    }

    /// <summary>Orders by precedence: the greater the answer, the newer the version.</summary>
    /// <param name="other">The version to compare with.</param>
    /// <returns>A negative number when this version has the lower precedence, zero when they are equal, a positive number when it has the higher one.</returns>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var compared = _major.CompareTo(other._major);
        if (compared != 0)
        {
            return compared;
        }

        compared = _minor.CompareTo(other._minor);
        if (compared != 0)
        {
            return compared;
        }

        compared = _patch.CompareTo(other._patch);
        if (compared != 0)
        {
            return compared;
        }

        // A pre-release has a lower precedence than the release with the same parts.
        if (_preRelease.Length == 0)
        {
            return other._preRelease.Length == 0 ? 0 : 1;
        }

        if (other._preRelease.Length == 0)
        {
            return -1;
        }

        var shortest = Math.Min(_preRelease.Length, other._preRelease.Length);
        for (var index = 0; index < shortest; index++)
        {
            compared = CompareIdentifier(_preRelease[index], other._preRelease[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    /// <summary>Precedence equality: two versions that differ only in build metadata are equal.</summary>
    /// <param name="other">The version to compare with.</param>
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_major);
        hash.Add(_minor);
        hash.Add(_patch);
        foreach (var identifier in _preRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumeric = IsNumeric(left);
        var rightIsNumeric = IsNumeric(right);
        if (leftIsNumeric && rightIsNumeric)
        {
            // Legal numeric identifiers carry no leading zero, so the longer one
            // is the greater number and equal lengths compare ordinally.
            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
        }

        if (leftIsNumeric)
        {
            return -1;
        }

        if (rightIsNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string identifier) =>
        identifier.Length > 0 && identifier.All(character => character is >= '0' and <= '9');
}
