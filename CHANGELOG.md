# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- A record may now be up to 2 MiB (it was 1 MiB), so a picture the producer has
  computed can travel in the record as a `data:` URI; a single token follows the
  record limit and the header stays at 1 MiB. Blocks still gather records up to
  1 MiB, and a larger record is printed in a block of its own. On a PDF, a value
  that is a base64 image data URI (PNG, JPEG, GIF, WebP or SVG) may carry up to
  1 MiB, while every other value keeps the 256 KiB bound; the
  `ROW_BOUND_EXCEEDED` diagnostic names the bound that applied. The Scriban
  engine converts values of up to 2 Mi characters, so a value at the record
  limit is never cut. A template sample may be a single record of up to 2 MiB.

### Added

- A job's PDF work is now dispatched by its priority class. Each class has its own
  render queue and its own merge queue (`report-pdf-rendering-high`,
  `report-pdf-rendering`, `report-pdf-rendering-low`, and the same for merge); the
  normal class keeps the existing names, so a deployment that sets no priority runs
  as before. A render or merge worker serves the one class `Priority:ServedClass`
  names (`normal` when absent); capacity per class is dedicated and its amount is
  the deployment's. A `low` job whose work has waited `Priority:Aging:Wait` (default
  10 minutes) is promoted to `normal` for the work it sends afterwards; work already
  sent completes where it is. Three instruments are added:
  `reportservice.taskqueue.pending`, `reportservice.taskqueue.schedule_to_start.duration`
  and `reportservice.job.priority.promotions`. See
  `docs/operations/priority-and-per-class-capacity.md`.

- The size of one PDF document is now the deployment's configured capacity,
  `DocumentCapacity:MaximumPages`, `DocumentCapacity:MaximumPartBytes` and
  `DocumentCapacity:ProfileName`, instead of a value compiled into the service. A
  deployment that configures none runs the example values, which equal the former
  bound (10 000 pages or 37 748 736 bytes of rendered parts), so behaviour is unchanged
  until a deployment states its own. `OUTPUT_LIMIT_EXCEEDED` keeps its code and its
  diagnostic, which now names the configured value. The assembly's temporary-disk quota
  adds 16 KiB per page when the template declares a per-page code. Every assembly
  records the capacity it was admitted against. A new page, `docs/document-capacity.md`,
  describes the settings, the answer above a deployment's capacity and the separation of
  document size from concurrency, and holds the figures of one document's assembly at
  10 000 and 50 000 pages with per-page codes - a lower bound, measured on synthetic
  pages: 389.0 MiB at 10 000 pages and 1 424.4 MiB at 50 000 pages, so a 50 000-page
  document needs a merge worker larger than the local composition's 512 MiB (see
  `docs/limitations.md`).
- A job request may declare a `priority` of `low`, `normal` or `high`; a request that
  declares none is created at `normal`, and any other value is refused with
  `400 REQUEST_INVALID` naming the field. The priority is fixed when the job is accepted,
  is part of the request's idempotency identity (a request naming `normal` and one naming
  none are the same request, so every existing key keeps its identity), and is read back
  on the job status as `priority.declared` and `priority.effective`. A deployment may
  restrict `high` with `Jobs:Priority:High:Restriction` (`none`, `allow-list` with
  `Jobs:Priority:High:AllowedConsumers`, or `disabled`); a `high` it does not admit is
  refused with `422 UNSUPPORTED_CAPABILITY`. The metadata database gains the two priority
  columns of a job (migration `0021`); existing jobs read `normal`. The priority does not
  yet change the order work is dispatched in.
- Two example packages, `invoice-batch` and `invoice-batch-two-up`, print a batch of
  invoices in one document: each record is one invoice with its nested lines, its
  precomputed totals, flags that show or hide a field or the barcode, and the
  precomputed bars of a Code 128 barcode printed as inline SVG; one invoice per page in
  the first, two per page in the second. The authoring guide gains "Printing a batch of
  invoices": the record-per-invoice pattern, the layout as the template's choice, what
  the consumer computes before the job, the data values the take's current rule refuses
  in an image source, conditions over flags, and what the adopter owns.

### Changed

- The single-block limit of PDF reports is removed. A PDF report may now span several
  blocks: the data step partitions the snapshot into ordered blocks with a durable index,
  the blocks are rendered within the bounded window, assembled in order and stamped with
  one global "Page X of Y". The `Partitioning:SingleBlockRecordLimit` setting, its
  validation and its start-up line no longer exist; a deployment that still sets the key
  starts normally and the value has no effect. The only size bound a PDF report has is the
  published document limit - 10 000 pages or 37 748 736 bytes of rendered parts, whichever
  is reached first - and a report beyond it ends `OUTPUT_LIMIT_EXCEEDED`, publishing
  nothing.

## [0.9.0] - 2026-11-23

This release takes the product from the groundwork skeleton of `0.1.0` — which
answered health checks only and rendered no report — to a working end-to-end
report service. A consumer can now open and stream an upload, have it verified
into an immutable snapshot, create a report job against a pinned template
version, and receive a printed PDF that is validated before it is published and
then listed and downloaded with integrity checks and an expiry. Along the way
the service gained template-version take and pinning, data-contract validation
of the snapshot, three peer template engines, idempotent job creation, job
progress and status reads, published-artifact retention, and a growing set of
measured rendering spikes.

