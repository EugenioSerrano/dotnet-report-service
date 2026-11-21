-- Upload transfer and outcome, the immutable snapshot of a verified upload,
-- and the durable requests that start background work.
ALTER TABLE report_service.uploads
    ADD COLUMN content_encoding   text        NULL
        CHECK (content_encoding IN ('none', 'gzip', 'other')),
    ADD COLUMN content_outcome    text        NULL
        CHECK (content_outcome IN ('complete', 'interrupted')),
    ADD COLUMN received_bytes     bigint      NULL CHECK (received_bytes >= 0),
    ADD COLUMN staging_size_bytes bigint      NULL CHECK (staging_size_bytes >= 0),
    ADD COLUMN staging_sha256     text        NULL CHECK (staging_sha256 ~ '^[0-9a-f]{64}$'),
    ADD COLUMN declared_sha256    text        NULL CHECK (declared_sha256 ~ '^[0-9a-f]{64}$'),
    ADD COLUMN completed_at       timestamptz NULL,
    ADD COLUMN rejection_code     text        NULL
        CHECK (rejection_code IN ('DATA_INVALID', 'QUOTA_EXCEEDED', 'UNSUPPORTED_CAPABILITY')),
    ADD COLUMN rejection_reason   text        NULL
        CHECK (rejection_reason IN ('sha256-mismatch', 'truncated', 'malformed', 'modified',
                                    'encoding', 'stored-bytes', 'input-bytes')),
    ADD CONSTRAINT uploads_rejection_iff_rejected
        CHECK ((status = 'REJECTED') = (rejection_code IS NOT NULL AND rejection_reason IS NOT NULL)),
    ADD CONSTRAINT uploads_completed_when_confirmed
        CHECK (status NOT IN ('VERIFYING', 'READY') OR completed_at IS NOT NULL);

-- One snapshot per upload: the unique key is the commit authority.
CREATE TABLE report_service.snapshots (
    upload_id                  text        PRIMARY KEY
                                           REFERENCES report_service.uploads (upload_id),
    consumer_id                text        NOT NULL,
    object_key                 text        NOT NULL UNIQUE,
    compression                text        NOT NULL CHECK (compression IN ('none', 'gzip')),
    profile                    text        NOT NULL CHECK (profile IN ('HEADER_ITEMS_V1')),
    sha256_uncompressed        text        NOT NULL CHECK (sha256_uncompressed ~ '^[0-9a-f]{64}$'),
    size_uncompressed_bytes    bigint      NOT NULL CHECK (size_uncompressed_bytes >= 0),
    sha256_stored              text        NOT NULL CHECK (sha256_stored ~ '^[0-9a-f]{64}$'),
    size_stored_bytes          bigint      NOT NULL CHECK (size_stored_bytes >= 0),
    created_at                 timestamptz NOT NULL
);

CREATE TABLE report_service.intents (
    intent_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind             text        NOT NULL CHECK (kind IN ('start-upload-verification')),
    subject_id       text        NOT NULL,
    status           text        NOT NULL CHECK (status IN ('PENDING', 'DISPATCHED')),
    attempts         integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    created_at       timestamptz NOT NULL,
    next_attempt_at  timestamptz NOT NULL,
    last_attempt_at  timestamptz NULL,
    dispatched_at    timestamptz NULL,
    UNIQUE (kind, subject_id),
    CHECK ((status = 'DISPATCHED') = (dispatched_at IS NOT NULL))
);

CREATE INDEX intents_pending_due
    ON report_service.intents (next_attempt_at, intent_id)
    WHERE status = 'PENDING';

CREATE TABLE report_service.execution_references (
    intent_id     bigint      PRIMARY KEY REFERENCES report_service.intents (intent_id),
    workflow_id   text        NOT NULL,
    run_id        text        NOT NULL,
    worker_build  text        NOT NULL,
    retry_policy  text        NOT NULL,
    recorded_at   timestamptz NOT NULL
);
