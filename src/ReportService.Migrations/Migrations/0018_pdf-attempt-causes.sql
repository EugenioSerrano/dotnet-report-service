-- The cause a rendering attempt recorded before it ended, keyed as the
-- orchestration decision states it: the output, the block and the attempt. Two
-- deadline classes only - the watchdog's and the composition guard's - so the row
-- can never carry a value, a message, a path or a storage key.
--
-- The rows are kept with the job's own metadata: the retention cleanup of a later
-- version removes them with the job they belong to, and nothing of a job outlives
-- its own records. At most one row per attempt, and the PDF retry class gives at
-- most three attempts per block. Additive and forward-only; no row is rewritten.

CREATE TABLE report_service.pdf_attempt_causes (
    output_id    text        NOT NULL REFERENCES report_service.report_outputs (output_id),
    block_index  integer     NOT NULL CHECK (block_index >= 0),
    attempt      integer     NOT NULL CHECK (attempt >= 1),
    cause        text        NOT NULL CHECK (cause IN ('watchdog-deadline', 'guard-deadline')),
    recorded_at  timestamptz NOT NULL,
    PRIMARY KEY (output_id, block_index, attempt)
);