The repository was also reorganized into a single, flatter layout: the service
hosts and the tests now sit together in one clear structure that is easier to
navigate and build, and the rendering regression suite derives its container
image tags from the product version instead of carrying fixed tags, so a
version change flows through automatically.

**Version numbering:** this release is **0.9.0**. The project moved directly
from the `0.2.0` development line to `0.9.0` to signal the pre-1.0 final
stretch, so there are no `v0.2.0`…`v0.8.0` tags — the previous tag is `v0.1.0`.

### Added

- A report whose print is interrupted by a restart finishes with one document: the
  attempt that died is delivered again, a committed part is returned instead of
  printed again, and the output is published once - no second artifact and no
  duplicated or missing pages. A report whose every allowed attempt fails for a
  reason that is not its data or its template ends failed with a code the consumer
  can act on: `RENDER_TIMEOUT` when the last attempt passed a deadline, and
  `EXECUTION_FAILED` when the worker was lost, with nothing published and a fixed
  sentence that carries no internal detail. A watchdog stops a render that neither
  writes nor returns, records the attempt's cause, reports the worker unhealthy and
  ends its process, and the platform's replacement finishes the report; the
  attempt-cause record is kept with the job and is not a status. The render worker's
  restart policy is declared by the composition and read back from the runtime.

- A PDF template version is taken only after a record whose largest text field is
  exactly 256 KiB of UTF-8 - the row bound - is printed with every character on the
  service's own print path and checked against the data contract: a version whose
  page drops, alters or hides part of that record is refused
  `TEMPLATE_INVALID` with the rule `at-bound-sample`, naming the file and the
  field's path, and nothing of the version is kept. The field is the first place
  the version's own data contract can carry that much text - the record's own
  field, a field inside an object or an array, a member an open record admits, the
  text side of a nullable field, or a field reached only through a combined,
  conditional, dependent or fixed-position shape - item anchors first; a field
  whose own limit or shape cannot hold 256 KiB is skipped; a version whose
  contract can carry 256 KiB nowhere is taken, and so is one whose sample carries
  no detail record (its record is built from the contract). The check writes one
  `at-bound-sample` log line per take and never carries a value of the data.

- A PDF report whose print is missing a record or part of its text is refused
  before anything is published, and the refusal is proved through a job: the PDF
  output and the job end `failed` with `PAGINATION_FAILED`, nothing is
  downloadable, no part is recorded and no object is written under the output's
  prefix. A block whose stored bytes are not the bytes that passed the checks is
  refused the same way (`EXECUTION_FAILED`), leaving its object as an unreferenced
  orphan for the storage sweep. The known-bad documents stay in the rendering
  regression suite, where both readers must refuse them, and one honest document
  proves the checks are not a refusal of correct reports.

- A job request may carry an `Idempotency-Key` header: the key binds the job it creates
  to the consumer that sent it, and a request repeated with the same key is answered the
  first job - the same `202`, the same identifier, nothing written - instead of creating a
  second report. The same key with a different upload, template, version, format or
  options is refused with `409 IDEMPOTENCY_CONFLICT` and leaves the first job unchanged.
  Keys belong to one consumer, a malformed key is refused as a malformed request, and a
  retry is answered its first job even after its upload was refused or expired, or after
  a newer template version was taken. A request without the header behaves exactly as
  before.
- A template version is taken only after its sample renders to HTML and PDF
  with the engine the version declares, and then once more with zero records: a
  version whose sample cannot render, print or verify is refused
  `TEMPLATE_INVALID` naming the package-relative file and the fix, nothing is
  kept, and the empty report must still print its header. Each take writes one
  `sample-render` log line and a `template.sample` span, and the two renders run
  under a 20-second budget (`Templates:Sample:RenderBudgetSeconds`, at most 22
  seconds). The three engines are peers: a version names the one it is written
  for.
- A PDF report whose detail data carries a text value above 256 KiB of UTF-8 is
  refused before printing: the job and its PDF output end `failed` with
  `ROW_BOUND_EXCEEDED`, no page is rendered and nothing is published, and the job
  status says which record and field to shorten — the record's number, the field's
  path, its size and the bound, never the value.
- A PDF report whose detail data carries a text value of up to 256 KiB of UTF-8
  prints it in full, across as many pages as the row needs: every character in
  order, no blank page inside the row, the amount on the row's first page and no
  "(continues)" mark. The bound counts bytes, not characters, so two- and
  three-byte text prints fewer characters for the same 256 KiB — all of them.
- A PDF report whose data does not fit in one block is refused before anything is
  printed: the job and its PDF output end `failed` with `QUOTA_EXCEEDED`, and the job
  status says which limit the data exceeded, in records or in bytes, naming the limit
  that was actually applied. The limit is temporary and will be removed when reports
  can span more than one block; [`docs/limitations.md`](docs/limitations.md) states it,
  why it exists and what removes it.
- The job status read carries `errorMessage` on the job and on each output: the failed
  job's condition in plain words, `null` when it has none in this version. It never
  names a value of the report or a setting of the deployment.
