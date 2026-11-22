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
| **Hermetic** — unit and component tests | `ReportService.Api.Tests`, `ReportService.Data.Tests`, `ReportService.DataContract.Tests`, `ReportService.Fixtures.Tests`, `ReportService.Jobs.Tests`, `ReportService.Migrations.Tests`, `ReportService.Pdf.Verification.Tests`, `ReportService.PdfOracle.Tests`, `ReportService.Rendering.Tests`, `ReportService.Storage.Tests`, `ReportService.TemplatePackages.Tests`, `ReportService.TemplateSource.Tests`, `ReportService.TemplateChecks.Tests`, `ReportService.Templates.Tests`, `ReportService.Uploads.Tests`, `ReportService.Workers.Orchestration.Tests`, `ReportService.Workers.Pdf.Tests` (one `ReportService.<Area>.Tests` per product project, each the only project that measures its own assembly), and `ReportService.PdfStructure.Tests` (the hermetic PDF structure verification over the committed golden PDFs) | nothing outside the test process; files only under a temporary directory unique to each test; the committed golden PDFs read in-process | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic [--degree <n>] [--sequential --reason "<text>"] [--coverage-role <measure\|verification>]` |
| **Integration** — one real dependency at a time | `ReportService.IntegrationTests` | PostgreSQL, Valkey and the Temporal development server, each in its own container; the API and the orchestration worker hosted in the test process against them; a storage root in the test's temporary area; the render worker image the startup class publishes from the render worker's entry project on the PDF base, run as a container | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- integration` |
| **Rendering** — every printed document judged | `ReportService.RenderingTests` | the rendering host image and the judge image, built once per run on the PDF base and started as one-shot containers; the core tier of the fixture library and the known-bad controls | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- rendering` |
| **End-to-end** — the whole local composition | `ReportService.EndToEndTests` | the composition of [`../src/ReportService.AppHost/`](../src/ReportService.AppHost/), started from the test | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- e2e` |
| **Performance** — the verification's smoke | `ReportService.PerformanceTests` | the rendering host image on the PDF base, and the deterministic volume and core datasets; the reference environment only | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- performance --timeout <mm:ss>` |


Every level accepts `--timeout <mm:ss>`, which replaces the deadline that level runs under; the deadline is one for the whole attempt, covering the prepare step, the build and every project. `performance` requires it until a run of that level is recorded, because no recorded span justifies a deadline for it.

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
  dependencies. The **integration** level also needs the local development
  registry `localhost:5055` and the PDF base image: the template source's
  startup class publishes the render worker image from the render worker's
  entry project on that base and runs it. A level without them fails naming
  the prerequisite.
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
- The **performance** level is the C4 smoke of the verification: it prints two documents
  once through the rendering host image and measures the product verification twenty times
  each, writing `performance.json` beside the run's record. It runs on the reference
  environment only, never in the hermetic or integration levels.
- Every test process is given the run's own folder in `REPORTSERVICE_RUN_DIRECTORY`, so a
  class that must leave an artefact for a person writes it into
  `artifacts/test-results/<stamp>/evidence/` through `ReportService.Testing.RunEnvironment`.
