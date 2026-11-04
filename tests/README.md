# tests

## Purpose

Automated tests of the product. The folder mirrors [`../src/`](../src/): every
product project gets its own unit/component test project, plus a few
cross-cutting suites organised by kind of test. Tests use xUnit v3 on
Microsoft.Testing.Platform (selected in [`../global.json`](../global.json));
each test project is a small executable that `dotnet test` runs.

## Test levels and their commands

Each level is run by **one command** from the repository root. The command is
the test runner in [`../eng/test/ReportService.Eng.TestRunner/`](../eng/test/ReportService.Eng.TestRunner/),
which knows the projects of each level, runs them with `dotnet test` in
Release, and exits non-zero naming the project (or the coverage floor) that
failed.

| Level | Projects | May reach | Command |
|-------|----------|-----------|---------|
| **Hermetic** — unit and component tests | `ReportService.Api.Tests`, `ReportService.Data.Tests`, `ReportService.DataContract.Tests`, `ReportService.Fixtures.Tests`, `ReportService.Jobs.Tests`, `ReportService.Migrations.Tests`, `ReportService.Rendering.Tests`, `ReportService.Storage.Tests`, `ReportService.TemplatePackages.Tests`, `ReportService.TemplateSource.Tests`, `ReportService.Templates.Tests`, `ReportService.Uploads.Tests`, `ReportService.Workers.Orchestration.Tests`, `ReportService.Workers.Pdf.Tests` (one `ReportService.<Area>.Tests` per product project, each the only project that measures its own assembly) | nothing outside the test process; files only under a temporary directory unique to each test | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic [--degree <n>] [--sequential --reason "<text>"] [--coverage-role <measure\|verification>]` |
| **Integration** — one real dependency at a time | `ReportService.IntegrationTests` | PostgreSQL, Valkey and the Temporal development server, each in its own container; the API and the orchestration worker hosted in the test process against them; a storage root in the test's temporary area | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- integration` |
| **End-to-end** — the whole local composition | `ReportService.EndToEndTests` | the composition of [`../src/ReportService.AppHost/`](../src/ReportService.AppHost/), started from the test | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- e2e` |

- The **hermetic** level needs only the .NET SDK — no container runtime. It
  is run with every change. Its mechanics — one build, the invocations without
  building, the bounded degree, the coverage role of a pair and the sequential
  fallback — are described under
  [*The hermetic level's execution*](#the-hermetic-levels-execution) below.
- The **integration** and **end-to-end** levels need a container runtime
  (Docker); the end-to-end level also needs what the composition needs (the
  local development registry and BuildKit, see the
  [README](../README.md)). Their tests are compiled and checked with every
  change, and run in a separate integration cycle against the real
  dependencies.
- The runner's `integration` command is the supported entry point for the
  integration level, because it switches off the Testcontainers resource
  reaper (the tests remove their own containers); running the project
  directly with `dotnet test` would start the reaper's extra container.
- The `integration` and `e2e` commands accept **`--without-printing`**, which
  leaves every test class carrying the trait `[Trait("Requires", "printing")]`
  out of the run. It exists for a check on a build that does not print a report
  yet; the flag is passed to the projects after `--` as
  `--filter-not-trait Requires=printing`. A covering run never passes it — a
  run without it executes the printing-dependent classes, and those fail by name
  when the printing steps are not part of the build.
- The end-to-end level prepares the render worker image once, before the tests
  start, so the classes never publish it at the same time.

### The hermetic level's execution

The command takes one level and, for the hermetic level only, three options:

| Option | Default | Meaning |
|--------|---------|---------|
| `--degree <n>` | `4` | How many project invocations may overlap: an integer from 2 to the number of projects in the level. |
| `--sequential --reason "<text>"` | absent | Runs one invocation at a time, in the level's order, and records the reason. The reason is required and is recorded verbatim; the option cannot be combined with `--degree`. |
| `--coverage-role <measure\|verification>` | `measure` | `measure` collects coverage and checks the floors; `verification` collects none. |

A run with no option is a `measure` run: it still collects coverage and still
checks the floors.

- **One build first.** Before any test starts, the level builds the level's
  projects and their project references in one Release build: an MSBuild
  traversal generated into the run directory from the level's own list
  (`hermetic-build.proj`), built with `Restore;Build` so a tree without restore
  output builds too. A compile error fails the run before a test starts, names
  the project the error belongs to, and exits **4**; the run directory then
  carries a `run.json` with no projects and the build's log.
- **Then the projects run without building.** Every invocation carries
  `--no-build`, its own `--results-directory`, its own `<project>.log` and — in
  a `measure` run — the coverage options and the include of the assembly that
  project owns. Each project appears once in the run, so one project is never
  invoked twice at the same time.
- **Bounded parallel.** The invocations overlap up to the recorded degree — 4 by
  default, lower on a smaller machine — and the degree never changes a result:
  nothing in the runner, in the tests or in the coverage options reads it. The
  command prints one start line and one finish line per project, so its console
  output interleaves; the per-project logs and `run.json` are the evidence.
- **One aggregation afterwards.** When the last invocation has ended, the floors
  are evaluated once from the measure run's reports and `coverage-summary.txt`
  is written — both in the level's fixed project order, never in completion
  order. `run.json` lists the projects in that same order.
- **The pair.** A run that measures coverage and floors is a `measure` run; the
  repeat that only checks the suite is `--coverage-role verification`, which
  passes no coverage option, writes no report and evaluates no floor. The two
  are compared on each project's counts and verdict, never on timings, run
  directory names or console interleaving.
- **The fallback.** `--sequential --reason "<text>"` runs the same work set one
  invocation at a time, with the same build and the same per-project evidence,
  and records that reason in `run.json`. It is for a machine or a project that
  cannot run in parallel. A run is never half-parallel: either it ran in
  parallel at a recorded degree or it ran sequentially with a reason, and
  misusing the options is a configuration error (exit code 3), never a silent
  change of mode.
- **Memory and throughput figures are never taken from a level run.** They come
  from a dedicated single-project run on the reference environment: the projects
  of a level run compete for the processor, so a figure measured beside them is
  not comparable with one measured alone.

### Run evidence

Every run writes its evidence under `artifacts/test-results/<yyMMdd-HHmm>/`
(git-ignored; a second run in the same minute gets `-2`, `-3`, …):

- `<project>.log` — the full console output of each test project;
- `hermetic-build.log` — the full output of the hermetic level's one build, and
  `hermetic-build.proj`, the list of projects it built; both are written by the
  run and are never part of the repository;
- `render-worker-image.log` — the console output of the end-to-end level's one
  image-preparation step, which runs before the tests start;
- `run.json` — the level, start and finish times, the product commit and
  whether the working tree was clean, the reference-environment revision, each
  project with its arguments and result (the end-to-end level lists its
  image-preparation step first), and — for the hermetic level — the coverage
  options and results and an `execution` block: the mode with its degree or its
  sequential reason, the coverage role of the run, and the build that preceded
  the invocations (command, log, exit code, duration, and the projects a failed
  build named), each project also carrying its own duration; for the integration
  and end-to-end levels, the
  identities (digests) of the images used and, for integration, the suite's
  containers before and after the run;
- hermetic level only: `<project>/coverage.cobertura.<timestamp>.xml` (one
  Cobertura report per project, in a `measure` run) and `coverage-summary.txt`.

## Coverage

- Measured on the **hermetic level only**, with `coverlet.MTP`, per product
  assembly.
- Floors: **80 % line / 70 % branch** for every product assembly that has a
  unit/component test project; **90 % / 85 %** for the integrity-critical
  modules. They apply from the change that creates the assembly's test
  project. A floor not met fails the hermetic command like a failing test,
  and the runner names the assembly and the figure.
- The floors live in one table in the runner
  (`eng/test/ReportService.Eng.TestRunner/CoverageFloors.cs`). **A new
  unit/component project adds its product assembly's row there in the same
  change**; an assembly with a row that is missing from the report fails the
  run. An assembly with no coverable line yet is reported `n/a`.
- Every product assembly with a unit/component project carries a floor at
  **80 % line / 70 % branch**: `ReportService.Api`, `ReportService.Jobs`,
  `ReportService.Migrations`, `ReportService.Rendering`, `ReportService.Storage`,
  `ReportService.Storage.Filesystem`, `ReportService.TemplatePackages`,
  `ReportService.TemplateSource`, `ReportService.Templates`,
  `ReportService.Uploads` and `ReportService.Workers.Orchestration`; the data
  contract's own assembly, `ReportService.Data`, carries **90 % line / 85 %
  branch**.
- An assembly without a floor is still reported: `ReportService.ServiceDefaults`
  and `ReportService.ServiceDefaults.Temporal` appear with their figures and no
  floor, because they have no unit/component project of their own yet.
- **A type that only talks to a real server may be excluded** with
  `[ExcludeFromCodeCoverage(Justification = "<one-line reason>")]`, and the
  change that adds it records the exclusion. The migrator's runner and journal
  are excluded this way: they are covered by the integration suite instead.
- Rates are computed from the report's line and branch counts and are never
  rounded up.
- **What is excluded, and how.** The runner passes the coverage filters on
  the command line: only `ReportService.*` assemblies are measured; the
  composition host (`ReportService.AppHost`), the engineering and sample
  tools, this folder's support library, composition roots (`Program.cs`) and
  generated files (`obj/`) are excluded; generated code marked with
  `GeneratedCode` or `CompilerGenerated` is excluded by the tool's defaults.
  A justified local exclusion is written on the code as
  `[ExcludeFromCodeCoverage(Justification = "<one-line reason>")]`.
- **Do not add a coverage section to a `testconfig.json`** unless it repeats
  every default exclusion of the tool and every filter of the runner: such a
  file replaces the tool's defaults instead of adding to them.

## Rules every test follows

- **Deterministic:** the same result on every run; no dependence on test
  order; cultures and time zones explicit when formatting or parsing; no wall
  clock in the code under test.
- **Hermetic tests reach nothing outside the process:** no network, database,
  queue or shared environment.
- **No automatic retries.** A test that passes only when re-run is a defect
  to fix, never something to re-run until green. Integration tests may poll a
  dependency until it is ready, within a deadline — never repeat an assertion.
- **Every integration test class owns its containers** and removes them
  through its own handles; nothing is pruned. Container names start with
  `report-service-it-`.
- **A test host carries its own meter identity and opens a unit of work on its
  own logger factory**, so two hosts may be open in one process and each capture
  holds only the telemetry of the unit of work its case opened: the spans of that
  unit's trace and the measurements recorded inside it, and nothing another case
  produced. Every read is a snapshot, so a case running beside another one that is
  exporting never sees a collection change under it. The `telemetry` collection
  still runs those classes one at a time, because the diagnostics endpoint reads and
  writes a process-wide environment entry a host's configuration sees at start-up;
  that serialisation is not what makes a capture correct. The class that starts two
  hosts at once to prove two captures stay apart runs with those cases for the same
  process-wide entry. The end-to-end composition class states, in one table, the
  state each worker reports for each of its queues — which is the work the service
  registers — and a change that registers work on a queue updates that table in the
  same change.
- Test names describe behaviour.

## What goes here

- **Unit and component tests**, one project per `src/` project.
- **Cross-cutting suites by kind:**
  - `ReportService.IntegrationTests` — runs against real dependencies
    (PostgreSQL, Valkey, Temporal; object storage when it exists).
  - `ReportService.EndToEndTests` — starts the whole local composition.
  - `ReportService.RenderingTests` (planned) — renders templates and compares
    the resulting documents (HTML, PDF, XLSX, Markdown).
  - `ReportService.PerformanceTests` — memory and throughput measurements over
    the generated pilot datasets; run on the reference environment.
- **`ReportService.Testing`** — a support library shared by the test
  projects (repository paths, the composition's pinned images, a bounded wait,
  in-memory log capture, the container helper, a directory made unwritable for
  a test). It is not a test project, and only test projects reference it.

## The storage provider-conformance suite

`ReportService.Storage.Tests` holds `ObjectStoreConformanceTests`, the
behaviour every object storage provider must show (see
[`../docs/object-storage.md`](../docs/object-storage.md)). It runs here
against the Filesystem provider, over a temporary directory per test. **A new
provider adds one test project that references `ReportService.Storage.Tests`
and derives `ObjectStoreConformanceTests`**, supplying its store, its declared
capabilities and a way to change stored bytes behind the store's back; a
provider whose emulator needs a container runs that project in the
integration level. A provider that fails any case is not shipped.

The suite's large-source case writes a generated 1 GiB source that cannot be
rewound and compares the process's peak working-set growth with a 64 MiB
write; it measures the whole process, so its class runs alone, after the
other tests. Its figures are attached to the run's results as
`memory-figure.txt`.
- Test fixtures and data owned by a single test project, inside that project.

## What does not go here

- Product code → [`../src/`](../src/). Nothing in `src/` references `tests/`.
- Datasets and templates meant for adopters → [`../samples/`](../samples/)
  (tests may read them, but do not own them).
- Exploratory measurements of Stage 0 → [`../spikes/`](../spikes/). Nothing
  in `src/` or `tests/` references `spikes/`.

## Naming

- Unit/component: `tests/ReportService.<Area>.Tests/ReportService.<Area>.Tests.csproj`,
  testing `src/ReportService.<Area>`.
- Cross-cutting: `tests/ReportService.<Kind>Tests/`.
- Shared support: `tests/ReportService.Testing/`.

Every test project is added to `dotnet-report-service.slnx` under the `tests`
solution folder, and to its level in the test runner.

## The upload verification suite

`ReportService.Workers.Orchestration.Tests` holds `UploadVerifierTests`, which
runs verification against the real Filesystem provider over a temporary
directory and the in-memory upload records: plain and compressed bodies, a
declared hash that matches and one that does not, every branch that refuses an
upload, a compressed body cut at every offset, every accepted header form, and
the cases that prove the content is read once and measured before anything is
committed. `Crc32Tests` holds the checksum against a bit-serial reference and
`GzipMemberAttestationTests` the trailer decision.

Three further classes of the same project:

- **`UploadVerificationMemoryTests`** verifies a generated body of one gibibyte,
  plain and compressed while it is written, and requires its working-set growth to
  stay within the growth of a sixty-four mebibyte body plus thirty-two mebibytes.
  Each case writes and removes its own storage root and reports its elapsed time
  and the most temporary storage it held.
- **`VerifyUploadActivityTests`** drives one attempt inside the workflow engine's
  own host for an activity: two attempts of one verification write under two
  different keys and one attempt always under the same one, a verification that
  takes time keeps telling the engine it is alive, and a cancelled attempt stops
  and commits nothing.
- **`UploadVerificationReplayTests`** replays every history committed under
  `Histories/` with the engine's replayer, so a change that would make a running
  verification non-deterministic fails without a server. The histories are
  fixtures: they are recorded once, against a real engine, by the opt-in capture
  case of the integration suite (`REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY` names the
  file to write), and committed with the change that recorded them. The case
  **fails** when no history exists — a case with nothing to check is not a result.

## The template suites

`ReportService.TemplateSource.Tests` holds the template source and the take: the
settings, the client against an in-process source, the version listing, the
by-name resolution and its take seam, the manifest reader, the take itself, and
the adapter that presents the take to the resolution. `ReportService.Workers.Pdf.Tests`
holds that worker's two activities as plain classes and the registration of the
take and of the by-name resolution on `report-pdf-rendering`.

The integration project carries them against real dependencies:
`TemplateVersionStoreTests` and `TemplateVersionReadTests` (the record and its
read on PostgreSQL), `TemplateTakeWorkerTests` (the take on its queue over a real
engine, database, storage root and template-source double), `TemplateByNameResolutionTests`
(the by-name resolution on the same queue, one workflow per case),
`GitHubTemplateSourceLiveTests` (the client against the public fixture
repository, including the listing of a real template folder) and
`JointTemplateTakeTests` (the job-level group, on a build that carries the job
step). The template-source double's fixture families are built by
`ReportService.Testing`: `TemplatePackageFixtures` for the take and
`ByNameFixtures` for the resolution, never checked in.

## The report job suite

The same project holds the tests of one report job's own work: `ReportJobActivitiesTests`
runs the check of the job's data reference and the failure that ends it against
the shared in-memory records, the case for every state an upload and a job can
be in; `ReportJobActivityOptionsTests` asserts every execution setting of those
steps; and `ReportJobReplayTests` with `ReportJobHistoryBoundTests` replay the
histories under `Histories/Jobs/` — one captured with a ready input, one with a
rejected input and one with a confirming input — and hold each of them to the
longest path this build writes, with no timer in it and exactly one check of the
input. The histories are recorded by the opt-in capture case of the integration
suite, which runs against the SDK's local engine and needs no container.

The integration project holds the suites that exercise a job against real
dependencies: the job store's shared contract on PostgreSQL
(`PostgresJobStoreContractTests`, with the schema's checks and two open
transactions competing for one upload), the accepted job that starts exactly once
across restarts (`JobStartTests`), the client that closes its connection before
reading its answer (`JobAcceptanceOverRealServerTests`), and — for a build that
prints — the job that runs to a terminal status (`JobNeverLostNeverDoubledTests`,
which carries `[Trait("Requires", "printing")]`).

