# Build stage — same SDK band as global.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props NuGet.config .editorconfig ./
COPY spikes/Directory.Build.props spikes/
COPY spikes/03-pdf-assembly/Directory.Packages.props spikes/03-pdf-assembly/
COPY spikes/03-pdf-assembly/PdfAssemblySpike/PdfAssemblySpike.csproj spikes/03-pdf-assembly/PdfAssemblySpike/
RUN dotnet restore spikes/03-pdf-assembly/PdfAssemblySpike/PdfAssemblySpike.csproj
COPY spikes/03-pdf-assembly/PdfAssemblySpike/ spikes/03-pdf-assembly/PdfAssemblySpike/
RUN dotnet publish spikes/03-pdf-assembly/PdfAssemblySpike/PdfAssemblySpike.csproj \
    -c Release --no-restore -o /app \
    -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

# qpdf stage — the upstream release archive, verified by its published SHA-256.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS qpdf
ARG QPDF_VERSION
ARG QPDF_SHA256
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && curl -fsSL -o /tmp/qpdf.zip \
       "https://github.com/qpdf/qpdf/releases/download/v${QPDF_VERSION}/qpdf-${QPDF_VERSION}-bin-linux-x86_64.zip" \
    && echo "${QPDF_SHA256}  /tmp/qpdf.zip" | sha256sum -c - \
    && mkdir /opt/qpdf && unzip -q /tmp/qpdf.zip -d /opt/qpdf

# Runtime stage — the same .NET runtime base as the product's images, plus the
# measuring and checking tools. This image is an experiment and is never published.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble AS final
ARG QPDF_VERSION
ARG QPDF_SHA256
ENV QPDF_VERSION=${QPDF_VERSION} QPDF_ARCHIVE_SHA256=${QPDF_SHA256}
RUN apt-get update && apt-get install -y --no-install-recommends \
       poppler-utils fonts-dejavu-core time \
    && rm -rf /var/lib/apt/lists/*
COPY --from=qpdf /opt/qpdf /opt/qpdf
RUN ln -s "$(find /opt/qpdf -type f -name qpdf -path '*/bin/*' | head -n 1)" /usr/local/bin/qpdf \
    && mkdir -p /work /out && chown "$APP_UID" /work /out
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "PdfAssemblySpike.dll"]
