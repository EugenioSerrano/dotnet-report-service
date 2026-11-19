namespace ReportService.Workers.Pdf.Tests.Rendering;

using ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The globalisation data the process records with every printed block: read from the
/// artefacts the runtime itself loads, over the short list of paths the base image and the
/// development host carry, with a probe that finds nothing answering nothing - the caller
/// refuses rather than recording a version nobody can name.
/// </summary>
public sealed class GlobalisationProvenanceTests
{
    [Fact]
    public void TheIcuMajorIsTheOneTheLibraryFilenameNames()
    {
        var root = Folder();
        try
        {
            var library = Path.Combine(root, "x86_64-linux-gnu");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "libicuuc.so.74.2"), string.Empty);
            File.WriteAllText(Path.Combine(library, "libicui18n.so.74.2"), string.Empty);

            Assert.Equal("74", GlobalisationProvenance.ReadIcu(root, Folder()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnImageWithoutTheLibrariesNamesNothing()
    {
        var root = Folder();
        var windows = Folder();
        try
        {
            Assert.Null(GlobalisationProvenance.ReadIcu(root, windows));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(windows, recursive: true);
        }
    }

    [Fact]
    public void TheZoneDataVersionIsTheFirstLineOfTheDatabaseFile()
    {
        var root = Folder();
        try
        {
            File.WriteAllText(Path.Combine(root, "tzdata.zi"), "# version 2025b\n# This file is in the public domain.\n");

            Assert.Equal("2025b", GlobalisationProvenance.ReadTimeZoneData(root, Folder()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheZoneDataVersionIsReadFromTheVersionFileWhenTheDatabaseIsAbsent()
    {
        var root = Folder();
        var windows = Folder();
        try
        {
            File.WriteAllText(Path.Combine(root, "+VERSION"), "2025b\n");

            Assert.Equal("2025b", GlobalisationProvenance.ReadTimeZoneData(root, windows));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(windows, recursive: true);
        }
    }

    [Fact]
    public void AHostWithoutTheZoneDataNamesNothing()
    {
        var root = Folder();
        var windows = Folder();
        try
        {
            Assert.Null(GlobalisationProvenance.ReadTimeZoneData(root, windows));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(windows, recursive: true);
        }
    }

    [Fact]
    public void AProcessThatRunsWithoutGlobalisationIsRefusedRatherThanRecorded()
    {
        // The shipped images never set this switch. A process that does has no ICU data to
        // name, and D10/P5 refuse a value that names no artefact instead of recording one.
        const string invariant = "System.Globalization.Invariant";
        Assert.False(
            AppContext.TryGetSwitch(invariant, out var already) && already,
            "this process already runs with globalisation disabled, so the case cannot separate the two modes");

        AppContext.SetSwitch(invariant, true);
        var root = Folder();
        var windows = Folder();
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(() => GlobalisationProvenance.ReadIcu(root, windows));

            Assert.Contains(invariant, refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            AppContext.SetSwitch(invariant, false);
            Directory.Delete(root, recursive: true);
            Directory.Delete(windows, recursive: true);
        }
    }

    [Fact]
    public void TheProcessRecordsTheVersionsOfTheArtefactsItRunsOn()
    {
        var provenance = GlobalisationProvenance.OfThisProcess();

        Assert.False(string.IsNullOrWhiteSpace(provenance.IcuVersion));
        Assert.False(string.IsNullOrWhiteSpace(provenance.TzDataVersion));
        Assert.NotEqual("unknown", provenance.IcuVersion);
        Assert.NotEqual("unknown", provenance.TzDataVersion);

        // Read once and kept: the process's lifetime sees the same value.
        Assert.Equal(provenance, GlobalisationProvenance.Current);
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "globalisation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