- A report may be written for Scriban, Handlebars or Liquid, whichever its template
  version declares: the three engines are peers, the version's engine is the one
  that composes its block, and a version whose engine this deployment does not offer,
  declares none, disagrees with the job's pin, or names another major of the engine
  version ends its job `failed` before anything is printed. Dates and amounts are
  printed in the culture and the time zone fixed when the job was accepted, with the
  report's "now" taken from that same instant - on every attempt of a retried block -
  through one small set of presentation functions the three engines share. A version
  that presents a value the service cannot format (`DATA_INVALID`, `TEMPLATE_INVALID`)
  ends `failed` with nothing published. Each committed block records what produced its
  presentation - the culture, the zone, the acceptance instant, the engine and its
  version, and the globalisation data of the image - and the block's log line names
  the same identifiers.
- A template version is checked when it is taken, and a broken one is refused before
  anything is kept: an invalid `schema.json`, a `sample.json` that breaks it, a syntax
  error in a part for its engine, a missing include or asset, an unescaped data output,
  an unsupported declaration, an unusable PDF page setting or an engine version this
  build does not render ends the job `failed` with `TEMPLATE_INVALID` or
  `UNSUPPORTED_CAPABILITY`, naming the file to fix. A version published more than once
  is refused and the conflict is logged as `TEMPLATE_VERSION_CONFLICT`.
- A job's published files can be listed and downloaded, verified to the last byte:
  `GET /api/v1/report-jobs/{id}/artifacts` answers the job's owner with each file's
  identifier, format, media type, size, SHA-256 and expiry, and
  `GET /api/v1/report-jobs/{id}/artifacts/{artifactId}/download` streams that file
  through the verifying reader, aborting the transfer — never delivering it — when the
  stored bytes no longer match their size or their hash. The downloaded name is built
  from the job's identifier, the length is declared up front, and the content is never
  held whole.
- Each published file has an expiry, recorded with it at publication from the
  orchestration worker's `Retention__Outputs` setting: the publication instant plus the
  configured retention, never recomputed at read time. Once it has passed, the download
  answers `410 ARTIFACT_EXPIRED` from the service's records alone — the stored object is
  not read, the job stays `succeeded` and nothing is written — while the file stays in
  the listing with its past instant. The worker refuses to start without a usable value,
  and the local composition supplies seven days.
- A PDF template version is refused when it is taken if its detail grid aligns a cell
  that may split across pages to the bottom, if its detail text names no font family,
  or if it declares printed page subtotals. Each refusal names the file to fix, and for
  the grid cell also the cell and the rule that aligns it; the PDF rules run in that
  fixed order, so a version that breaks several of them reports the page totals first,
  then the font family, then the grid cell.
- The shipped sample templates now name their font family (`"Liberation Sans",
  sans-serif`) and declare no printed page totals, so every sample the product ships
  passes its own take.
- A template version published more than once is never chosen by a reference that
  names the template only: the service logs `TEMPLATE_VERSION_CONFLICT` naming the
  version and the duplicate publication, skips it like any invalid version, and the
  job uses the newest valid release below it. A correction is published under a new
  version; the template guide says so.

### Changed

- The access rules of the report-job reads — the status read, the artifact list and the
  download — are stated in one place: the consumer registry as the gate, its one refusal
  for a missing or unknown consumer, another consumer's job or file answered exactly as a
  missing one, an inactive consumer keeping the results it already produced, and a
  registry change taking effect without a restart. The refusal counter's `job-status`,
  `artifact-list` and `artifact-download` values are covered by tests.
- A taken template version's metadata is readable by every registered consumer when the version
  is shared: the answer is the owner's own, the publication descriptor's hash, the package hash
  and the files the take verified included, while another consumer's non-shared version still
  answers exactly as a version that does not exist.
- A template version is read from the deployment's object storage, through the
  deployment's own store and the `template-source` class: the version's publication
  record is opened with the hash its own name carries and the size the listing gave,
  checked against its own rules and read with its files, and every file is streamed
  only against the size and SHA-256 the record declares. A version that does not
  exist, a publication that was never completed, one published more than once, an
  invalid record and a source that cannot be read are told apart and answered with
  the code each one means; a completed read that contradicts its record is never
  reported as an outage. The service only reads the class: it never writes, deletes
  or repairs it. The by-name resolution lists the class's published versions, and the
  version's name is enforced against the record and the manifest.
- The taken version records the publication record's hash, the key it was read from
  and the verified file list, and its commit columns are empty; a version taken
  before the change keeps the values it recorded.
- A version already taken is watched where the by-name path reads the template
  source: when the source lists a publication record other than the one the
  version was taken with, the service logs `TEMPLATE_VERSION_CONFLICT` once,
  naming both record hashes and asking for a version bump, keeps the taken copy
  and its metadata, and refuses no job. The comparison reads the listing only —
  never a file of the package — and a version taken before the object store has no
  publication identity, so it is never compared. The take's own log line now names
  the publication it read and, when a conflict was recorded, which observation
  produced it.

### Added

