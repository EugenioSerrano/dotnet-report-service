# The authoring tool's golden PDFs

One golden per example package, printed once by the live local render during
US-000.BOLT-114's V-Bounce and verified by the product's own post-render
verification before it was copied here. The hermetic cases replay them in-process:
a replaying printer copies the golden to the path the product renderer asks for,
so `PdfSampleRenderer` verifies the exact printed page a live print produced.

| Golden | Example | Engine | Bytes | SHA-256 |
|--------|---------|--------|-------|---------|
| `pilot-simple.pdf` | `samples/templates/pilot-simple` | Scriban 7.5.1 | 55 651 | `fbd6731d…0313031b` |
| `pilot-handlebars.pdf` | `samples/templates/pilot-handlebars` | Handlebars 2.4.3 | 55 651 | `beccc597…ec6e6252` |
| `pilot-liquid.pdf` | `samples/templates/pilot-liquid` | Liquid/Fluid 2.40.0 | 55 651 | `3bafda49…86c18035` |

The exact hashes are in `SHA256SUMS` beside this file.

## How they were produced

Product commit `67599df63f4f5e5177f438fcfeb2544b8c288740` (branch `develop`), on
Windows, with Playwright 1.63.0's Chromium (revision 1243, Chromium
153.0.8010.12) — the same build the PDF base image installs.

From the repository root, once per example:

```text
dotnet run --project samples/template-authoring/ReportService.Samples.TemplateAuthoring -- render samples/templates/pilot-simple --out artifacts/template-preview/pilot-simple/1.0.0
dotnet run --project samples/template-authoring/ReportService.Samples.TemplateAuthoring -- render samples/templates/pilot-handlebars --out artifacts/template-preview/pilot-handlebars/1.0.0
dotnet run --project samples/template-authoring/ReportService.Samples.TemplateAuthoring -- render samples/templates/pilot-liquid --out artifacts/template-preview/pilot-liquid/1.0.0
```

Each command printed `rendered <id> 1.0.0: engine=<engine> pages=1 words=183` and
exited 0, which the tool does only after `PdfSampleRenderer` accepted the printed
page with the product's verification. The composed HTML of the three runs is
byte-identical (`SHA-256 017e0c2398e91c307aad9938515d959104bb7539578e609987770cb2b6fff7d7`):
the three examples are the same report in a different engine. The PDFs differ in
their container bytes (Chromium's own metadata) and each was verified on its own.

## When they must be regenerated

Regenerate — by the same live commands, then copy the PDFs here and rewrite
`SHA256SUMS` — when an example package changes (its parts, stylesheet, schema or
sample), when an engine's version changes, or when the product's composition or
verification changes in a way that alters what is printed. A change to any of
those makes the hermetic verification fail loudly against the recomposed block;
the goldens are never edited by hand and never regenerated outside a live,
verified render.

The three goldens are binaries no case writes: the hermetic suite copies them to
a temporary path and reads them in-process, so the level needs only the .NET SDK.
