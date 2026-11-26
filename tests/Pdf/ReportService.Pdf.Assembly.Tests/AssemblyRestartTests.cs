using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// Restart from committed parts: an assembly that is interrupted leaves no staging
/// file behind and publishes nothing, and the re-run over the same committed parts
/// produces exactly the content a clean run produces.
/// </summary>
public sealed class AssemblyRestartTests
{
    [Fact]
    public async Task An_interrupted_assembly_leaves_no_staging_file_and_a_rerun_equals_a_clean_run()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 2),
                AssemblyFixture.CreatePart(directory, 1, 2),
            };
            var clean = Path.Combine(directory, "clean.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, clean, batchSize: 1);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var interrupted = Path.Combine(directory, "interrupted.pdf");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new PdfSharpAssembler().AssembleAsync(
                    new AssemblyRequest(parts, AssemblyFixture.Contract(), interrupted, directory, BatchSize: 1),
                    cancelled.Token));

            Assert.False(File.Exists(interrupted));
            Assert.False(Directory.Exists(Path.Combine(directory, "staging")));

            var resumed = Path.Combine(directory, "resumed.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, resumed, batchSize: 1);

            Assert.Equal(AssemblyFixture.PageTexts(clean), AssemblyFixture.PageTexts(resumed));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
