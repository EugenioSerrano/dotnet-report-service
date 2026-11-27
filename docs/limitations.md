# Current limitations

What this service version does not do, why, how to recognise it and what removes it.
Each entry states the condition under which it goes, so a limitation outlives neither
its reason nor the release that ends it.

## The size of a PDF report

A PDF report is printed from one or more blocks. The only bound on its size is the
document capacity the deployment configures - pages and bytes of rendered parts,
whichever is reached first. The service imposes no ceiling of its own; when a
deployment configures none, the example values apply: **10 000 pages** or
**37 748 736 bytes**. A report within the configured capacity is printed and published
whole; a report whose assembled document would exceed it ends `failed` with
`OUTPUT_LIMIT_EXCEEDED`, before anything is published. The stored diagnostic names the
limit that was reached, its configured value and the value the report reached - never a
value of the report. [Document capacity](document-capacity.md) describes the settings,
the measured figures and what to do above the capacity.

**A job of about 50 000 pages does not complete end to end in this version.** With the
default rendering window, the workflow history of a job at that block count passes its
bound - a defect being corrected. The published capacity figures measure one document's
assembly; they do not establish that a job of that size completes. This limitation goes
when the defect is corrected and a job of that size is shown to complete.

Nothing else refuses a PDF report for its size: the block limits split a large report
into more blocks, and a report that fits one block is printed exactly as it is.