- A report job prints the PDF it reports on and publishes it, once: an accepted job over a
  ready snapshot reads the snapshot, partitions it into one block and its index page,
  composes the block with the engine of the template version the job pinned, prints it on
  the browser's own native layout, verifies the printed document, and publishes exactly one
  output-class artifact. The job ends `succeeded` when its output does, and `failed` with the
  output's code when it does not; the render reads only the taken copy, so a template source
  that is unreachable after the version was taken changes nothing.
- Every printed PDF is verified before it is published: each record's row is printed once,
  in the snapshot's order, every declared field is printed as it is, every character of the
  composed text is in the document, and a document has at least one page with a word on
  every page. A document that fails is never published and never retried. What is checked,
  with which reader and why is documented in
  [docs/pdf-verification.md](docs/pdf-verification.md).
- A template declares the fields it prints as they are under
  `outputs.pdf.verification.anchors`, and a version that declares none is refused before
  anything is printed. The constraints the verification puts on a template — no generated or
  hidden text, no hyphenation, one named font family — and the values a template can read
  are documented in [docs/template-authoring.md](docs/template-authoring.md).
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) names every package the shipped worker
  images carry, at the exact version this repository pins, with its licence, its copyright
  line and the notice the licence requires, including the material PdfPig bundles and the
  colour profile it embeds.
- The rendering regression suite prints the core set of documents and judges every one of
  them with an independent reader as well as with the product's own verification, failing on
  any disagreement, and keeps a known-bad table document as a permanent case.

- A job's template version is fixed once, right after the request is accepted, and never
  again: a version already taken is bound with one read of the service's records without
  reading the source at all, one that is not taken is taken from the configured repository
  and branch, and a reference by name resolves the highest release version. The version, the
  commit, the package hash, the engine, the engine's version, the schema and the instant it
  was resolved are recorded on the job and never re-resolved — not on a retry, not when a
  worker restarts, not when a newer version is taken later.
- A template that cannot be used fails the job and its PDF output after the `202` with a
  code that says what to do: `TEMPLATE_NOT_FOUND` (it does not exist, or it is another
  consumer's and is not shared — one identical answer), `TEMPLATE_INVALID` (the message
  names what its author has to fix), `TEMPLATE_SOURCE_UNAVAILABLE` (the source could not be
  read; submit again later) and `UNSUPPORTED_CAPABILITY` (the engine is not one this
  deployment renders). The message travels to the job and is kept with it.
- A template reference is refused at acceptance with `404 TEMPLATE_NOT_FOUND` when it can
  never name a template: an identifier outside the lower-case template grammar, or a version
  that is not a semantic version — a range, a branch name or a partial version. A version
  already taken that belongs to another consumer and is not `shared` receives the same
  answer, exactly as if it did not exist.
- A reference by name records, when the job is accepted, the highest release version already
  taken that its consumer may use; if the template source cannot be checked later, the job
  uses that version and logs that the source could not be checked.

### Changed

- A job's template version is now fixed with the publication descriptor's hash, the package hash
  and the verified per-file list instead of a commit; the version's metadata answer carries the
  same provenance and no commit SHA. A version taken before the change keeps the pin it recorded
  and keeps rendering from its copy.
- The job's run continues past its data step: it moves the job to running, starts the
  output's own execution, and closes the job from the output's outcome. The steps are gated
  by a patch, so every history recorded before them still replays unchanged.
- `report_service.artifacts` and `report_service.pdf_part_commits` are written by the render
  and the publication; `partitions` by the data step. An output is published once, under the
  guard that refuses a job that is ending and an output that is no longer running.
- The telemetry of a printed block now carries the four phases of a render as a histogram,
  a verification outcome counter with its failure class, a page-count histogram and the
  count of aborted browser requests.
- `report_jobs` gains `error_message` (at most 1 000 characters), and
  `template_references` gains the by-name baseline and the pin: the resolved version, the
  commit, the package hash, the engine and its version, the schema reference and the instant
  of resolution. The database refuses any change to the reference as written and any second
  write of a pin, so the first committed pin is the version the job keeps.
- A job whose template step runs is fixed before its data is read: a run recorded before this
  step replays unchanged and never schedules it, and the step itself is bounded well inside
  the workflow's history budget.

- A report job can be followed: `GET /api/v1/report-jobs/{reportJobId}` answers the
  job's owner with its state, the step it is in, the counters `recordsRead`,
  `blocksCompleted` and `pagesGenerated` — each of which only rises, across retries
  and restarts — and the totals `recordsTotal` and `blocksTotal`, `null` until the
  job has counted them and never a guess (a total of `0` is a counted, empty
  report). Each requested output carries its own state and, once published, the
  identifier of its file; a failed job and its failed output carry the stable code
  of the condition. No percentage is given anywhere. The answer is never cached and
  one read is one indexed query.
- A job's run now ends by closing the job from the outcome of its outputs: the last
  step of the run finalises the job idempotently — every output succeeded makes it
  `succeeded`, a failed output makes it `failed` with that output's code, and a job
  whose outputs have not ended is left as its steps left it. The step and the job's
  first transition are gated by one patch, so every history recorded before them
  still replays unchanged.
- A job's stage is written with its status and never moves back: the first
  transition records `validating-data`, and the rendering transition records
  `rendering-pdf`. The step a finished job last entered is kept in storage and
  hidden from the answer, because a finished job is in no step.

