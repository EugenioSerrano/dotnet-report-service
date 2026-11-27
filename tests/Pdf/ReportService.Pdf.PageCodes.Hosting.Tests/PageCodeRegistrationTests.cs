using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Pdf.PageCodes.QrCoder;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.PageCodes.Hosting.Tests;

public sealed class PageCodeRegistrationTests
{
    private const string FixtureAssembly = "ReportService.Testing.PageCodeFixture.dll";
    private const string DependencyAssembly = "ReportService.Testing.PageCodeFixtureDependency.dll";
    private const string DependencyName = "ReportService.Testing.PageCodeFixtureDependency";
    private const string FixtureGenerator = "fixture-cells";

    private static readonly PageCodeArea Area = new(64, 64);

    [Fact]
    public void NothingIsRegisteredByDefault() =>
        Assert.Empty(PageCodeRegistration.Build(Configuration()).Names);

    [Fact]
    public void TheReferenceGridAndTheExampleAreRegisteredOnlyWhenEnabled()
    {
        Assert.Equal(["reference-grid"], PageCodeRegistration.Build(Configuration(("PageCodes:ReferenceGrid:Enabled", "true"))).Names);
        Assert.Equal(["qrcoder-qr"], PageCodeRegistration.Build(Configuration(("PageCodes:Examples:QrCoder:Enabled", "true"))).Names);
        Assert.Empty(PageCodeRegistration.Build(Configuration(("PageCodes:ReferenceGrid:Enabled", "false"))).Names);
        Assert.Empty(PageCodeRegistration.Build(Configuration(("PageCodes:ReferenceGrid:Enabled", "yes"))).Names);
    }

    [Fact]
    public void ADisabledNameIsRemovedLast()
    {
        var registry = PageCodeRegistration.Build(Configuration(
            ("PageCodes:ReferenceGrid:Enabled", "true"),
            ("PageCodes:Examples:QrCoder:Enabled", "true"),
            ("PageCodes:Disabled", "reference-grid")));

        Assert.Equal([QrCoderPageCodeGenerator.GeneratorName], registry.Names);
    }

    [Fact]
    public void TheRegistryIsTheServiceTheWorkersResolve()
    {
        var services = new ServiceCollection();
        services.AddPageCodeGenerators(Configuration(("PageCodes:ReferenceGrid:Enabled", "true")));

        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<PageCodeGeneratorRegistry>().TryResolve("reference-grid", out _));
    }

    [Fact]
    public void AConfiguredGeneratorLoadsInItsOwnContextAndCallsItsPrivateDependencyFromBesideIt()
    {
        var directory = CopyFixture();
        var path = Path.Combine(directory, FixtureAssembly);

        var registry = PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", path)));

        Assert.True(registry.TryResolve(FixtureGenerator, out var registered));
        var content = registered.Generator.Generate("ABC", new Dictionary<string, string>(), Area).Content!;
        Assert.Null(PageCodeContentValidator.Problem(content));

        // The generator implements the worker's own interface, from the default context.
        Assert.Same(typeof(IPageCodeGenerator).Assembly, registered.Generator.GetType().GetInterfaces().Single().Assembly);
        var generatorContext = AssemblyLoadContext.GetLoadContext(registered.Generator.GetType().Assembly)!;
        Assert.NotSame(AssemblyLoadContext.Default, generatorContext);

        // The dependency was loaded by that context, from the temporary copy, and the
        // default context holds no copy of it.
        var dependency = Assert.Single(generatorContext.Assemblies, assembly => assembly.GetName().Name == DependencyName);
        Assert.StartsWith(directory, dependency.Location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == DependencyName);

        Assert.Equal(PageCodeGeneratorOrigins.Configured, registered.Descriptor.Origin);
        Assert.Equal(path, registered.Descriptor.Path);
        Assert.Equal("ReportService.Testing.PageCodeFixture", registered.Descriptor.AssemblyName);
        Assert.False(string.IsNullOrWhiteSpace(registered.Descriptor.AssemblyVersion));
    }

    [Fact]
    public void AMissingPrivateDependencyFailsTheLoadNamingIt()
    {
        var directory = CopyFixture();
        File.Delete(Path.Combine(directory, DependencyAssembly));

        var failure = Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", Path.Combine(directory, FixtureAssembly)))));

        Assert.Contains(DependencyName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(FixtureAssembly, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutADepsFileTheDependencyIsResolvedFromTheAssemblysDirectory()
    {
        var directory = CopyFixture();
        File.Delete(Path.Combine(directory, "ReportService.Testing.PageCodeFixture.deps.json"));

        var registry = PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", Path.Combine(directory, FixtureAssembly))));

        Assert.True(registry.TryResolve(FixtureGenerator, out var registered));
        Assert.NotNull(registered.Generator.Generate("A", new Dictionary<string, string>(), Area).Content);
        var context = AssemblyLoadContext.GetLoadContext(registered.Generator.GetType().Assembly)!;
        var dependency = Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == DependencyName);
        Assert.StartsWith(directory, dependency.Location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutADepsFileAMissingDependencyStillFailsNamingIt()
    {
        var directory = CopyFixture();
        File.Delete(Path.Combine(directory, "ReportService.Testing.PageCodeFixture.deps.json"));
        File.Delete(Path.Combine(directory, DependencyAssembly));

        var failure = Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", Path.Combine(directory, FixtureAssembly)))));

        Assert.Contains(DependencyName, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPathAnUnloadableFileAndAnAssemblyWithoutAGeneratorFailNamingThePath()
    {
        var directory = CopyFixture();
        var missing = Path.Combine(directory, "absent.dll");
        var garbage = Path.Combine(directory, "garbage.dll");
        File.WriteAllText(garbage, "not an assembly");
        var dependency = Path.Combine(directory, DependencyAssembly);

        Assert.Contains(missing, Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", missing)))).Message, StringComparison.Ordinal);
        Assert.Contains(garbage, Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", garbage)))).Message, StringComparison.Ordinal);
        Assert.Contains("contributes no", Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies:0", dependency)))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameGeneratorTwiceFailsNamingIt()
    {
        var first = Path.Combine(CopyFixture(), FixtureAssembly);
        var second = Path.Combine(CopyFixture(), FixtureAssembly);

        var failure = Assert.Throws<PageCodeRegistrationException>(() =>
            PageCodeRegistration.Build(Configuration(("PageCodes:Assemblies", first + ";" + second))));

        Assert.Contains(FixtureGenerator, failure.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "page-code-fixture");
        var target = Path.Combine(Path.GetTempPath(), "page-code-hosting-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }
}
