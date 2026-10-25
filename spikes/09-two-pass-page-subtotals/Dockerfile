# Digest of the PDF base image in the local development registry, resolved at
# build time; there is no default, so a build without it fails. Requires BuildKit.
ARG PDF_BASE_DIGEST

# Build stage — same SDK band as global.json. The fixture library of
# spikes/07-native-tall-rows is referenced (read only), so it is in the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props NuGet.config .editorconfig ./
COPY spikes/Directory.Build.props spikes/
COPY spikes/07-native-tall-rows/PaginationFixtures/PaginationFixtures.csproj spikes/07-native-tall-rows/PaginationFixtures/
COPY spikes/09-two-pass-page-subtotals/Directory.Packages.props spikes/09-two-pass-page-subtotals/
COPY spikes/09-two-pass-page-subtotals/TwoPassSpike/TwoPassSpike.csproj spikes/09-two-pass-page-subtotals/TwoPassSpike/
RUN dotnet restore spikes/09-two-pass-page-subtotals/TwoPassSpike/TwoPassSpike.csproj
COPY spikes/07-native-tall-rows/PaginationFixtures/ spikes/07-native-tall-rows/PaginationFixtures/
COPY spikes/09-two-pass-page-subtotals/TwoPassSpike/ spikes/09-two-pass-page-subtotals/TwoPassSpike/
RUN dotnet publish spikes/09-two-pass-page-subtotals/TwoPassSpike/TwoPassSpike.csproj \
    -c Release --no-restore -o /app \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false
# The browser automation driver must be executable by the non-root runtime user;
# the published file is not, so set its mode here and fail if path or mode differ.
RUN chmod 0755 /app/.playwright/node/linux-x64/node && test "$(stat -c %a /app/.playwright/node/linux-x64/node)" = "755"

# Runtime stage — the PDF base image itself, so the Chromium, fonts and system
# libraries measured here are the ones the PDF worker runs. Nothing is installed.
FROM localhost:5055/report-service-pdf-base:1.63.0-10.0.13-noble@${PDF_BASE_DIGEST} AS final
ARG PDF_BASE_DIGEST
# Recorded in the results so every measurement names the base it ran on.
ENV PDF_BASE_DIGEST=${PDF_BASE_DIGEST}
WORKDIR /app
COPY --from=build /app ./
USER root
# Output folder owned by the non-root user (numeric uid:gid of the base's user).
RUN mkdir /out && chown 1654:1654 /out && chmod 0755 /out
USER app
ENTRYPOINT ["dotnet", "TwoPassSpike.dll"]
