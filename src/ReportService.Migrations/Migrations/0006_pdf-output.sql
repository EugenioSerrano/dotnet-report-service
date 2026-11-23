-- The data side of a printed report: the one partition of a job, the commit of
-- each rendered block and the artifacts the service publishes. Additive and
-- forward-only; expiry, download names and retention belong to later scripts.

-- One partition record per job: the single block and its index page.
CREATE TABLE report_service.partitions (
    report_job_id        text        PRIMARY KEY REFERENCES report_service.report_jobs (report_job_id),
    partitioner_version  integer     NOT NULL CHECK (partitioner_version >= 1),
    block_count          integer     NOT NULL CHECK (block_count >= 1),
    record_count         bigint      NOT NULL CHECK (record_count >= 0),
    record_bytes         bigint      NOT NULL CHECK (record_bytes >= 0),
    index_object_key     text        NOT NULL UNIQUE,
    index_sha256         text        NOT NULL CHECK (index_sha256 ~ '^[0-9a-f]{64}$'),
    index_size_bytes     bigint      NOT NULL CHECK (index_size_bytes > 0),
    committed_at         timestamptz NOT NULL
);

-- One commit per rendered block of an output, written once.
CREATE TABLE report_service.pdf_part_commits (
    output_id                text        NOT NULL REFERENCES report_service.report_outputs (output_id),
    block_index              integer     NOT NULL CHECK (block_index >= 0),
    object_key               text        NOT NULL UNIQUE,
    sha256                   text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes               bigint      NOT NULL CHECK (size_bytes > 0),
    page_count               integer     NOT NULL CHECK (page_count >= 1),
    template_package_sha256  text        NOT NULL CHECK (template_package_sha256 ~ '^[0-9a-f]{64}$'),
    committed_at             timestamptz NOT NULL,
    PRIMARY KEY (output_id, block_index)
);

-- Publication: one artifact per output, naming an output-class object only.
CREATE TABLE report_service.artifacts (
    artifact_id    text        PRIMARY KEY CHECK (artifact_id ~ '^artifact-[0-9a-f]{32}$'),
    report_job_id  text        NOT NULL REFERENCES report_service.report_jobs (report_job_id),
    output_id      text        NOT NULL UNIQUE REFERENCES report_service.report_outputs (output_id),
    format         text        NOT NULL CHECK (format IN ('PDF', 'HTML', 'XLSX', 'MARKDOWN')),
    media_type     text        NOT NULL,
    object_key     text        NOT NULL UNIQUE CHECK (object_key LIKE 'outputs/%'),
    sha256         text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes     bigint      NOT NULL CHECK (size_bytes > 0),
    page_count     integer     NULL CHECK (page_count >= 1),
    created_at     timestamptz NOT NULL
);

CREATE INDEX artifacts_by_job ON report_service.artifacts (report_job_id);