- The **rendering** level prints real documents and judges them: it publishes the
  rendering host image on the PDF base and builds the judge image in
  [`../eng/pdf-oracle/`](../eng/pdf-oracle/), then prints the core tier of the
  fixture library with the product composer and printer and reads every document
  with the judge. It needs the local development registry and the PDF base image;
  the runner prepares both, like the end-to-end level. It takes no
  `--without-printing`, because every document it runs is printed. The
  verification-loss corpus is part of the tier: its three loss documents and its
  honest document are printed with the corpus packages, and each reading is
  asserted apart - the product's one reason and the judge's class set - beside the
  supplementary template-authored control that carries the blank trailing page.
  `AtBoundSampleRenderingTests` is part of the tier too: the at-bound record the
  take derives from the control package is printed through the take's own sample
  verb on the PDF base, the product's verification accepts it with its anchored
  field compared against the source, one character is removed from the composed
  HTML after the expected units are extracted and both the product and the judge
  refuse the result with `text-dropped`, a contract that admits the bound nowhere
  spends no render, and the three renders the take runs are measured three
  repetitions each against the delivered render budget and the take's deadline.
  Its **`engines` tier** (`[Trait("Tier", "engines")]`) prints one report in three
  template versions, one per engine, under three locale settings: the three
  composed documents are byte-identical **under the `es-AR` ambient culture and
  under the invariant one, with the engines rendering in parallel** (the host takes
  `--ambient-culture`), the three printed documents pass the product's
  verification and the judge, the two locale variants print their own culture's
  amounts and zone's acceptance instant, and the control that presents every
  formatted value in the invariant culture fails both readings. The tier keeps the
  three engines' documents of one hundred records and the two locale documents in
  the run's `evidence/` folder with `same-report-demo.sha256`.
- The **hermetic PDF structure verification** (`ReportService.PdfStructure.Tests`,
  in the Pdf group `tests/Pdf/`) is what keeps the V-Bounce **Docker-free**
  (ADR-039 §3.5.8). It reads the **committed golden PDFs** in
  [`Pdf/ReportService.PdfStructure.Tests/Goldens/`](Pdf/ReportService.PdfStructure.Tests/Goldens/)
  **in-process** with the product's own reader (PdfPig, `PdfWordReader`) and checks
  block/row order, the global "Page X of Y", row coverage against the source
  fixture and the words of each page — **no browser, no engine, no container**. The
  goldens are produced once by the rendering path and committed with their source
  fixture snapshot and page/frame declaration; their `Goldens/README.md` records
  how to regenerate them.
- The **rendering** regression is a **cycle-level, triggered** suite (ADR-039
  §3.5.6): it runs in the integration/rendering cycle for a change that touches
  rendering, the engine, the PDF base image or a template, **not** as part of the
  V-Bounce. The V-Bounce verifies PDF structure hermetically over the committed
  goldens instead; the live Chromium/Paged.js render, the poppler oracle
  cross-check and the two-reader agreement (PdfPig vs poppler) run in the cycle.
- The **integration** level's `EngineAndPresentationTests` drives three jobs, one per
  engine, over one snapshot, two jobs under two cultures and zones, a block forced to
  retry between streaming and its commit (through the render step's test-only fault
  seam), a version whose engine major the deployment does not run, and a snapshot whose
  amount cannot be presented — asserting the published documents, the two attempt-scoped
  objects of the retried block, and the presentation provenance of every committed row.
  `PresentationProvenanceMigrationTests` holds the provenance column to its two admitted
  shapes against PostgreSQL, and `EnginePresentationEndToEndTests` prints one job that
  names a culture and a zone over the whole local composition. The **performance**
  level prints `B1000` once per engine and records each engine's render time beside the
  same verification thresholds. `AtBoundSampleTakeTests` takes the at-bound control
  versions on the take's own queue - the control taken with its record, its object and
  the object's SHA-256 read back, the clipping version refused `TEMPLATE_INVALID`, a
  held at-bound render retried and never refused, the repeat answered `ALREADY_TAKEN`,
  and the no-record version taken on its contract-synthesised record - and
  `AtBoundSampleJointGroupTests` runs the joint group with the job step: a job naming
  the refusing version fails with the take's code and publishes nothing, one naming the
  control succeeds with one artifact. The joint group names what a commit is missing
  when the job step is not on it, and takes no part in that run rather than passing.

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

### The lint step (analyzers and warnings-as-errors)

The hermetic level's one build is tuned for a fast TDD inner loop: it builds the
projects in parallel (`-maxcpucount`), passes the commit once instead of letting
every project shell out to `git`, and — for the **test** projects only — turns off
the analyzer, code-style and warnings-as-errors gating (it sets `HermeticBuild=true`,
which `Directory.Build.props` reads). That gating is **not** dropped; it is moved to
a dedicated step so it is not paid on every inner-loop build of the test projects.

