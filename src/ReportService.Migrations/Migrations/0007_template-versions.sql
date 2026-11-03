-- Template versions taken from the configured template source. A version
-- becomes usable only when its row exists; the row never changes.
CREATE TABLE report_service.template_versions (
    template_id        text        NOT NULL CHECK (template_id ~ '^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$'),
    version            text        NOT NULL CHECK (version ~ '^[0-9A-Za-z.+-]{1,128}$'),
    owner_consumer_id  text        NOT NULL CHECK (owner_consumer_id ~ '^[A-Za-z0-9._-]{1,128}$'),
    shared             boolean     NOT NULL,
    manifest_version   text        NOT NULL CHECK (manifest_version = '1.0'),
    engine             text        NOT NULL CHECK (engine IN ('SCRIBAN', 'HANDLEBARS', 'LIQUID_FLUID')),
    engine_version     text        NOT NULL CHECK (engine_version ~ '^[0-9]+\.[0-9]+\.[0-9]+$'),
    schema_ref         text        NOT NULL CHECK (length(schema_ref) BETWEEN 1 AND 240),
    commit_sha         text        NOT NULL CHECK (commit_sha ~ '^[0-9a-f]{40}$'),
    source_tree_sha    text        NOT NULL CHECK (source_tree_sha ~ '^[0-9a-f]{40}$'),
    package_sha256     text        NOT NULL CHECK (package_sha256 ~ '^[0-9a-f]{64}$'),
    package_size       bigint      NOT NULL CHECK (package_size > 0),
    file_count         integer     NOT NULL CHECK (file_count BETWEEN 1 AND 256),
    object_key         text        NOT NULL UNIQUE,
    manifest           jsonb       NOT NULL,
    taken_at           timestamptz NOT NULL,
    PRIMARY KEY (template_id, version)
);
