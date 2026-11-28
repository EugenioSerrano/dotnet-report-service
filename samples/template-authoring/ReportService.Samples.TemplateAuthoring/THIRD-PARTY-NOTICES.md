# Third-party notices

This package carries the components below, each under its own licence. The
package itself is under the MIT licence.

| Component | Version | Licence |
|-----------|---------|---------|
| Corvus.Text.Json | 5.6.1 | Apache-2.0 |
| Dapper | 2.1.89 | Apache-2.0 |
| Fluid.Core | 2.40.0 | MIT |
| Google.Protobuf | 3.26.1 | BSD-3-Clause |
| Handlebars.Net | 2.4.3 | MIT |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT |
| Microsoft.Playwright | 1.63.0 | MIT |
| Microsoft.Win32.SystemEvents | 6.0.0 | MIT |
| NexusRpc | 0.4.0 | MIT |
| NodaTime | 3.3.1 | Apache-2.0 |
| Npgsql | 10.0.3 | PostgreSQL |
| OpenTelemetry, OpenTelemetry.Api, OpenTelemetry.Api.ProviderBuilderExtensions, OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Instrumentation.AspNetCore, OpenTelemetry.Instrumentation.Http, OpenTelemetry.Instrumentation.Runtime | 1.20.1 | Apache-2.0 |
| Parlot | 1.5.2 | BSD-3-Clause |
| PDFsharp | 6.2.4 | MIT |
| PdfPig | 0.1.16 | Apache-2.0 |
| QRCoder | 1.8.0 | MIT |
| Scriban | 7.5.1 | BSD-2-Clause |
| System.Drawing.Common | 6.0.0 | MIT |
| Temporalio | 1.21.0 | MIT |
| TimeZoneConverter | 7.2.0 | MIT |
| YamlDotNet | 18.1.0 | MIT |

## The Playwright driver

`Microsoft.Playwright` carries a Node.js runtime for each supported platform and
the Playwright JavaScript package. Their licence texts are carried beside them:

- `tools/net11.0/any/.playwright/node/LICENSE` - the Node.js licence and the
  notices of the components Node.js bundles;
- `tools/net11.0/any/.playwright/package/LICENSE`, `NOTICE` and
  `ThirdPartyNotices.txt` - the Playwright package (Apache-2.0) and the
  components it bundles.

## Not carried

The Chromium browser the PDF is printed with is not part of this package: it is
installed by the user with Playwright's own script. No font a document prints
with is carried either; a template's fonts are the template author's. The icon font
the Playwright JavaScript package carries for its own trace-viewer and recorder
pages (`codicon`) is left out of this package; the tool never uses those pages.