Run the lint step as an ordinary full-gating build — any build that does **not** set
`HermeticBuild` keeps analyzers, `EnforceCodeStyleInBuild` and
`TreatWarningsAsErrors` on for every project, test projects included:

```
dotnet build dotnet-report-service.slnx -c Release
```

It fails (non-zero) on any analyzer finding or warning-class violation, exactly as
the hermetic build did before. Run it before merging (and it is the gating CI runs);
the hermetic inner loop stays fast. Production projects are unaffected either way —
their gating is never turned off.

### Run evidence

Every run writes its evidence under `artifacts/test-results/<yyMMdd-HHmm>/`
(git-ignored; a second run in the same minute gets `-2`, `-3`, …):

- `<project>.log` — the full console output of each test project;
- `hermetic-build.log` — the full output of the hermetic level's one build, and
  `hermetic-build.proj`, the list of projects it built; both are written by the
  run and are never part of the repository;
- `render-worker-image.log` — the console output of the end-to-end level's one
  image-preparation step, which runs before the tests start;
- `render-worker-startup-image.log` — the console output of the integration
  level's one publication of the render worker image for the template source's
  startup cases, and `render-worker-refused-start.log` and
  `render-worker-normal-start.log` — those two containers' logs, written by the
  class that runs them;
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
- **The integration suite shares one PostgreSQL server per run, with per-test
  isolation preserved.** The PostgreSQL server is started once per run by an
  assembly fixture (`Shared/SharedPostgres.cs`) and reused across the suite
  through the per-class handle `PostgresFixture`; every case still asks for a
  database of its own (`CreateDatabaseAsync`, owned by the `reportservice`
  application role the repository's init script creates), and the databases a
  class created are dropped from the shared server at the class's teardown, so
  the server does not accumulate databases across the run. The server is removed
  at the end of the run by the assembly fixture's own handle. Its
  `max_connections` is raised on the shared container to cover the parallel peak
  (the runner's `maxParallelThreads` classes each with its own database and
  pool).
  - **The classes that need a dedicated PostgreSQL container keep one**
    (`DedicatedPostgresFixture`): the classes that pause/unpause the server or
    read server-level catalogue state (`MetadataDatabaseTests`,
    `Uploads/UploadApiDatabaseTests`) — a pause or a server-level read is visible
    to every test on the same container, so they are never served by the shared
    server. The shared handle deliberately does not expose pause/unpause.
  - **Temporal is NOT shared** — each class that needs an engine starts its own
    (`EngineStart`), as before. The worker task-queue names are constants reused
    across the whole suite in the `default` namespace, so a shared engine's
    workers would drain one another's tasks; per-test isolation could not be
    proven without rewriting every worker host's namespace, so Temporal keeps its
    per-class engine (the safe outcome; isolation is never traded for speed).
  - **Valkey** (one reachability-smoke class) and the **filesystem storage**
    (a per-test temp root, not a container) are unchanged.
  - **Order-randomized covering run (contamination is often order-dependent).**
    The built-in xUnit v3 run seed randomizes test-case order within a collection
    but does not reorder whole classes/collections; `Shared/SeedTestCollectionOrderer.cs`
    is the seed-driven whole-class orderer. Set the environment variable
    `REPORTSERVICE_IT_SEED` to an integer to run the classes in a reproducible
    shuffled order (with no variable set, the order is the deterministic base
    order); the covering run records the exact seed it used so the order can be
    reproduced.
  - Every container the suite starts still removes itself through its own handle;
    nothing is pruned. Container names start with `report-service-it-`.
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
- **The continue-as-new case runs its job in a worker process of its own**, started
  from the product's own orchestration assembly with the case's own settings: the
  wait's two bounds are process-wide, so a host started beside the case in this test
  process would publish over them. See
  `JobInputWaitTests.AWaitThatOutlastsItsChecksContinuesAsANewRunAndIsStillWoken`.
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
settings, the object-store class read and its four states, the version listing, the
by-name resolution and its take seam, the manifest reader, the take itself, and
the adapter that presents the take to the resolution. `ReportService.Workers.Pdf.Tests`
holds that worker's two activities as plain classes and the registration of the
take and of the by-name resolution on `report-pdf-rendering`.

The integration project carries them against real dependencies:
`TemplateVersionStoreTests` and `TemplateVersionReadTests` (the record and its
read on PostgreSQL), `TemplateTakeWorkerTests` (the take on its queue over a real
engine, database and storage root, and the source class over the composition's
own root), `TemplateByNameResolutionTests` (the by-name resolution on the same
queue, one workflow per case), `TemplateSourceClassConformanceTests` (the source
class on the real Filesystem provider: the paged listing, the verified record and
file, a flipped byte, a missing object, a truncated object and a create-only write
the class refuses) and `JointTemplateTakeTests` (the job-level group, on a build
that carries the job step). The fixtures are built by `ReportService.Testing`:
`TemplateSourceClass` publishes a version into a store the way the owner's
pipeline does, and `TemplatePackageFixtures` and `ByNameFixtures` supply the
packages and the families, never checked in. `PdfConstraintFixtures`
(`TemplatePackages/PdfConstraints/`) supplies the one report written for each
engine that the take's PDF constraints admit — table-markup detail, a named font
family on the cell's own rule, the printed totals off — beside the page-total
mutations those cases start from, the two family mutations (the one that replaces
the family with a generic keyword, one package per engine, and the data-shape
package whose grouped row carries a class of its own whose rule sets a generic
keyword after the plain row's named rule), the three grid mutations (a grid row
whose cell is bottom-aligned by its own rule for two engines and by the
container's for the third, the same defect in a package whose sample cannot show
it, and the one package that breaks all three PDF constraints at once).

The take's own evidence is a family of its own:
`TemplatePackages/sample-gate/` holds three versions of one report - Scriban,
Handlebars and Liquid - each with its `owner` and its own `sample.json`, which
is what makes a package takeable. The family is never composed as evidence of
another family: `SampleGatePackagesTests` holds the three engines to byte-
identical composed HTML under both ambient cultures, `SampleControlTierTests`
runs the take's `sample` verb on it and on its two known-bad mutations (a row
that prints no text, an anchor the parts do not print) in the rendering image,
and the two integration classes take it through the real source and job. The
delivered `same-report` family stays untouched as the composition reference.

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