- A job's snapshot is checked against the data contract of the template version it
  is pinned to before anything is rendered: the header and every record are
  validated against the template's `schema.json`, in the same single pass that
  reads the snapshot, and a value that breaks the schema ends the job `failed`
  with `DATA_INVALID` and a diagnostic naming the ordinal, the byte offset, the
  field's path and the rule — never the value. A violation anywhere in the
  snapshot, the last record of a full block included, ends the job before the
  first page is rendered, and nothing of it is published. The schema is read
  from the taken copy of the pinned version through a verifying read, compiled
  once per package per worker and kept in a bounded cache; the validator is
  `Corvus.Text.Json` 5.6.1, pinned exactly, with its licence recorded in
  `THIRD-PARTY-NOTICES.md`.
- A template whose schema cannot be used — absent from the taken copy, outside
  the admitted profile, not compilable, or too slow or too deep to decide a
  value — ends the job `failed` with `TEMPLATE_INVALID` in one attempt instead.
- A job's own template reference is read before its snapshot, and the version it
  names is looked up in the taken versions: a reference that names no version, or
  one that was never taken, ends the job `TEMPLATE_INVALID rule=pin` in one
  attempt, with no exception and no retry.
- Referencing a template by name only: a job that names no version gets the
  highest valid **release** version of that template, ordered by Semantic
  Versioning 2.0.0 — so `1.10.0` is newer than `1.2.0`, a pre-release sorts below
  its release and is used only when a job names it exactly, and two builds of one
  version are of equal precedence. A newer version its author has to fix is
  logged with what to fix and skipped, so one broken version never breaks the
  jobs that name the template only; when no version is usable the job ends failed
  with `TEMPLATE_INVALID` naming the newest one, and when the template repository
  cannot be checked it uses the newest release already taken or ends failed with
  `TEMPLATE_SOURCE_UNAVAILABLE`. A taken version stays usable after its folder is
  removed from the repository, and a version the requesting consumer may not use
  is never chosen.
- `ResolveTemplateVersion`, the activity the PDF worker serves on its queue: it
  reads the template's version folders at one commit, orders them by precedence,
  takes each candidate through the take, and answers the version a job is pinned
  to with the values the take records, saying whether it was taken now or is the
  copy already kept. A refusal ends it with the resolution's own code, retrying a
  source that may come back and never retrying a template its author has to fix.
- `TemplateSource:ResolveDeadline`: how long one by-name resolution — the listing
  and every version it takes — may take, from five seconds to five minutes, four
  minutes by default, validated at start.
- The take's registration now shares the queue with the by-name resolution: one
  engine worker on `report-pdf-rendering` serves both steps at one activity at a
  time.
- The template repository now names what a by-name reference gets, in
  `samples/templates/README.md`, for template authors.
- The take: the first time a job references a template version, the service reads
  it from the configured repository and branch at one commit and keeps it as an
  immutable copy — one write-once object under
  `templates/<owner>/<templateId>/<version>/<sha256>`, made usable only by an
  insert-once record — so a version taken once is never taken again and never
  changes afterwards. A version already taken is answered from its record without
  reading the source; a version a consumer may not use is answered as missing
  before any refusal that could name a file of the package; a taken version that
  changed in the branch is logged as `TEMPLATE_VERSION_CONFLICT` and the copy is
  kept. A source that cannot be reached, an absent version and the refusals an
  author has to fix are told apart by the job's own error code.
- `TakeTemplateVersion`, the activity the PDF worker serves on its queue: it runs
  one take at a time, answers the values a job is pinned to (commit, package hash
  and size, engine and its version, schema path, owner and the shared flag), and
  ends a refusal with the take's own code, retrying only a source that may come
  back.
- Reading `template.yaml`: one reader, through the YamlDotNet 18.1.0 event
  parser, under a restricted subset with size, depth and node limits; the document
  is stored once as JSON, so nothing that reads a taken version needs YAML again.
- `THIRD-PARTY-NOTICES.md`, with YamlDotNet 18.1.0's MIT licence and the
  copyright line the package itself records.
- The pilot template manifests name their owning consumer and their `shared` flag,
  declare the fields their printed PDF is verified against, and write their part
  paths under `parts`; `samples/templates/README.md` documents the manifest subset
  for template authors.
- `tests/ReportService.Workers.Pdf.Tests`: the take's registration on its queue
  and the activity as a plain class.
- A report job created on an upload that is still being verified waits for it
  instead of stopping at `queued`: it reports the stage `waiting-for-input`, it
  starts once the upload is ready, it ends `failed` with the upload's own code
  when the upload is rejected, and it ends `failed` with `INPUT_NOT_FOUND` when
  the upload's verification deadline passes. The wait re-checks the upload on a
  notification a finished verification sends to the jobs of its upload and on a
  durable timer of its own, so a lost notification costs at most half a minute,
  and a wait that outlasts the checks one run may take continues as a new run of
  the same execution. `Jobs__InputWait__Backstop` and
  `Jobs__InputWait__ContinueAsNewAfter` set the two bounds; a pass limit the
  history of one run cannot carry stops the worker at start.
