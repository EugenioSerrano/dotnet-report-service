# The test oracle image: the independent reader of a printed document, with the
# text tools it invokes as separate processes. It is built locally, never pushed
# beyond the loopback registry, holds no browser and no product library, and runs
# as a user that owns nothing (ADR-013 C1-C5).
# Build context: the repository root, BuildKit not required.
#   docker build -f eng/pdf-oracle/Dockerfile -t report-service-pdf-oracle:<tag> .

# Build stage: the oracle's own console, published from the repository.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig ./
COPY tests/ReportService.PdfOracle/ tests/ReportService.PdfOracle/
RUN dotnet publish tests/ReportService.PdfOracle/ReportService.PdfOracle.csproj \
    -c Release -o /oracle \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

# Runtime: the pinned base plus the two pinned tools and their closure. The
# install's package set is held to the owner's accepted list, and the versions,
# the copyright hashes and the licence names are recorded inside the image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac

COPY eng/pdf-oracle/accepted-packages.txt /tmp/c3/accepted-packages.txt
COPY eng/pdf-oracle/check-installed.sh /tmp/c3/check-installed.sh

RUN dpkg-query -W -f='${Package} ${Version}\n' >/tmp/c3/before \
    && apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends \
        poppler-utils=24.02.0-1ubuntu9.11 \
        qpdf=11.9.0-1.1ubuntu0.1 \
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
