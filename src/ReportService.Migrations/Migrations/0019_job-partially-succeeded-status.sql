-- Widen the report job status set to admit the ADR-048 terminal-status rollup's
-- PARTIALLY_SUCCEEDED state (US-008.BOLT-002, SPEC-261125-1224 Phase D.5). A job
-- whose requested outputs are a mix of succeeded and not-succeeded ends in this
-- terminal state, carrying no job-level error code.
--
-- Additive and forward-only: it only re-adds two CHECK constraints over a wider
-- allowed set and rewrites no row. The shipped 0005_report-jobs.sql is never edited.
-- The (status = 'FAILED') = (error_code IS NOT NULL) CHECK needs no change: a
-- PARTIALLY_SUCCEEDED job has status != 'FAILED' and error_code IS NULL, which it
-- already satisfies.

ALTER TABLE report_service.report_jobs
    DROP CONSTRAINT report_jobs_status_check;

ALTER TABLE report_service.report_jobs
    ADD CONSTRAINT report_jobs_status_check CHECK (status IN ('QUEUED', 'VALIDATING', 'PARTITIONING',
        'RUNNING', 'FINALIZING', 'SUCCEEDED', 'FAILED', 'CANCELLING', 'CANCELLED', 'PARTIALLY_SUCCEEDED'));

ALTER TABLE report_service.report_jobs
    DROP CONSTRAINT report_jobs_check;

ALTER TABLE report_service.report_jobs
    ADD CONSTRAINT report_jobs_check
        CHECK ((status IN ('SUCCEEDED', 'FAILED', 'CANCELLED', 'PARTIALLY_SUCCEEDED')) = (finished_at IS NOT NULL));
