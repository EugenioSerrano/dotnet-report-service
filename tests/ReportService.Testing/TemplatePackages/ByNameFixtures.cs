using ReportService.Testing.TemplateSource;

namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// The template family a by-name resolution is exercised against, built by this
/// helper into the template-source double and never checked in: a repository
/// whose version folders are the cases of the resolution — the numeric order,
/// the invalid newer version, the pre-release, the name that is no version at
/// all, the version this deployment cannot render, two versions of equal
/// precedence, another consumer's version, and a template folder with no version
/// underneath it.
/// <para>
/// The family lists what it <b>can</b> seed, not a state every case sees: the
/// case's own Given decides which versions it seeds, so a criterion is never
/// answered by a version it does not describe. One rule never bends: no version
/// of <see cref="Template"/> shares the precedence of another, so only the
/// equal-precedence family meets the tie-break between two builds of one version.
/// </para>
/// </summary>
public static class ByNameFixtures
{
    /// <summary>The template the resolution cases read by name.</summary>
    public const string Template = "invoice-energy";

    /// <summary>The template that holds two release versions of equal precedence.</summary>
    public const string EqualPrecedenceTemplate = "invoice-energy-equal";

    /// <summary>The template whose every version is refused by the take's own rules.</summary>
    public const string InvalidTemplate = "invoice-energy-invalid";

    /// <summary>The template whose every version declares an engine this deployment does not render.</summary>
    public const string UnsupportedTemplate = "invoice-energy-unsupported";

    /// <summary>The template that holds pre-releases only.</summary>
    public const string PreReleaseTemplate = "invoice-energy-prerelease";

    /// <summary>The template one version of which belongs to another consumer.</summary>
    public const string ForeignTemplate = "invoice-energy-foreign";

    /// <summary>The template whose folder holds no version folder.</summary>
    public const string EmptyTemplate = "invoice-energy-empty";

    /// <summary>The consumer the resolution runs for.</summary>
    public const string Owner = "billing-system";

    /// <summary>The consumer that owns the foreign version.</summary>
    public const string Other = "reporting-portal";

    /// <summary>A folder name that is not a version, beside the versions that are.</summary>
    public const string NotAVersion = "v1.3.0";

    /// <summary>A valid release of the family.</summary>
    public const string First = "1.0.0";

    /// <summary>The valid release a by-name job takes when <see cref="First"/> is taken.</summary>
    public const string Second = "1.1.0";

    /// <summary>The valid release that is higher by precedence than every other release of the family.</summary>
    public const string HighestRelease = "1.10.0";

    /// <summary>The version the take's own rules refuse: its manifest declares another version.</summary>
    public const string Invalid = "1.2.0";

    /// <summary>The version that declares an engine this deployment does not render.</summary>
    public const string Unsupported = "1.4.0";

    /// <summary>A valid pre-release, higher by precedence than every release of the family.</summary>
    public const string PreRelease = "2.0.0-beta.1";

    /// <summary>The valid version of the foreign family that every consumer may use.</summary>
    public const string Shared = "1.2.0";

    /// <summary>The two builds of one version the equal-precedence cases read.</summary>
    public static readonly IReadOnlyList<string> EqualPrecedence = ["1.1.0+build.1", "1.1.0+build.2"];

    /// <summary>Seeds the named versions of <see cref="Template"/>, each in the shape its name means.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="versions">The versions this case seeds.</param>
    public static void Seed(GitHubTemplateSourceStub stub, params string[] versions)
    {
        ArgumentNullException.ThrowIfNull(stub);
        foreach (var version in versions)
        {
            stub.SeedPackage(Template, version, Package(Template, version));
        }
    }

    /// <summary>Seeds the two builds of one version of <see cref="EqualPrecedenceTemplate"/>.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="versions">The versions this case seeds; both builds when none is named.</param>
    public static void SeedEqualPrecedence(GitHubTemplateSourceStub stub, params string[] versions)
    {
        ArgumentNullException.ThrowIfNull(stub);
        foreach (var version in versions.Length == 0 ? EqualPrecedence : versions)
        {
            stub.SeedPackage(EqualPrecedenceTemplate, version, Valid(EqualPrecedenceTemplate, version));
        }
    }

