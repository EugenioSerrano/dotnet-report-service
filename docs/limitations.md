# Current limitations

What this service version does not do, why, how to recognise it and what removes it.
Each entry states the condition under which it goes, so a limitation outlives neither
its reason nor the release that ends it.

## The size of a PDF report

A PDF report is printed from one or more blocks. The only bound on its size is the
published document limit: **10 000 pages**, or **37 748 736 bytes** of rendered parts,
whichever is reached first. A report within the limit is printed and published whole; a
report whose assembled document would exceed it ends `failed` with
`OUTPUT_LIMIT_EXCEEDED`, before anything is published. The stored diagnostic names the
limit that was reached, the limit's value and the value the report reached - never a
value of the report.

Nothing else refuses a PDF report for its size: the block limits split a large report
into more blocks, and a report that fits one block is printed exactly as it is.
