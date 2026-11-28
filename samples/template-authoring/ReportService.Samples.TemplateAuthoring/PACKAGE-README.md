# report-template

Scaffolds a report template package for one engine (Scriban, Handlebars or Liquid)
and renders the package's own sample locally to a preview HTML and a verified PDF,
with the report service's own checks and renderer.

```text
dotnet tool install --global ReportService.TemplateAuthoring --version <version> --add-source <package-folder>
report-template scaffold --engine <scriban|handlebars|liquid> --id <template-id> [--version <semver>] [--out <dir>]
report-template render <package-folder> [--out <dir>] [--page-capacity <pages>]
```

Requires the .NET 11 SDK. Printing the PDF also needs the Chromium build of
Playwright 1.63.0, which this package does not carry: install it once with
`pwsh <tool folder>/playwright.ps1 install chromium`. Without it, `render` prints
`render-unavailable` and exits 3.

The full guide is `docs/template-authoring.md` in the product repository. The
third-party components this package carries are listed in `THIRD-PARTY-NOTICES.md`.
