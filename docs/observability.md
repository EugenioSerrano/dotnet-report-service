# Observability

Every process of the service — the API, the orchestration worker and the PDF
worker — reports structured logs, traces and metrics, and reports them the same
way, so a change only adds the signals of the work it delivers. This page says
what each process emits, what it never emits, how the export destination is
configured, and how a later change adds a signal.

## What each process reports

| Process | `service.name` | Signals |
|---------|----------------|---------|
| API | `report-service-api` | logs, traces, metrics |
| Orchestration worker | `report-service-orchestration-worker` | logs, traces, metrics |
| PDF worker | `report-service-render-worker` | logs, traces, metrics |

Every signal of a process carries the same resource attributes:

| Attribute | Value |
|-----------|-------|
| `service.name` | the name in the table above |
| `service.version` | the build's informational version — `0.2.0-dev+<commit>` between releases |
| `service.instance.id` | a random identifier created when the process starts |
| `deployment.environment` | the process's environment name (`Development` in the local composition) |

### Logs

Logs are structured and carry their scopes, so a value written as a named
placeholder stays a field and a scope's fields travel with every entry written
inside it. Each process writes one start-up line:

```text
service=report-service-api version=0.2.0-dev+<commit> runtime=10.0.14 commit=<commit> started
```

The PDF worker writes its browser line beside it:

```text
Chromium 153.0.8010.12 ready (Playwright 1.63.0, .NET 10.0.14)
```

### Traces

Incoming HTTP requests and outgoing HTTP calls are traced. A request to
`/health` is not: health checks run every few seconds and would drown the useful
traces. The service's own spans are started on the `ReportService` activity
source, so they belong to the same trace as the request or the call that caused
them, and the trace context is carried from one process to the next.

### Metrics

Metrics come from the ASP.NET Core, HTTP client and runtime instrumentation,
plus the service's own meter `ReportService`, whose only instrument today is the
gauge `service.build_info` (value 1, tags `version` and `runtime`). That gauge is
how a metrics backend learns which build a process runs.

## The job identifier: logs and spans, never a metric

A unit of work is opened with a job identifier:

```csharp
using (JobContext.Begin(jobId))
{
    // every log entry written here carries the scope field job_id
    // every span started here carries the tag job.id
}
```

- The identifier is an internal identifier, not free text: it must match
  `[a-z0-9._-]{1,128}`, and opening a unit of work with anything else throws.
- It is **never** a metric dimension. Metric labels that vary per job make a
  backend's cardinality grow with the number of jobs, which is why the rule is
  structural rather than a convention: the service's instruments take constant
  tags, the instrumentation that adds the identifier to spans sees spans only,
  and a test enumerates the meter's instruments and asserts that none declares a
  job tag.
- Nesting a unit of work restores the previous identifier when the inner one
  ends.

## What never appears in telemetry

Before anything leaves a process, the log and span pipelines pass through the
redaction rules:

- every configured sensitive value (`Telemetry:Redaction:Values`, a list, empty
  by default) is replaced with `[redacted]`;
- the storage root path (`Storage:Filesystem:Root`) is replaced with
  `[redacted]`, so a path written by mistake does not reveal the layout of the
  machine;
- a log attribute whose name matches
  `password|secret|token|credential|connectionstring|apikey|accesskey`
  (case-insensitive) is dropped from the record;
- a value longer than 4 096 characters is truncated to its first 1 024
  characters followed by `[truncated]` — report content never travels whole.

Two places the rules cannot reach, and the rule that stands in for them:

- **A span's tag** cannot be removed once it is set; a tag whose name matches
  the list above is kept with the value `[redacted]`, which is what matters —
  the value does not travel.
- **A span event's tags** and **a metric's tags** cannot be changed by anything
  in the pipeline: the framework keeps them in structures the pipeline can only
  read, and the metrics pipeline has no processor at all. The rule for the code
  that writes them is therefore the same as everywhere else: a secret, a
  credential, a path or report content is never put in an event tag or in a
  metric tag, and a metric tag never varies per job.

## Where telemetry goes: configuration, and nothing when it is not configured

The destination is an address, never a secret:

| Variable / setting | Meaning |
|--------------------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | the standard variable every OpenTelemetry process reads |
| `Telemetry:OtlpEndpoint` | the same address through the service's configuration, for a deployment that sets it that way |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (the default) or `http/protobuf` |

With neither address set, a process adds **no destination exporter**: it starts,
logs and measures, and attempts no export at all. With an address set, all three
signals are exported over OTLP to it.

The local composition points the three processes at its own telemetry dashboard.
A container does not inherit the AppHost's telemetry address, so the composition
gives the PDF worker an address a container can reach — this machine, not its
loopback — and sets it before the container is created.

## Development-only diagnostics

Three endpoints exist to show what the processes emit, and only while
`Telemetry:Diagnostics:Enabled` is true. The development and test compositions
set it; a deployed configuration leaves it false, and nothing else turns it on:

| Endpoint | What it answers |
|----------|-----------------|
| `GET /diagnostics/telemetry/metrics` | the metric points the process's pipeline exported, with its resource attributes, the capture's export interval and how many points were omitted |
| `POST /diagnostics/telemetry/unit-of-work` | runs one unit of work under a synthetic job identifier and answers with its trace and span identifiers |
| `GET /diagnostics/telemetry/trace` | the trace identifier of the request's own span |
| `GET /diagnostics/telemetry/propagate` (API only) | calls the orchestration worker and reports whether both spans share one trace |

The metric capture behind the first endpoint is bounded: it retains at most
2 000 points (the oldest are dropped first) and returns at most 512 KiB, with
the omitted count in the body. It is not a destination and it is not an
exporter's replacement — it reads the points the pipeline was handed, so a run
with no destination still has points to show.

## Adding a signal for new work

1. **A metric:** create the instrument on the `ReportService` meter through the
   shared build-info type's meter, and take its tags from constants. Never take
   a tag from a job identifier or from anything that varies per job, and never
   from a setting value.
2. **A span:** start it on the `ReportService` activity source. The job tag and
   the redaction rules apply by themselves; nothing needs to be passed down.
3. **A log entry:** use the structured logging API with named placeholders — no
   string concatenation of settings, no report content, and nothing that the
   redaction rules would have to catch.
4. **A unit of work:** open it with `JobContext.Begin(jobId)` at the point where
   a job starts being processed, and dispose it when the unit ends.
5. Add the assertion to the matching test project: the hermetic suite for what
   can be checked in-process, the end-to-end suite for what needs the running
   composition.
