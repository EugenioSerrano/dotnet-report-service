# Printing a report: retries, restarts and failed attempts

This page is for the operator of the reporting service and for the consumer who
reads a report's outcome. It states what happens while a report is printed, what
a restart does, and what a failed attempt means.

## The attempt budget

One block of a report is printed by one step, and that step may be attempted
**three times**. The service's own clock and a single render slot are shared by
every job, so an attempt that neither finishes nor fails is the one case the
budget cannot absorb on its own; the render watchdog is what ends it.

Between attempts the service waits a short, growing interval. A failure that is
the data's or the template's is **not** retried: the same input reaches the same
verdict, so it ends the block on its first attempt with its own code.

## What a restart does to a print

Nothing about a print is checkpointed on the worker. An attempt holds one block
in memory, writes the block's own object under a key that names the attempt, and
then records a single part with the page count and the object's SHA-256. When a
worker stops in the middle of a print:

- the engine delivers the attempt again, on a healthy worker;
- the new attempt looks for a part it may already have committed and returns it
  instead of printing again, so a killed attempt can cost a render but never a
  second document;
- the job's result is published once, by a guarded insert, so a restart cannot
  produce a second artifact.

A consumer sees the same job, the same single report, and no duplicated or
missing pages. Nothing is listed while the job is still printing.

## When every attempt fails

When the last allowed attempt fails for a reason that is not the data or the
template, the output and the job end **failed**:

| The last attempt ended because | The code the consumer reads |
|-------------------------------|-----------------------------|
| the composition passed its own deadline | `RENDER_TIMEOUT` |
| the render neither wrote nor returned and the watchdog stopped it | `RENDER_TIMEOUT` |
| the worker was lost, the process crashed, storage or the database failed | `EXECUTION_FAILED` |

No report is published and nothing is listed for download. The message the
consumer reads is a fixed sentence for the code; it never carries a value, a
path, a key or an exception text. The reason stored beside the code names the
cause the last attempt recorded, for example `exhausted cause=guard-deadline`.

## The watchdog: why a worker disappears for a moment

A render that neither writes nor returns would hold the service's only render
slot until the engine's own deadline. The worker therefore runs a watchdog: an
attempt registers itself when it starts, and when it is still registered thirty
seconds after its own start-to-close deadline, the watchdog

1. records `watchdog-deadline` for that output, block and attempt;
2. reports the worker **unhealthy** on its health endpoint;
3. stops the attempt's heartbeats;
4. ends the process.

The platform starts the worker again, the engine sees the lost attempt and
retries it, and the retry prints the block. That is why a worker can disappear
for a moment and a job can still finish successfully.

## The attempt-cause record

Each attempt that ends on a deadline leaves one small row: the output, the
block, the attempt number and one of two tokens (`guard-deadline` or
`watchdog-deadline`). It carries no value, no message, no path and no key.

The record is **kept with the job's own metadata** and is removed by the same
retention that removes the job's rows. It is **not a status**: the output's and
the job's status is what a consumer reads, and the record only explains which
deadline ended the last attempt.

## A deployment must replace an exited worker

The watchdog ends its own process on purpose. A deployment that does not replace
an exited container therefore turns one lost attempt into a stopped service.
The local composition declares the render worker with `--restart=always`; a real
deployment must declare an equivalent restart policy for the render worker. The
policy is visible to the runtime (`docker inspect` reports it), and it is a
prerequisite of this page, not an optimization.
