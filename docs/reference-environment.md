# Reference environment

Every published measurement of dotnet-report-service (throughput, memory,
temporary disk, rendering time) states the machine and the software it ran
on. This page describes that **reference environment** and the convention
every measurement follows when it is reported.

**Page revision:** 6 (2026-11-23; revision 5 on 2026-11-17, revision 4 on 2026-11-15, revision 3 on 2026-11-01, revision 2 on 2026-10-21, revision 1 the same day before the PDF base image and the local development registry existed). Revision 6 records the platform move to **.NET 11 + Aspire 17**: the toolchain .NET SDK moves to 11.0.100 and the runtime to 11.0.0 (Aspire unchanged at 17.0.0, migrated in the prior step), and the container base images move from `*-noble` (Ubuntu 24.04) to `*-resolute` (**Ubuntu 26.04**) — `mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute` and `sdk:11.0.100-resolute` — so the PDF base image is rebuilt on the .NET 11 ASP.NET Core base, and its tag (`1.63.0-11.0.0-resolute`), local-registry digest, image size and build time are re-measured on that base. The machine, host operating system and container runtime are **unchanged from revision 5** (Docker Desktop 4.99.0 / Engine 29.9.3). The values that only the covering integration/e2e/rendering cycle can confirm — the .NET runtime the render worker reports from its own start-up log, the render worker image rebuilt by the composition, and the composition-green confirmation — are that cycle's evidence and are marked *pending the covering cycle* below. **Revert-on-failure rule:** this revision-6 re-add records the values this V-Bounce measured; if the covering cycle fails, it is reverted in the follow-up fix and re-added when the cycle is green. Revision 5 states the machine as it reports itself now: the same CPU model as revision 4 — Intel Core i9-14900 — with a different core count and memory, the operating system reporting 11 cores / 22 threads and 32 GiB on the host where revision 4 recorded 8 / 16 and 24 GiB, and the container engine giving its virtual machine 22 CPUs and 15.6 GiB where revision 4 recorded 16 and 11.7 GiB; Docker Desktop 4.99.0 (Docker CLI 29.9.3) with Engine 29.9.3 where revision 4 recorded Docker Desktop 4.97.0 (Docker CLI 29.9.2) with Engine 29.9.2; a .NET SDK that resolves to 10.0.404 under `global.json`'s 10.0.402 where revision 4 recorded 10.0.402, and runtimes at 10.0.15 where revision 4 recorded 10.0.13; a PostgreSQL digest of sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42 where revision 4 recorded sha256:1267c7e692de235fda2a50dbcdbcbca4672ea6a0298576a6bd9d07cafc319b32, a PDF base image digest of sha256:da5f467003ee90edff38265659f0afa3884f965bf8c74def7773c858d3370437 where revision 4 recorded sha256:bdc804d2f5a4a08d7e1a1387d4a7434f15e9c5d7d22b9e968a990659f23d9c5f, and a render worker image tag of `report-service-render-worker:0.2.0-dev` where revision 4 recorded `0.1.0-dev`; the .NET SDK and ASP.NET Core images are not in this machine's image store, so their digests read "not measured". The machine differs — the same CPU model with a different core count and memory, and a later container runtime — and this page records that difference, not a cause for it. The host operating system, the CPU model, the Valkey and Temporal digests, Aspire and Microsoft.Playwright are unchanged. Revision 4 changes only the Aspire line of the toolchain, from 13.6.2 to 13.6.5. Revision 3 changes only the container runtime: Rancher Desktop was replaced by Docker Desktop 4.97.0 on 2026-11-01. A change to anything below — hardware, operating system, container runtime, toolchain or a pinned component — creates a new revision of this page; a measurement always cites the revision it ran on.

## Machine

| Item | Value |
|------|-------|
| CPU | Intel Core i9-14900 |
| Cores / threads | 11 / 22 (as reported by the operating system) |
| Memory | 32 GiB (34 358 579 200 bytes, as reported by the operating system) |

## Host operating system

| Item | Value |
|------|-------|
| Edition | Windows 11 Pro, 64-bit |
| Version / build | 10.0.26300 (build 26300) |

## Container runtime

| Item | Value |
|------|-------|
| Product | Docker Desktop 4.99.0 (Docker CLI 29.9.3) |
| Engine | Docker Engine 29.9.3, Linux containers, `linux/amd64` |
| Virtual machine | WSL 2, Docker Desktop's `docker-desktop` distribution, kernel 6.18.33.2-microsoft-standard-WSL2 |
| CPUs given to the virtual machine | 22 |
| Memory given to the virtual machine | 15.6 GiB (16 764 805 120 bytes, as the engine reports it) |
| Storage driver | overlayfs (cgroup v2) |
| VM disk location | the system drive (`C:`); the previous runtime kept a 31.9 GB disk on `D:` |
| Image builder (Dockerfile images) | BuildKit through the buildx plugin v0.38.1 (default builder, `docker` driver) — builds the PDF base image |
| Product images | Built by the .NET SDK container publishing (`-t:PublishContainer`), no builder plugin — the render worker image |

## Toolchain

