# Digest of the PDF base image in the local development registry, resolved at
# build time; there is no default, so a build without it fails. Requires BuildKit.
ARG PDF_BASE_DIGEST

# Group-rules harness image. Build stage — same SDK band as global.json. The
# harness links files of spikes/02-pdf-layout, spikes/08-pagedjs-pre-split and
# spikes/10-pagedjs-table-strict-accounting and references the fixture library of
# spikes/07-native-tall-rows; all are copied read only, unchanged. The build stage
# also verifies the formatting and audits the packages; any drift fails the build.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props NuGet.config .editorconfig ./
COPY spikes/Directory.Build.props spikes/
COPY spikes/14-group-rules-feasibility/Directory.Packages.props spikes/14-group-rules-feasibility/
COPY spikes/07-native-tall-rows/Directory.Packages.props spikes/07-native-tall-rows/
COPY spikes/07-native-tall-rows/PaginationFixtures/PaginationFixtures.csproj spikes/07-native-tall-rows/PaginationFixtures/
COPY spikes/14-group-rules-feasibility/GroupRulesSpike/GroupRulesSpike.csproj spikes/14-group-rules-feasibility/GroupRulesSpike/
RUN dotnet restore spikes/14-group-rules-feasibility/GroupRulesSpike/GroupRulesSpike.csproj
COPY spikes/02-pdf-layout/PdfLayoutSpike/AccountingVerifier.cs spikes/02-pdf-layout/PdfLayoutSpike/BlockHtmlBuilder.cs spikes/02-pdf-layout/PdfLayoutSpike/BlockSource.cs spikes/02-pdf-layout/PdfLayoutSpike/LayoutRunner.cs spikes/02-pdf-layout/PdfLayoutSpike/ProcessTreeSampler.cs spikes/02-pdf-layout/PdfLayoutSpike/
COPY spikes/02-pdf-layout/PdfLayoutSpike/assets/ spikes/02-pdf-layout/PdfLayoutSpike/assets/
COPY spikes/07-native-tall-rows/PaginationFixtures/ spikes/07-native-tall-rows/PaginationFixtures/
COPY spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/PageBuilder.cs spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/RunRecord.cs spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/
COPY spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/assets/pre-split.js spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/assets/fixture.css spikes/08-pagedjs-pre-split/PreSplitLayoutSpike/assets/
COPY spikes/10-pagedjs-table-strict-accounting/PagedMarkupSpike/MatrixDriver.cs spikes/10-pagedjs-table-strict-accounting/PagedMarkupSpike/
COPY spikes/14-group-rules-feasibility/GroupRulesSpike/ spikes/14-group-rules-feasibility/GroupRulesSpike/
RUN dotnet format spikes/14-group-rules-feasibility/GroupRulesSpike/GroupRulesSpike.csproj --verify-no-changes --no-restore \
    && dotnet list spikes/14-group-rules-feasibility/GroupRulesSpike/GroupRulesSpike.csproj package --vulnerable --include-transitive
RUN dotnet publish spikes/14-group-rules-feasibility/GroupRulesSpike/GroupRulesSpike.csproj \
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
# The assembly keeps the name the linked driver starts for each run.
ENTRYPOINT ["dotnet", "PagedMarkupSpike.dll"]
