# Job priority and per-class capacity

A job is submitted at one of three priorities and its render and merge work is
dispatched by that priority's class. This page is for the people who deploy and
operate the service: what the levels do, how a waiting `low` job is promoted,
how a deployment provides capacity for each class, what to watch, and what
priority never changes. The request member and its refusals are described in
[`../api/report-jobs.md`](../api/report-jobs.md).

## The three levels

- A job is `low`, `normal` or `high`. A request that names no priority is
  `normal`; every job created before priorities existed is `normal`.
- The priority decides **which queued work runs next** for the job's PDF blocks,
  its assembly, and the fixing of its template version (the take or the by-name
  resolution). Every other step - validation, the data step, publication, the
  HTML output - runs on the queue it always ran on.
- Work that is already running is never interrupted. A `high` job waits at most
  for the activity already running on a process of its class (a block can take
  up to 300 s, an assembly up to 15 min) and for the `high` work queued before it.

## Aging: a waiting `low` job is promoted to `normal`

A `low` job whose render or merge work has been waiting for the deployment's
aging wait is promoted to `normal`, so it is not overtaken forever.

- **The setting.** `Priority:Aging:Wait` on the orchestration worker. The default
  is 10 minutes; a value below 5 seconds or above 7 days stops the worker at start
  naming the setting.
- **What is measured.** The job's *pending time*: while it has PDF blocks or an
  assembly that were offered to the `low` queue and have not started. The
  orchestration samples it every twentieth of the wait (between 5 and 60 seconds;
  30 seconds at the default), so the measure is a lower bound - a job is never
  promoted before it really waited the configured time. Time spent waiting for
  the job's input or for its template is not counted.
- **What moves - only what is dispatched after the promotion.** From the
  promotion on, every block the job sends and its assembly go to the `normal`
  queues. **Work already offered to the `low` queue - started or not - stays there
  and completes there**, under the usual retries. A job whose blocks were all
  offered before the promotion - a single-block job, or one in its last window -
  is still promoted and counted, but none of its work moves; its assembly, if it
  has one and it was not yet sent, goes to `normal`.
- **What the operator sees.** The status read answers `priority.declared`
  (`low`, never changed) and `priority.effective` (`normal` after the
  promotion); the orchestration worker writes one log line per promotion, with
  the job identifier and the pending time; the counter
  `reportservice.job.priority.promotions` rises by one per job, whatever the
  number of its outputs or of retried steps.
- **The cost.** A sampling `low` output adds about 8 workflow-history events per
  sample: about 960 an hour at the default, about 5 800 an hour at the 5-second
  floor. A run continues as new after 1 000 samples, as it does after 1 000
  blocks.
- **Aging moves depth between queues.** When a job is promoted, its later work
  arrives on the `normal` queues rather than the `low` ones, without new work
  being submitted. Read the effective class, not the submitted one, before
  reading a change of depth as a burst.

## Restricting `high`

A deployment may restrict `high` with `Jobs:Priority:High:Restriction` (an
allow-list of consumers, or `high` disabled). A request the restriction does not
admit is refused with `422 UNSUPPORTED_CAPABILITY`, before the job exists; the
effective class of an admitted job shows the class it is dispatched on. The
restriction is a policy over a declared consumer, not authentication. See
[`../api/report-jobs.md`](../api/report-jobs.md#the-priority-and-the-deployments-restriction-of-high).

## Capacity per class is dedicated, and its amount is the deployment's

Each class has its own render queue and its own merge queue:

| Kind | `high` | `normal` | `low` |
|------|--------|----------|-------|
| render (blocks, template take and resolution) | `report-pdf-rendering-high` | `report-pdf-rendering` | `report-pdf-rendering-low` |
| merge (assembly) | `report-pdf-merge-high` | `report-pdf-merge` | `report-pdf-merge-low` |

- **One class per process.** A render or merge worker serves exactly one class,
  named by `Priority:ServedClass` (`high`, `normal` or `low`); absent, it serves
  `normal`, so a deployment that sets nothing runs exactly as before. Any other
  value - a list of classes included - stops the process at start naming the
  setting. Each process still runs one activity at a time.
- **The amount is the deployment's.** How many processes serve each class is the
  deployment's choice, recorded with its configuration. The product ships no
  ratio and enforces none. **A deployment may scale a class to zero.** The work
  of a class that has no capacity at that moment **waits on its queue and is
  never refused**; it runs when a process of its class is there - and a `low`
  job's later work moves to `normal` once it is promoted. A class with no
  provisioned capacity gains no speed.
- **Isolation.** Because each process serves one class, a `low` flood cannot
  delay `high` work on the `high` processes. A process serving several classes
  is not offered: measurements on the reference engine showed that sharing one
  process's single activity slot between class queues could not keep the
  ordering and the progress bounds the contract needs.
- **The window against the replicas, per class.** The per-job block window
  (`Partitioning:PdfOutputWindow`) should be at least the number of render
  processes of the class a large job runs on; a window larger than a class's
  processes only queues the job's own blocks on its class queue.
- **Upgrade and rollback order.** Deploy the per-class render and merge workers
  first, then the orchestration worker; roll back in the opposite order.
  Otherwise `high` or `low` work is sent to a queue no process serves yet.

### Azure Container Apps

- One container app per kind and class: `render-high`, `render-normal`,
  `render-low`, and the same for merge, each with its `Priority:ServedClass`.
- Keep **at least one replica** of the `high` apps: a class that scales to zero
  pays a cold start on exactly the work that asked to go first.
- Each app's scaler reads `reportservice.taskqueue.pending` for its own queue.
  Every orchestration replica reports the same value, so aggregate the replicas
  with the **maximum**, never the sum.
- Azure Container Apps itself is not exercised by the product's test suite;
  validate the scaling rules in the deployment before relying on them.

## What to watch

| Instrument | Process | What it tells |
|------------|---------|---------------|
| `reportservice.taskqueue.pending` | orchestration worker | The work waiting on each class's render and merge queue (`task_queue`, `kind`, `priority_class`). |
| `reportservice.taskqueue.schedule_to_start.duration` | render and merge workers | How long each attempt waited on its class queue before a process took it. |
| `reportservice.job.priority.promotions` | orchestration worker | Jobs promoted by aging (`from`, `to`). |

No attribute of these instruments carries a job identifier.
`Telemetry:PendingWork:SampleInterval` (default 15 s, between 1 s and 5 min)
sets how often the pending work is read.

## What priority never changes

- The job's outcome: its terminal status, error codes and outputs are the same at
  every level.
- The document: block order and the assembled document are unchanged.
- The document-size capacity (`DocumentCapacity:*`): a document beyond it fails
  the same way at every level, and a `low` document inside it is admitted.
- Refusals: no job is refused because the service is busy, at any level.
- No latency, position or throughput is promised; the figures a deployment
  publishes are its own measurements.
