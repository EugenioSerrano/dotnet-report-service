-- Upload sessions: one row per upload a consumer opens. Content, snapshots and
-- verification state are added by later scripts.
CREATE TABLE report_service.uploads (
    upload_id         text        PRIMARY KEY
                                  CHECK (upload_id ~ '^upload-[0-9a-f]{32}$'),
    consumer_id       text        NOT NULL
                                  CHECK (consumer_id ~ '^[A-Za-z0-9._-]{1,128}$'),
    mode              text        NOT NULL
                                  CHECK (mode IN ('API_STREAM', 'DIRECT_STORAGE')),
    declared_profile  text        NOT NULL
                                  CHECK (declared_profile IN ('HEADER_ITEMS_V1')),
    status            text        NOT NULL
                                  CHECK (status IN ('CREATED', 'UPLOADING', 'VERIFYING',
                                                    'READY', 'REJECTED', 'EXPIRED')),
    staging_key       text        NOT NULL,
    created_at        timestamptz NOT NULL,
    expires_at        timestamptz NOT NULL,
    CHECK (expires_at > created_at)
);