`SingleBlockLimitTests` holds the report that does not fit in one block: each case
starts the composition with the record count it asserts (the setting is read when the
worker starts), submits a snapshot of the families in
`tests/ReportService.Testing/Limits/`, and asserts one state — the job and its PDF
output failed with the same code and the same stored reason, the data step ran once,
no render or publish step was scheduled, no partition record, part commit or artifact
was written and no object reached the output namespace — or, for the document at the
limit, that it printed and published once. `SingleBlockLimitEndToEndTests` follows the
same limit the way a consumer does: upload, confirm, accept and the status read until
it ends, with the message on the job and on its output and the refusal's transcript
kept with the run. `SingleBlockLimitMessageTests` (hermetic) asserts the sentence
itself, and `SingleBlockLimitRefusalMeasurementTests` records what a refusal of fifty
thousand records costs, as an observation on the reference environment with no
threshold.

## The restarted print and the exhausted budget

`PdfRestartTests` (integration level) drives the same job twice through the
product's registrations, in-process on a real engine, a real database and one
storage root: a composition whose guard is passed on every attempt ends the output
with the timeout code and the cause its last attempt recorded, and a package whose
anchors are undeclared is refused after one attempt without ever reading a cause.
Its watchdog case passes the print deadline once, waits for the watchdog's trip,
stops and starts the worker, and asserts the report finishes on the fresh one. The
hosted watchdog and its health answer are the running worker's, never a double.