- The taken template version: a version read from the configured template
  repository and branch at one commit, kept as one write-once object under
  `templates/<owner>/<templateId>/<version>/<sha256>` and made usable by an
  insert-once record whose primary key is the template and the version, so a
  taken version is never overwritten and never taken twice. The record holds the
  commit, the package hash and size, the engine and its version, the schema path
  and the whole manifest as JSON, so nothing that reads a taken version ever
  needs the source format again.
- The `report_service.template_versions` table and its migration, and the
  record store behind it (Dapper over Npgsql, an insert that lets the primary
  key decide a race, constant SQL with parameters only).
- `ReportService.TemplatePackages`: the content-addressed key of a package, the
  semantic version syntax, the deterministic package archive (one uncompressed
  ustar archive whose bytes depend on the files alone) and the reader that
  extracts a taken copy only after the whole object verified.
- `ReportService.TemplateSource`: the settings of the configured source and
  their bounds, a client that reads a branch at one commit, walks to
  `templates/<templateId>/<version>/`, lists its files and streams each blob
  verified against its git hash, and classifies every failure as unreachable,
  misconfigured, too large or unsupported — never as "the version does not
  exist". A tree the source reports as truncated is refused rather than read as
  an absent version.
- The warning a taken version logs when it reads differently from its copy
  (`TEMPLATE_VERSION_CONFLICT`), which keeps the copy and refuses nothing, and
  the seam the take-time checks plug into (no check is registered yet).
- The template-source test double: a request handler that answers the source's
  documented shapes with real git object names, and an in-memory source whose
  versions can be seeded and replaced.
- `GET /api/v1/templates/{templateId}/versions/{version}`: the owner's read of a
  taken template version answers the commit, the package hash, the engine and its
  version, the owner and the shared flag the take fixed, plus when it was taken.
  The operation reads the record only — no storage and no template repository —
  and answers another consumer's version exactly like one that was never taken.
  See `docs/api/templates.md`.

- Report jobs: a consumer creates one with `POST /api/v1/report-jobs` for a
  confirmed upload it owns, a template reference and the output format it wants,
  and receives `202 Accepted` with the job's identifier, the state `queued` and
  the URL its status is read from. The job, its requested output, the template
  reference exactly as the request wrote it and the request to start it are
  written together in one transaction before the answer is given, so a client
  that closes its connection after sending its request has still created a job
  that runs. The orchestration worker starts each accepted job exactly once,
  across restarts of both processes; a job whose upload is refused or runs out
  of time ends failed with the upload's own condition. The creation operation
  checks the consumer against the registry before it reads anything else and
  answers an unsupported format or profile, an unusable data reference and a
  malformed body with their own codes; `Idempotency-Key`, `render` and
  `metadata` are not honoured yet. See `docs/api/report-jobs.md` and
  `docs/operations/upload-verification.md`.

- An upload ends by itself when nobody finishes it: one that is not confirmed
  before the end of its session becomes `expired`, takes no more content and
  cannot be confirmed, and one whose verification outlasts the deadline recorded
  at its confirmation expires as well and can never become a snapshot
  afterwards. The end is applied at the moment of any operation on the upload
  and, without traffic, by a periodic sweep in the orchestration worker that
  also deletes what an expired upload was still holding. The deadline is
  configured by `Uploads__VerificationDeadline` (six hours by default, never
  shorter than one verification attempt) and the sweep by
  `Uploads__Expiry__SweepInterval` and `Uploads__Expiry__BatchSize`. See
  `docs/api/uploads.md` and `docs/operations/upload-verification.md`.
- Every upload operation resolves the declared consumer against the service's
  consumer registry before it reads the body or touches a record: a consumer
  that is not registered is refused with `CONSUMER_UNKNOWN`, an inactive one
  cannot open a new upload (`CONSUMER_INACTIVE`) but keeps reading, streaming to
  and confirming the uploads it opened while it was active, and a registry change
  applies without a restart.

- Report data can be **streamed** to an upload — plain, or compressed with
  `Content-Encoding: gzip` — and then **confirmed**: the body is written to the
  upload's staging object as it arrives, counted against the size limit and never
  held in memory, and the confirmation answers `202 Accepted` with the upload
  `verifying`. The service then verifies the stored bytes in the background and
  keeps an immutable snapshot with the SHA-256 and size of the content, or ends the
  upload rejected with a stable code and reason that the status read shows back.
  Content sent to an upload that is already ready is refused and its snapshot stays
  as it was. See `docs/api/uploads.md` and
  `docs/operations/upload-verification.md`.
- Upload verification: a stored upload is read once, a copy of exactly those
  bytes is written to a snapshot object, and the content is measured as it is
  read — the bytes stored, the content they decompress to, and whether a
  compressed object is one complete unit of content whose own trailer agrees
  with what was read. A verified upload ends with an immutable snapshot record
  carrying the hash and size of the content; anything else ends rejected with a
  code and a reason, and no snapshot exists for it. The upload record and its
  store now live in one library shared by the API and the orchestration worker.
  See `docs/operations/upload-verification.md`.

- Upload sessions: a consumer opens one for its report data with
  `POST /api/v1/uploads` (`header-items-v1`, `API_STREAM`) and receives its
  `uploadId`, the state `created` and the time it expires, then reads the same
  status back with `GET /api/v1/uploads/{uploadId}`. A request for any other
  profile or mode — including direct-to-storage, which this version does not
  offer — is refused at once with `UNSUPPORTED_CAPABILITY` and no session is
  created. The declared consumer, carried in the `X-Consumer-Id` header, owns
  every session it creates, and another consumer's session is answered as one
  that does not exist.
