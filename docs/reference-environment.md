# Reference environment

Every published measurement of dotnet-report-service (throughput, memory,
temporary disk, rendering time) states the machine and the software it ran
on. This page describes that **reference environment** and the convention
every measurement follows when it is reported.

**Page revision:** 2 (2026-10-21; revision 1 the same day, before the PDF
base image and the local development registry existed). A change to anything below — hardware,
operating system, container runtime, toolchain or a pinned component —
creates a new revision of this page; a measurement always cites the revision
it ran on.

## Machine

| Item | Value |
|------|-------|
| CPU | Intel Core i9-14900 |
| Cores / threads | 8 / 16 (as reported by the operating system) |
| Memory | 24 GiB |

## Host operating system

| Item | Value |
|------|-------|
| Edition | Windows 11 Pro, 64-bit |
| Version / build | 10.0.26300 (build 26300) |

## Container runtime

| Item | Value |
|------|-------|
| Product | Rancher Desktop (Docker CLI 29.6.1) |
| Engine | Docker Engine 29.4.0-ce, Linux containers, `linux/amd64` |
| Virtual machine | WSL 2, openSUSE Leap 16.0, kernel 6.18.33.2 |
| CPUs given to the virtual machine | 16 |
| Memory given to the virtual machine | 11.7 GiB |
| Storage driver | overlayfs (cgroup v2) |
| Image builder (Dockerfile images) | BuildKit v0.29.0 through the buildx plugin v0.35.0 (default builder, `docker` driver) — builds the PDF base image |
| Product images | Built by the .NET SDK container publishing (`-t:PublishContainer`), no builder plugin — the render worker image |

## Toolchain

| Item | Value |
|------|-------|
| .NET SDK | 10.0.402 (selected by `global.json`) |
| .NET runtime | Microsoft.NETCore.App 10.0.13, Microsoft.AspNetCore.App 10.0.13 |
| Aspire | 13.6.2 (AppHost SDK and hosting packages, restored from NuGet) |
| Microsoft.Playwright | 1.63.0 |

## Pinned components as run

| Component | Version / tag | Digest |
|-----------|---------------|--------|
| PostgreSQL | `postgres:18.6` | `sha256:1267c7e692de235fda2a50dbcdbcbca4672ea6a0298576a6bd9d07cafc319b32` |
| Valkey | `valkey/valkey:9.1.2` | `sha256:418652cfb58ef879d4978c33553735d7147016032d5aefaa14c828e611eb9dfd` |
| Temporal development server | `temporalio/temporal:1.9.1` | `sha256:ad4c82c97bd12b417d1ea942610dbcd511afb250c4d5ed26c694009533df447e` |
| .NET SDK image (build stage of the PDF base) | `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` | `sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa` |
| ASP.NET Core image (base of the PDF base) | `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble` | `sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac` |
| Local development registry | `registry:3.1.2` — development only, bound to `127.0.0.1:5055` | `sha256:ddf754342cfc8acc51a56d5d0ab6af06826461864460636d8bd5c546dab2a7b8` |
| PDF base image | `report-service-pdf-base:1.63.0-10.0.13-noble`, built from `eng/pdf-base/Dockerfile` on this machine | `sha256:bdc804d2f5a4a08d7e1a1387d4a7434f15e9c5d7d22b9e968a990659f23d9c5f` — as logged by the composition; per machine, not a shared pin |
| Render worker image | `report-service-render-worker:0.1.0-dev`, built by the .NET SDK on the PDF base digest above | — (local image, rebuilt on every start of the composition) |
| Chromium (render worker) | 153.0.8010.12, headless shell (Playwright 1.63.0, browser revision 1243), installed in the PDF base image; version confirmed from the render worker's own start-up log: `Chromium 153.0.8010.12 ready (Playwright 1.63.0.0, .NET 10.0.13)` | — |
| .NET runtime in the render worker | 10.0.13, as the render worker logs it (framework-dependent, from the PDF base) | — |

The PDF base image and the render worker image are not published to any
registry: the base lives in the local development registry and its digest
is what the render worker image is built on. Two builds of the base on
different days may differ (operating-system packages come from the Ubuntu
archive of that day), so a measurement that depends on the render worker
cites the base digest it ran on.

## Image sizes and build times

First measurement on this machine (2026-10-21). Sizes as `docker image ls`
reports them ("content" = compressed layers, "disk usage" = unpacked image
plus its share of the store) and the unpacked root file system (`du -sx /`
inside the image).

| Image | Content (compressed) | Unpacked root file system | Disk usage |
|-------|----------------------|---------------------------|------------|
| `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble` (for comparison) | 95.9 MB | — | 340 MB |
| PDF base `report-service-pdf-base:1.63.0-10.0.13-noble` | 328 MB (7 layers, 327 791 257 bytes in the registry manifest) | 825 300 KiB (≈ 806 MiB) | 1.17 GB |
| Render worker `report-service-render-worker:0.1.0-dev` | 377 MB (8 layers) | 963 832 KiB (≈ 941 MiB) | 1.37 GB |

| Operation | Time |
|-----------|------|
| Clean build of the PDF base (BuildKit, no build cache; .NET images already pulled) | 145 s |
| Publish of the render worker image, clean (no `bin`/`obj`) | 16.5 s |
| Publish of the render worker image after a source file change (time stamp only) | 17.9 s |
| Start of the local composition to a healthy API, base already in the registry | 30.4 s |

## Reporting measurements

Every measurement states:

- the **date** it was taken;
- the **revision of this page** it ran on;
- the **dataset file** it used and its **SHA-256** — the pilot datasets and
  their hashes are listed in [`samples/datasets/README.md`](../samples/datasets/README.md);
- the same **four memory figures**:
  1. **managed heap peak** — the largest managed heap size of the .NET
     process during the run;
  2. **process peak RSS / working set** — the peak resident memory of the
     .NET process (peak working set on Windows, peak RSS on Linux);
  3. **Chromium process-tree peak RSS** — the peak resident memory of the
     browser and all its child processes, when a browser runs (otherwise
     "n/a");
  4. **temporary disk used** — the peak space taken by temporary files
     during the run.

A figure that could not be taken is written as "not measured", never left
out. This page never records host names, user names, IP addresses or serial
numbers.
