# PDF base image: ASP.NET Core 10.0.13 on Ubuntu 24.04 with only the Chromium
# headless shell of the pinned browser automation package and its system
# libraries. The PDF worker image is published on top of it by the .NET SDK.
# Build context: the repository root. Requires BuildKit.

# Build stage: the small installer that runs the package's own browser installer.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS installer
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig ./
COPY eng/pdf-base/ReportService.Eng.PlaywrightInstaller/ eng/pdf-base/ReportService.Eng.PlaywrightInstaller/
RUN dotnet publish eng/pdf-base/ReportService.Eng.PlaywrightInstaller/ReportService.Eng.PlaywrightInstaller.csproj \
    -c Release -o /installer \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

# Runtime: the installer is mounted for one step only, so it never ships.
# Anything the installer creates besides the headless shell is removed in the
# same step; the browsers folder stays root-owned and read-only for others.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN --mount=type=bind,from=installer,source=/installer,target=/opt/browser-installer \
    dotnet /opt/browser-installer/ReportService.Eng.PlaywrightInstaller.dll install --with-deps --only-shell chromium \
    && find /ms-playwright -mindepth 1 -maxdepth 1 ! -name 'chromium_headless_shell-*' -exec rm -rf {} + \
    && chown -R root:root /ms-playwright \
    && chmod -R u=rwX,go=rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/* /tmp/* /root/.cache
USER app
