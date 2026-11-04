-- Job progress counters and the job's current stage.
ALTER TABLE report_service.report_jobs
    ADD COLUMN records_read     bigint  NOT NULL DEFAULT 0 CHECK (records_read >= 0),
    ADD COLUMN records_total    bigint  NULL               CHECK (records_total >= 0),
    ADD COLUMN blocks_completed integer NOT NULL DEFAULT 0 CHECK (blocks_completed >= 0),
    ADD COLUMN blocks_total     integer NULL               CHECK (blocks_total >= 0),
    ADD COLUMN pages_generated  bigint  NOT NULL DEFAULT 0 CHECK (pages_generated >= 0),
    ADD CONSTRAINT report_jobs_stage_check CHECK (stage IS NULL OR stage IN (
        'WAITING_FOR_INPUT', 'RESOLVING', 'VALIDATING_DATA', 'PARTITIONING', 'COMPOSING_HTML',
        'RENDERING_PDF', 'MERGING_PDF', 'EXPORTING_XLSX', 'CONVERTING_MARKDOWN', 'PUBLISHING', 'NOTIFYING')),
    ADD CONSTRAINT report_jobs_records_within_total CHECK (records_total IS NULL OR records_read <= records_total),
    ADD CONSTRAINT report_jobs_blocks_within_total  CHECK (blocks_total IS NULL OR blocks_completed <= blocks_total);