| Item | Value |
|------|-------|
| .NET SDK | 11.0.100 (resolved by `global.json`'s 11.0.100 with `rollForward: latestPatch`) |
| .NET runtime | Microsoft.NETCore.App 11.0.0, Microsoft.AspNetCore.App 11.0.0 (the highest 11.0 patch listed) |
| Aspire | 17.0.0 (AppHost SDK and hosting packages, restored from NuGet) |
| Microsoft.Playwright | 1.63.0 |

## Pinned components as run

| Component | Version / tag | Digest |
|-----------|---------------|--------|
| PostgreSQL | `postgres:18.6` | `sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42` |
| Valkey | `valkey/valkey:9.1.2` | `sha256:418652cfb58ef879d4978c33553735d7147016032d5aefaa14c828e611eb9dfd` |
| Temporal development server | `temporalio/temporal:1.9.1` | `sha256:ad4c82c97bd12b417d1ea942610dbcd511afb250c4d5ed26c694009533df447e` |
| .NET SDK image (build stage of the PDF base) | `mcr.microsoft.com/dotnet/sdk:11.0.100-resolute` (Ubuntu 26.04) | `sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea` — the registry multi-arch index digest (verified on MCR); the image is pulled by BuildKit and is not in this machine's named image store |
| ASP.NET Core image (base of the PDF base) | `mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute` (Ubuntu 26.04) | `sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242` — the registry multi-arch index digest (verified on MCR); the base the PDF base build resolved |
| Local development registry | `registry:3.1.2` — development only, bound to `127.0.0.1:5055` | `sha256:ddf754342cfc8acc51a56d5d0ab6af06826461864460636d8bd5c546dab2a7b8` |
| PDF base image | `report-service-pdf-base:1.63.0-11.0.0-resolute`, built from `eng/pdf-base/Dockerfile` on this machine and pushed to the local registry | `sha256:00fd46a4c16dccd69e76c750244500aae38d213abf057875d8a6a6ee0fa495ec` — the local-registry manifest-list digest this V-Bounce built and pushed; per machine, not a shared pin |
| Render worker image | `report-service-render-worker`, built by the .NET SDK on the PDF base digest above | *pending the covering cycle* — not rebuilt in this revision; the composition rebuilds it on start |
| Chromium (render worker) | 153.0.8010.12, headless shell (Playwright 1.63.0, browser revision 1243), installed in the PDF base image; the worker's start-up log line is *pending the covering cycle* (expected `Chromium 153.0.8010.12 ready (Playwright 1.63.0.0, .NET 11.0.0)`) | — |
| .NET runtime in the render worker | *pending the covering cycle* — expected 11.0.x, as the render worker logs it (framework-dependent, from the PDF base) | — |

The pinned-component digests above were recorded under revision 2 and were re-read on the first run under revision 3; the images themselves carried over unchanged from the previous runtime on 2026-11-01. They were read again for revision 5: the PostgreSQL digest and the locally built PDF base digest have moved since revision 4 recorded them, the Valkey and Temporal digests are unchanged, and the two .NET base images are not in this machine's image store, so their digests read "not measured". For **revision 6** the PDF base image was rebuilt on the .NET 11 ASP.NET Core base and its new local-registry digest recorded; the `sdk`/`aspnet` base images now carry their MCR registry multi-arch index digests (verified on the registry); PostgreSQL, Valkey, Temporal and the local registry are unchanged from revision 5. The render-worker image and the worker-reported runtime are *pending the covering cycle* (revert-on-failure, see the revision note).

The PDF base image and the render worker image are not published to any
registry: the base lives in the local development registry and its digest
is what the render worker image is built on. Two builds of the base on
different days may differ (operating-system packages come from the Ubuntu
archive of that day), so a measurement that depends on the render worker
cites the base digest it ran on.

## Image sizes and build times

Revision 6 re-measured the PDF base image on the .NET 11 / Ubuntu 26.04 base (2026-11-23): its disk usage and unpacked root file system are recorded below, measured with `docker image ls` and `du -sx /` inside the image. The render-worker figures and a clean comparable build-time are *pending the covering cycle* (revert-on-failure). Sizes as `docker image ls` reports them ("content" = compressed layers, "disk usage" = unpacked image plus its share of the store) and the unpacked root file system (`du -sx /` inside the image).

| Image | Content (compressed) | Unpacked root file system | Disk usage |
|-------|----------------------|---------------------------|------------|
| `mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute` (for comparison) | not separately measured this revision | — | not separately measured this revision |
| PDF base `report-service-pdf-base:1.63.0-11.0.0-resolute` | not separately measured this revision | 864 800 KiB (≈ 844 MiB) | 1.24 GB |
| Render worker `report-service-render-worker` | *pending the covering cycle* | *pending the covering cycle* | *pending the covering cycle* |

| Operation | Time |
|-----------|------|
| Clean build of the PDF base (BuildKit, no build cache; .NET images already pulled) | re-measured in the covering cycle — the revision-6 V-Bounce built the base but did not take an isolated clean-build timing |
| Publish of the render worker image, clean (no `bin`/`obj`) | *pending the covering cycle* |
| Publish of the render worker image after a source file change (time stamp only) | *pending the covering cycle* |
| Start of the local composition to a healthy API, base already in the registry | *pending the covering cycle* |

## Document capacity measurements

The assembly figures of one PDF document at 10 000 and 50 000 pages, and how a
deployment measures its own, are published in
[document capacity](document-capacity.md). They are taken on this environment and are
a lower bound for documents printed from real templates.

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