- The `report_service.uploads` table, its migration, and the record store behind
  the upload operations (Dapper over Npgsql, owner-scoped reads, a five-second
  command timeout).
- The API's error answers are RFC 9457 problem documents carrying a stable
  `code` and the request's `traceId`: `CONSUMER_UNKNOWN` (403),
  `UNSUPPORTED_CAPABILITY` (422), `QUOTA_EXCEEDED` (413), `REQUEST_INVALID`
  (400, with `field`, `reason` and `unknownFieldCount`), `INPUT_NOT_FOUND` (404)
  and `SERVICE_UNAVAILABLE` (503 or 500). No answer carries an exception message,
  a statement, a connection detail or a value the caller sent.
- `Uploads__SessionLifetime`: how long a new upload session accepts data, one day
  by default, validated at start.
- `docs/api/uploads.md`: the identity header, both operations, the session states
  and every error code.
- The service's own metadata database and credentials: the `reportservice`
  database, owned by the `reportservice` login role, which is not a superuser
  and cannot create databases or roles. The local composition creates both once,
  from a script the PostgreSQL image runs at initialisation, with a password
  generated at every start; no process of the service receives the server's
  bootstrap user.
- `ReportService.Migrations`, a one-shot migrator: versioned, forward-only SQL
  scripts embedded in the build, applied in order under a PostgreSQL advisory
  lock, each in its own transaction with a journal row that records its SHA-256
  checksum. It refuses a script that changed after it was applied and a journal
  entry the build does not know, and exits non-zero naming the failing script.
  Its baseline creates the `report_service` schema; the upload sessions add the
  first business table beside it.
- A `metadata-database` readiness check in the API and the orchestration worker:
  ready only when the database is reachable and at the schema version the build
  expects, with the expected and applied versions in the health body. The PDF
  worker has no database check until it writes its first record.
- `ReportService.Migrations.Tests`, the hermetic unit and component tests of the
  migrator, with a coverage floor in the test runner.
- `docs/metadata-database.md`: the database and its role, the journal, how a
  change adds a migration, and how an operator provisions the database and
  applies migrations to an existing deployment.

### Changed

- **The package path rule now holds what the package archive can write:** one to
  eight segments of at most a hundred characters each and at most 240 characters
  in total, so a path the rule accepts can never fail to be written. A segment or a
  path over those lengths is refused by the rule itself.

- The error format is stated as RFC 9457 (Problem Details for HTTP APIs), which
  supersedes the RFC 7807 reference in the README.
- The API's `/health` body is JSON, like the workers': the metadata database's
  check reports the expected and applied schema versions. Status codes are
  unchanged (200 ready, 503 not ready).
- The health body's allowed data keys gain `expected`, `applied` and `error`.
- `Npgsql` becomes a product package and `Dapper` joins it; the composition and
  the processes that use the database connect as the application role only.
- A host's telemetry is now its own: its instruments are created on a meter
  identity the host carries, its metric provider subscribes to that identity, and
  a unit of work opens its `job_id` log scope on the logger factory of the host
  that opens it. A process that owns its host exports the same instrumentation
  scope name (`ReportService`), the same instruments and the same `job_id` scope
  as before; a process that hosts more than one host no longer mixes their
  telemetry.

### Fixed

- A rendered part is now stored under a key that fits a storage root of 111
  characters: the attempt's identity inside the key carries 96 bits instead of
  the whole digest. The object storage page states the path budget every class
  of stored object needs — the longest key of each one, the room a storage root
  leaves free for it, and the 38 characters a write's temporary file adds.

#### Measured spikes

Throwaway evaluation programs under `spikes/`, kept for their measurements.
They are not part of the product: they are not in the solution and are never
packaged or shipped. Each README states the hypothesis, how to run the spike,
the measured results and the conclusions. Added since `0.1.0` (`06`–`15`), all
measuring the PDF pagination, layout, assembly and verification path:

- PDF assembly on Chromium-printed parts
  ([`spikes/06-pdf-assembly-chromium-parts/`](spikes/06-pdf-assembly-chromium-parts/README.md)):
  assembling the final PDF from Chromium-printed partial documents, with
  independent checks on the merged output.
- Native pagination of tall rows
  ([`spikes/07-native-tall-rows/`](spikes/07-native-tall-rows/README.md)):
  how Chromium's native layout paginates very tall rows and long free text.
- Pre-split layout
  ([`spikes/08-pagedjs-pre-split/`](spikes/08-pagedjs-pre-split/README.md)):
  splitting tall rows before the pagination library lays the page out.
- Two-pass page subtotals
  ([`spikes/09-two-pass-page-subtotals/`](spikes/09-two-pass-page-subtotals/README.md)):
  computing per-page subtotals over the native layout in a second pass.
- Table-and-grid strict accounting
  ([`spikes/10-pagedjs-table-strict-accounting/`](spikes/10-pagedjs-table-strict-accounting/README.md)):
  laying the fixture series out with rows pre-split, checked page by page.
