# Current limitations

What this service version does not do, why, how to recognise it and what removes it.
Each entry states the condition under which it goes, so a limitation outlives neither
its reason nor the release that ends it.

## A PDF report must fit in one block

This version prints a PDF report from **one block**. A job whose report does not fit
is refused **before anything is printed**: the job and its PDF output end `failed`
with `QUOTA_EXCEEDED`, and the status read's `errorMessage` says which limit the data
exceeded.

### What the limit is

Two numbers bound one block, and the smaller one is the limit that applies:

| Bound | Value | Where it comes from |
|-------|-------|---------------------|
| Records | the configured record count, `1000` by default | `Partitioning:SingleBlockRecordLimit` of the orchestration worker |
| Record data | 2 000 records or 1 048 576 bytes, whichever comes first | the block's own maximum, which the configuration cannot raise |

A configured value above 2 000 is accepted and has no effect: the worker logs once, at
start-up, that the setting is above the maximum, and the jobs run under 2 000. The
operator, not the producer, reads that line.

### Why it exists

The print path lays out one HTML document into one PDF, and beyond the limits above
the layout stops being reliable: a row whose text cannot be placed, a page that is
never signalled, a table that loses rows. The limits are the boundary of what the
layout has been measured to print whole. A report inside the boundary is printed
exactly and verified after rendering; a report outside it is refused, so nothing
partial or silently truncated is ever delivered.

### How to recognise it

- **The code.** `errorCode: "QUOTA_EXCEEDED"` and, on the report's PDF output, the
  same code.
- **The message.** A `200 OK` status read carries `errorMessage` in plain words, for
  example:

  > The PDF report was not printed: record 1001 exceeds this version's limit of 1000
  > records per report (one block). Send a smaller report.

  The number it names is the limit that was **applied** to the data - the configured
  value, or the block's maximum when the configuration is above it - and the record it
  names is the position at which the pass stopped. The number the operator configured
  never appears in a message.
- **What was written.** Nothing: no page was printed, no file was published and no
  artifact is listed. The report is not partly available.
- **What was not done.** The refusal is not retried: the pass stops at the first record
  over the limit, so a second job over the same data fails in the same place.

### When it goes, and what that release must do

The limit disappears in the first release that can print a PDF report from **more than
one block** - that is, the release that partitions a report into several blocks and
merges them in order. Removing the refusal is part of that change, not a follow-up:
a version that can print two blocks and still refuses at 1 000 records would refuse
reports it can print.

The same change must:

1. remove the `Partitioning:SingleBlockRecordLimit` setting and its validation, and the
   start-up line that reports it;
2. remove the admission rule from the data pass and the refusal it raises;
3. delete the test that asserts this version's one-block admission and its removal
   instruction, which turns red the moment a second block becomes possible;
4. delete this entry, and say in `CHANGELOG.md` that the limit was removed.

No message promises a date. The consumer-facing text names the limit that applied and
what to do about it; when a release removes the limit, the message stops existing with
it.
