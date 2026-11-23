# The test oracle image: the independent reader of a printed document, with the
# text tools it invokes as separate processes. It is built locally, never pushed
# beyond the loopback registry, holds no browser and no product library, and runs
# as a user that owns nothing (ADR-013 C1-C5).
# Build context: the repository root, BuildKit not required.
#   docker build -f eng/pdf-oracle/Dockerfile -t report-service-pdf-oracle:<tag> .

# Build stage: the oracle's own console, published from the repository.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig ./
COPY tests/Rendering/ReportService.PdfOracle/ tests/Rendering/ReportService.PdfOracle/
RUN dotnet publish tests/Rendering/ReportService.PdfOracle/ReportService.PdfOracle.csproj \
    -c Release -o /oracle \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

# Runtime: the pinned base plus the two pinned tools and their closure. The
# install's package set is held to the owner's accepted list, and the versions,
# the copyright hashes and the licence names are recorded inside the image.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242

COPY eng/pdf-oracle/accepted-packages.txt /tmp/c3/accepted-packages.txt
COPY eng/pdf-oracle/check-installed.sh /tmp/c3/check-installed.sh

RUN dpkg-query -W -f='${Package} ${Version}\n' >/tmp/c3/before \
    && apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends \
        poppler-utils=26.01.0-2ubuntu0.3 \
        qpdf=12.3.2-1 \
    && bash /tmp/c3/check-installed.sh /tmp/c3/before /tmp/c3/accepted-packages.txt /oracle-evidence \
    && rm -rf /var/lib/apt/lists/* /tmp/* /root/.cache

COPY --from=build /oracle /oracle

# The image's own identity, so a run names the exact set it read.
RUN dpkg-query -W -f='${Package} ${Version}\n' \
      poppler-utils qpdf | sed 's/^/tool: /' >/oracle-evidence/tools.txt \
    && printf 'waived - ADR-013\n' >/oracle-evidence/licence-gate.txt

USER app
WORKDIR /oracle
ENTRYPOINT ["dotnet", "/oracle/ReportService.PdfOracle.dll"]