`RestartAndExhaustionEndToEndTests` (end-to-end level) kills the composition's real
render-worker container with `docker kill` and starts it again: before any part
commit, after the part commit, on every allowed attempt, and after the last one.
It reads the job from the composition's database and storage root, verifies the
published object by size and SHA-256, and reads the runtime's restart policy back
from the container. Its two transcripts are the demo's evidence.

`PdfOutputRefusalTests` registers the output child against a real engine and reads
the child's own history: the refusal arm schedules no cause read and carries no
reason, and the exhausted arm schedules the read exactly once and carries the
reason the failure records. Both histories are written by the same opt-in capture
and replayed hermetically by `PdfOutputHistoryTests`, which skips while the
captures are not committed.

## The independent judge of a printed document

`ReportService.PdfOracle` reads a printed PDF with the text tools of its own image
(`pdfinfo` for the page count, `pdftotext -bbox -enc UTF-8` for every word and its
box) and judges it against the data the document was printed from. It references no
product assembly and no PDF library, so its verdict is reached without the code it
judges, and it shares nothing with the product's own verification.

- **What it judges:** the page's size and frame, the two bands no text may print in,
  the column captions on every page, every record printed once and in order, each
  row's date, description and amount against the record, the amount of a row that
  runs over a page break on the page the row begins on, the header values printed
  before the table, the text printed after it, and the continuation mark the layout
  must not print. A failure names its rule, the kind of failure (`row-lost`,
  `row-duplicated`, `row-order`, `text-dropped`, `text-repeated`, `text-altered`,
  `structure-lost`), the record, the field and the page — never a value of the data
  or of the printed text.
- **What it reads beside the document:** the fixture (a `header-items-v1` snapshot)
  and a declaration of the page the package lays out — paper, margins, columns,
  column captions, the header members printed before the table and the one printed
  after it. The declaration names no page of a row: the row's first page is the page
  its own first text — its number — printed on. The declarations of the rendering
  suite's packages live in
  `ReportService.Testing/OracleExpectations/`, and `ReportService.PdfOracle.Tests`
  holds them to the packages themselves: the column names and their order in the row
  template, the width each column declares in the stylesheet, the offset that width
  and the manifest's left margin put it at, and the fact that the columns fill the
  content area without a gap and without an overlap. A band that agrees only with the
  declaration's own previous band fails.
- **How it is held to itself:** `ReportService.PdfOracle.Tests` builds a passing
  document by hand, plants one failure at a time in its words — a lost word on the
  page a row continues on, a repeated line, a lost row, swapped words, a row out of
  order, a duplicated row, a page without text, a record number the data does not
  hold — and requires each to be reported with the class it plants. A second group
  changes one cell of a row and nothing else — the amount, another record's amount,
  the date, an amount that prints nowhere, a wrong amount on the second page, a
  correct amount moved to the page a split row continues on — so the rule that owns
  a row's date and amount (TI-4) is proved to fail on the failure it exists for, and
  a third plants a continuation mark outside every row for the half of TI-7 no
  control reaches. The command has its own cases: its one verb, its options, the
  verdict it prints or writes, and the three exit codes.
- **Command:** `ReportService.PdfOracle verdict --pdf <file> --fixture <snapshot>
  --expectation <declaration> [--out <verdict>]`. It exits 0 for a verdict (a
  document that fails a rule is a verdict, not an error), 1 when the document, the
  snapshot or the declaration cannot be read, and 2 on a usage error. Each text tool
  is given two minutes and is stopped when it outlives them, so a read that hangs is
  refused instead of holding the suite; a caller that cancels gets its cancellation
  back and no verdict. The tool needs `poppler-utils`, so it runs in its own image,
  never on a host.