    /// <summary>Seeds pre-releases only, each folder holding a valid package.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="versions">The versions this case seeds; two pre-releases when none is named.</param>
    public static void SeedPreReleases(GitHubTemplateSourceStub stub, params string[] versions)
    {
        ArgumentNullException.ThrowIfNull(stub);
        foreach (var version in versions.Length == 0 ? new[] { "2.0.0-beta.1", "2.0.0-rc.1" } : versions)
        {
            stub.SeedPackage(PreReleaseTemplate, version, Valid(PreReleaseTemplate, version));
        }
    }

    /// <summary>Seeds every version of a template refused by the take's own rules.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="versions">The versions this case seeds.</param>
    public static void SeedInvalid(GitHubTemplateSourceStub stub, params string[] versions)
    {
        ArgumentNullException.ThrowIfNull(stub);
        foreach (var version in versions)
        {
            stub.SeedPackage(InvalidTemplate, version, InvalidPackage(InvalidTemplate, version));
        }
    }

    /// <summary>Seeds every version of a template that declares an engine this deployment does not render.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="versions">The versions this case seeds.</param>
    public static void SeedUnsupported(GitHubTemplateSourceStub stub, params string[] versions)
    {
        ArgumentNullException.ThrowIfNull(stub);
        foreach (var version in versions)
        {
            stub.SeedPackage(UnsupportedTemplate, version, UnsupportedPackage(UnsupportedTemplate, version));
        }
    }

    /// <summary>Seeds one version another consumer owns and does not share, beside one every consumer may use.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    /// <param name="foreignVersion">The version another consumer owns.</param>
    /// <param name="sharedVersion">The version every consumer may use.</param>
    public static void SeedForeign(GitHubTemplateSourceStub stub, string foreignVersion = Second, string sharedVersion = Shared)
    {
        ArgumentNullException.ThrowIfNull(stub);
        stub.SeedPackage(ForeignTemplate, foreignVersion, Valid(ForeignTemplate, foreignVersion, Other, shared: false));
        stub.SeedPackage(ForeignTemplate, sharedVersion, Valid(ForeignTemplate, sharedVersion, Other, shared: true));
    }

    /// <summary>Seeds a template folder that holds no version folder.</summary>
    /// <param name="stub">The template source the cases read from.</param>
    public static void SeedEmpty(GitHubTemplateSourceStub stub)
    {
        ArgumentNullException.ThrowIfNull(stub);
        stub.SeedTemplateFolder(EmptyTemplate);
    }

    /// <summary>A package the product's own rules accept.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version the folder and the manifest declare.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    /// <param name="shared">Whether every consumer may use it.</param>
    public static Dictionary<string, byte[]> Valid(
        string templateId,
        string version,
        string owner = Owner,
        bool shared = false) =>
        TemplatePackageFixtures.Valid(templateId, version, owner, shared, title: version);

    /// <summary>A package the take refuses: its manifest declares a version other than its folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The folder's name.</param>
    public static Dictionary<string, byte[]> InvalidPackage(string templateId, string version) =>
        TemplatePackageFixtures.FolderDoesNotMatchTheVersion(templateId, version, version + ".1");

    /// <summary>A package that declares an engine this deployment does not render.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static Dictionary<string, byte[]> UnsupportedPackage(string templateId, string version) =>
        TemplatePackageFixtures.UnsupportedEngine(templateId, version);

    /// <summary>One version of the family, in the shape the version's name means.</summary>
    private static Dictionary<string, byte[]> Package(string templateId, string version) => version switch
    {
        Invalid => InvalidPackage(templateId, version),
        Unsupported => UnsupportedPackage(templateId, version),
        NotAVersion => Valid(templateId, "1.3.0"),
        _ => Valid(templateId, version),
    };
}
