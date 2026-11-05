using ReportService.Testing;

namespace ReportService.RenderingTests;

/// <summary>
/// The shipped sample a consumer copies, printed as it ships: the package is its own
/// folder under <c>samples/templates</c> and the payload is its own snapshot, both
/// mounted read-only, so nothing the print needs is substituted on the way. The case
/// reads the product's own verdict, not the judge, because the claim is that the
/// product accepts the report a consumer would get.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class ShippedSampleTests(RenderingSuite suite)
{
    private const string SimpleSample = "pilot-simple";

    /// <summary>The simple sample prints from its own folder and the verification accepts it.</summary>
    [Fact]
    public async Task TheShippedSimpleSamplePrintsAndVerifiesAsItShips()
    {
        var render = await suite.RenderShippedSampleAsync(SimpleSample);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The print writes into the suite's temporary root, which DisposeAsync deletes:
        // the result and the document are copied into the run's own evidence folder first,
        // so the verdict and the aborted request count outlive this case.
        RunEnvironment.WriteEvidence(
            "pilot-simple-print.json",
            await File.ReadAllBytesAsync(Path.Combine(suite.OutputDirectory, "sample.json"), cancellationToken));
        RunEnvironment.WriteEvidence("pilot-simple-print.pdf", await File.ReadAllBytesAsync(render.Pdf, cancellationToken));

        Assert.True(
            render.VerificationPassed,
            $"the shipped {SimpleSample} sample was refused by the product's verification: "
            + $"{render.VerificationReason} ({render.AbortedRequests} aborted request(s)).");
    }
}
