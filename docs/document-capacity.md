# Document capacity

How large one PDF document may be, who decides it, how it is measured and what
happens above it. The short version: **the service imposes no page ceiling of its
own**. The size of one document is bounded by the capacity the deployment
configures, which the deployment measures on its own infrastructure; what the
service ships is an example starting point.

## What bounds a document

A PDF report is printed in blocks and, when it has more than one block, assembled
into one document by the merge worker. Two sums decide whether a document is
admitted, checked as each block's part is committed and again before assembly:

| Dimension | What is summed | Setting |
|-----------|----------------|---------|
| Pages | the pages of the committed parts | `DocumentCapacity:MaximumPages` |
| Parts bytes | the bytes of the committed parts | `DocumentCapacity:MaximumPartBytes` |

The first commit that would push either sum past its configured value fails the
PDF output with `OUTPUT_LIMIT_EXCEEDED`, non-retryable, before anything is
published. The stored diagnostic names the dimension, its **configured** value and
the value the report reached - never a value of the report:

```text
limit=document-pages limitValue=50000 reached=50184
```

Pages are judged before bytes when one commit passes both.

The other figures of an assembly - its peak memory, its time per stage, its
temporary disk and the size of the output - are **budget** dimensions: they are not
checked before assembly; they are recorded for every assembled document (see
[observability](observability.md#pdf-assembly)) and they are what a deployment sizes
its merge worker from. The one budget the service enforces is the assembly's
temporary disk: three times the parts' bytes, plus 16 KiB per page when the template
declares a per-page code (the most one stored code may weigh). Passing it fails the
output with `QUOTA_EXCEEDED`.

## Configuring the capacity

| Setting | Meaning | When absent |
|---------|---------|-------------|
| `DocumentCapacity:MaximumPages` | Most pages one PDF document may reach (> 0) | 10 000 |
| `DocumentCapacity:MaximumPartBytes` | Most bytes of rendered parts one PDF document may reach (> 0) | 37 748 736 |
| `DocumentCapacity:ProfileName` | The deployment's name for this profile: 1 to 64 printable ASCII characters, no spaces. Required as soon as either value is set | `example-reference-10k` |

Give the same values to **every** process: the PDF worker judges each part as it is
committed, the merge worker re-checks before it assembles, and the API and the
orchestration worker build the same job records. The local composition forwards
the three settings to all four when its own configuration states them, and gives
none by default. A value that is not valid stops the process at start, naming the
setting and never its value.

**The values the service ships are an example, not a rule.** They carry the last
published reference measurement (10 000 pages of ordinary rows, without per-page
codes). A deployment replaces them with what it has
measured on its own infrastructure; a smaller value is admissible for a smaller
deployment, a larger one is the deployment's own measured capacity. Nothing in the
service checks a configured value against a published figure.

The profile name is written to the merge worker's log on every assembly and every
refusal for capacity; the configured values are also recorded as metrics. The
profile name is never a metric tag.

## Above the configured capacity

In this order:

1. **A larger vertical resource.** One document's assembly grows with its page
   count, mostly in memory (see the figures below). Give the merge worker more
   memory or faster temporary disk, measure again, and raise the configured
   capacity to the new measurement. No product change is needed.
2. **Split the batch into several documents.** The service does not split a
   document for you, and never does so silently. Splitting one job's batch into
   several documents, each numbered on its own, is a capability under evaluation,
   not delivered; until then a consumer that needs more than one deployment's
   single-document capacity submits several jobs.
3. **The explicit refusal** described above, when neither is applied.

## Size and concurrency are separate

Scaling has two independent axes:

- **Document size** is a single document's assembly: its block count and the merge
  worker's **vertical** memory and disk. It is configured here and answered by a
  larger vertical resource.
- **Concurrency** is how much work runs at once. Work beyond the deployment's
  capacity **queues and is never refused**; more **horizontal** replicas make it
  finish sooner.

A larger document never calls for more replicas, and more replicas never raise a
single document's capacity. Configure the two independently.

## Measured figures

The figures below are one document's assembly, measured in process on the
[reference environment](reference-environment.md), every page carrying its global
number and a per-page code drawn by the service's reference code generator.

**They are a lower bound.** The pages measured are synthetic: each carries one short
line of text written by the PDF library, one font and no row that spans pages. A
page a real template prints carries more text, more fonts and its own layout, so a
document of the same page count printed from a real template needs **at least**
these resources and probably more. Size a deployment with a margin above them, or
measure your own templates.

**What they do not claim.** They measure the assembly of one document, not a job.
A job of about 50 000 pages does not complete end to end in this version: with the
default rendering window, the job's workflow history passes its bound at that block
count, a defect being corrected. Until it is corrected, these figures establish
what one document's assembly needs; they do not establish that a job of that size
completes.

| Figure | 10 000 pages, with codes | 50 000 pages, with codes |
|--------|--------------------------|--------------------------|
| Parts / parts bytes | pending measurement | pending measurement |
| Peak working set of the assembly | pending measurement | pending measurement |
| Peak managed heap | pending measurement | pending measurement |
| Stamping and merge time | pending measurement | pending measurement |
| Code generation time | pending measurement | pending measurement |
| Verification time | pending measurement | pending measurement |
| Temporary-disk peak / quota | pending measurement | pending measurement |
| Output size | pending measurement | pending measurement |
| Fits the local composition's 512 MiB merge worker | pending measurement | pending measurement |

Memory growth between the two points, per 1 000 pages: pending measurement (the
assembly of ordinary rows without codes was last measured at about 10 MiB per
1 000 pages).

The merge worker of the local composition runs with a 512 MiB memory limit. That
limit is a deployment parameter of the composition, not a product rule: a
deployment that admits larger documents gives its merge worker the memory its own
measurement shows.

## Measuring your own deployment

The measurement is the `performance` test level, run alone on the machine:

```bash
dotnet run --project eng/test/ReportService.Eng.TestRunner -- performance --timeout <mm:ss>
```

`DocumentCapacityProvingGroundTests` assembles 10 000 and 50 000 pages with codes
and writes its figures to the run's evidence folder
(`capacity-reference-10k.json`, `capacity-proving-ground-50k.json` and a one-line
summary). To measure what your own templates print, run a job of your largest
expected document on your own deployment and read the assembly metrics of
[observability](observability.md#pdf-assembly).
