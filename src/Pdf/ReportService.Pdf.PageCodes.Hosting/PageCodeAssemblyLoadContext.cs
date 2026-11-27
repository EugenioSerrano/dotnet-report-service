using System.Reflection;
using System.Runtime.Loader;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.PageCodes.Hosting;

/// <summary>
/// The load context of one adopter assembly. Its own dependencies resolve from the
/// assembly's <c>.deps.json</c> and directory, beside it, without touching the
/// worker's own graph. The shared contract - the product assembly that defines the
/// generator interface - and every assembly the worker already loaded come from the
/// default context, so a generator's interface is the worker's interface.
/// </summary>
internal sealed class PageCodeAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly string ContractName =
        typeof(IPageCodeGenerator).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PageCodeAssemblyLoadContext(string path)
        : base("page-code:" + path, isCollectible: false) =>
        _resolver = new AssemblyDependencyResolver(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsShared(AssemblyName name) =>
        string.Equals(name.Name, ContractName, StringComparison.Ordinal)
        || Default.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, name.Name, StringComparison.Ordinal));
}
