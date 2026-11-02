-- Report jobs, their requested outputs and their template references, and the
-- intent kind that starts a job.
CREATE TABLE report_service.report_jobs (
    report_job_id  text        PRIMARY KEY CHECK (report_job_id ~ '^job-[0-9a-f]{32}$'),
    consumer_id    text        NOT NULL CHECK (consumer_id ~ '^[A-Za-z0-9._-]{1,128}$'),
    upload_id      text        NOT NULL REFERENCES report_service.uploads (upload_id),
    status         text        NOT NULL CHECK (status IN ('QUEUED', 'VALIDATING', 'PARTITIONING',
                                   'RUNNING', 'FINALIZING', 'SUCCEEDED', 'FAILED', 'CANCELLING', 'CANCELLED')),
    stage          text        NULL,
    report_clock   timestamptz NOT NULL,
    error_code     text        NULL,
    error_reason   text        NULL,
    created_at     timestamptz NOT NULL,
    started_at     timestamptz NULL,
    finished_at    timestamptz NULL,
    CHECK ((status IN ('SUCCEEDED', 'FAILED', 'CANCELLED')) = (finished_at IS NOT NULL)),
    CHECK ((status = 'FAILED') = (error_code IS NOT NULL))
);

CREATE INDEX report_jobs_by_upload ON report_service.report_jobs (upload_id);

CREATE TABLE report_service.report_outputs (
    output_id      text        PRIMARY KEY CHECK (output_id ~ '^output-[0-9a-f]{32}$'),
    report_job_id  text        NOT NULL REFERENCES report_service.report_jobs (report_job_id),
    format         text        NOT NULL CHECK (format IN ('PDF', 'HTML', 'XLSX', 'MARKDOWN')),
    status         text        NOT NULL CHECK (status IN ('PENDING', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED')),
    error_code     text        NULL,
    started_at     timestamptz NULL,
    finished_at    timestamptz NULL,
    UNIQUE (report_job_id, format),
    CHECK ((status = 'FAILED') = (error_code IS NOT NULL))
);

CREATE TABLE report_service.template_references (
    report_job_id     text        PRIMARY KEY REFERENCES report_service.report_jobs (report_job_id),
    template_id       text        NOT NULL CHECK (template_id ~ '^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$'),
    reference_form    text        NOT NULL CHECK (reference_form IN ('EXACT', 'BY_NAME')),
    requested_version text        NULL CHECK (requested_version ~ '^[!-~]{1,128}$'),
    CHECK ((reference_form = 'EXACT') = (requested_version IS NOT NULL))
);

-- A job is started by the dispatcher, which now serves two kinds of request.
ALTER TABLE report_service.intents
    DROP CONSTRAINT intents_kind_check,
    ADD CONSTRAINT intents_kind_check CHECK (kind IN ('start-upload-verification', 'start-job'));