- Row-end link of a split row
  ([`spikes/11-row-end-link-threshold/`](spikes/11-row-end-link-threshold/README.md)):
  where a split row continues across a page under native layout.
- Group continuity across pages
  ([`spikes/12-group-continuity/`](spikes/12-group-continuity/README.md)):
  groups that cross page boundaries, with rows pre-split in grid markup.
- Group continuity across blocks
  ([`spikes/13-group-continuity-across-blocks/`](spikes/13-group-continuity-across-blocks/README.md)):
  what prints at a block boundary when a grouped report is assembled from parts.
- Group-rule feasibility
  ([`spikes/14-group-rules-feasibility/`](spikes/14-group-rules-feasibility/README.md)):
  which group behaviours are feasible with the pagination library and pre-split
  rows in grid markup.
- PDF reader agreement
  ([`spikes/15-pdf-reader-agreement/`](spikes/15-pdf-reader-agreement/README.md)):
  whether a PDF text-and-position reader with a shippable licence agrees with
  the accounting path well enough for the product to ship it.

## [0.1.0] - 2026-10-22

First release. It completes the groundwork milestone of the roadmap
(Stage 0): the repository layout and shared build conventions, a local
development composition started with one command, seeded pilot datasets
with sample templates, and five measured technical spikes. No report is
rendered yet: the API answers health checks only and the render worker only
checks that Chromium starts. The spikes record measurements, not decisions;
the remaining library choices are made from them in the next versions.

### Added

- Repository layout: `src/`, `tests/`, `samples/`, `docs/`, `eng/` and
  `spikes/`, each with a README describing its purpose and naming rules.
- Shared build configuration: nullable reference types, implicit usings,
  warnings as errors, code style enforced in build, deterministic builds.
- Central package management and a package source restricted to nuget.org
  with package source mapping.
- Platform matrix: .NET 10 SDK pinned to the 10.0.4xx band, `net10.0` and
  analyzer level 10.0 as shared defaults.
- Product versioning: one Semantic Versioning version for the whole
  repository, a `-dev` pre-release label on builds between releases, and
  this changelog.
- README describing the product design, and `docs/repository-layout.md`.
- Local development composition started with one command
  (`dotnet run --project src/ReportService.AppHost`): an API stub, PostgreSQL
  18, Valkey 9.1, the Temporal development server and a render worker
  container, each with a health check; the render worker image is built by
  the .NET SDK on a base image that holds only the Chromium headless shell.
- PDF base image definition (`eng/pdf-base/`) and a local development
  registry, bound to the loopback address, used by the local composition.
- Seeded pilot dataset generator (simple, grouped and high-volume datasets in
  the `header-items-v1` envelope) with a `verify` command and the expected
  hashes in `samples/datasets/SHA256SUMS`.
- Three sample template packages, one per pilot dataset.
- Reference environment description for published measurements
  (`docs/reference-environment.md`).

#### Measured spikes

Throwaway evaluation programs under `spikes/`, kept for their measurements.
They are not part of the product: they are not in the solution and are
never packaged or shipped. Each README states the hypothesis, how to run
the spike, the measured results and the conclusions.

- Streaming data and template engines
  ([`spikes/01-streaming-data/`](spikes/01-streaming-data/README.md)):
  incremental reading of the record envelope, plain and gzip, with every
  data limit failing with a stable, named error, and per-record rendering
  through Scriban, Handlebars and Liquid with one render context and one
  escaping policy; measures memory, the byte identity of the three engines'
  HTML and the cost of each engine.
- PDF layout ([`spikes/02-pdf-layout/`](spikes/02-pdf-layout/README.md)):
  page layout of the grouped pilot dataset with Paged.js in headless
  Chromium on the PDF base image; checks page subtotals against the data
  and measures memory and time per block size, the handling of one very
  tall row, and Chromium's native page margin boxes as an alternative.
- PDF assembly ([`spikes/03-pdf-assembly/`](spikes/03-pdf-assembly/README.md)):
  ordered merge of hundreds of partial PDFs and global "Page X of Y"
  numbering with PDFsharp and qpdf at 1 000, 5 000 and 10 000 pages;
  measures memory, time, temporary disk and output size, and checks the
  structure, page order, numbering and pixels of every output.
- XLSX writer ([`spikes/04-xlsx-writer/`](spikes/04-xlsx-writer/README.md)):
  1 000 000 rows written with MiniExcel and with the Open XML SDK writer;
  measures memory and checks exact per-column typing, formula-like text kept
  as text, the sheet row limit, and that the files open without repair.
- Markdown conversion ([`spikes/05-markdown/`](spikes/05-markdown/README.md)):
  four routes from report HTML to Markdown — a library per fragment, a
  streaming converter, Markdown template parts, and a hybrid by size;
  measures table continuity across fragments, memory, and what each route
  loses against the HTML output.

[Unreleased]: https://github.com/EugenioSerrano/dotnet-report-service/compare/v0.9.0...develop
[0.9.0]: https://github.com/EugenioSerrano/dotnet-report-service/compare/v0.1.0...v0.9.0
[0.1.0]: https://github.com/EugenioSerrano/dotnet-report-service/releases/tag/v0.1.0
