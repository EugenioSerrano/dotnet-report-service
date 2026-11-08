-- A version taken from the deployment's object store states the publication it
-- verified; a version taken before that change keeps the commit it was read at.
-- The change is additive: no row is rewritten and no value is invented.
ALTER TABLE report_service.template_versions
    ALTER COLUMN commit_sha DROP NOT NULL,
    ALTER COLUMN source_tree_sha DROP NOT NULL;

ALTER TABLE report_service.template_versions
    ADD COLUMN source_descriptor_sha256 text NULL,
    ADD COLUMN source_descriptor_key text NULL,
    ADD COLUMN source_objects jsonb NULL;

ALTER TABLE report_service.template_versions
    ADD CONSTRAINT template_versions_source_descriptor_sha256 CHECK (
        source_descriptor_sha256 IS NULL OR source_descriptor_sha256 ~ '^[0-9a-f]{64}$');

ALTER TABLE report_service.template_versions
    ADD CONSTRAINT template_versions_provenance CHECK (
        (commit_sha IS NOT NULL AND source_tree_sha IS NOT NULL
         AND source_descriptor_sha256 IS NULL AND source_descriptor_key IS NULL
         AND source_objects IS NULL)
        OR
        (commit_sha IS NULL AND source_tree_sha IS NULL
         AND source_descriptor_sha256 IS NOT NULL AND source_descriptor_key IS NOT NULL
         AND source_objects IS NOT NULL));

ALTER TABLE report_service.template_references
    ADD COLUMN source_descriptor_sha256 text NULL,
    ADD COLUMN source_objects jsonb NULL;

ALTER TABLE report_service.template_references
    ADD CONSTRAINT template_references_source_descriptor_sha256 CHECK (
        source_descriptor_sha256 IS NULL OR source_descriptor_sha256 ~ '^[0-9a-f]{64}$');

ALTER TABLE report_service.template_references
    ADD CONSTRAINT template_references_pin_shapes CHECK (
        num_nonnulls(resolved_version, commit_sha, source_descriptor_sha256, package_sha256,
                     source_objects, engine, engine_version, schema_ref, resolved_at) = 0
        OR
        (commit_sha IS NOT NULL AND source_descriptor_sha256 IS NULL AND source_objects IS NULL
         AND num_nonnulls(resolved_version, commit_sha, package_sha256, engine, engine_version,
                          schema_ref, resolved_at) = 7)
        OR
        (commit_sha IS NULL AND source_descriptor_sha256 IS NOT NULL AND source_objects IS NOT NULL
         AND num_nonnulls(resolved_version, source_descriptor_sha256, package_sha256, source_objects,
                          engine, engine_version, schema_ref, resolved_at) = 8));

ALTER TABLE report_service.template_references
    DROP CONSTRAINT template_references_pin_complete;

CREATE OR REPLACE FUNCTION report_service.template_references_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.report_job_id     IS DISTINCT FROM OLD.report_job_id
       OR NEW.template_id       IS DISTINCT FROM OLD.template_id
       OR NEW.reference_form    IS DISTINCT FROM OLD.reference_form
       OR NEW.requested_version IS DISTINCT FROM OLD.requested_version
       OR NEW.baseline_version  IS DISTINCT FROM OLD.baseline_version THEN
        RAISE EXCEPTION 'template reference is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.resolved_at IS NOT NULL AND ROW(NEW.resolved_version, NEW.commit_sha,
           NEW.source_descriptor_sha256, NEW.package_sha256, NEW.source_objects,
           NEW.engine, NEW.engine_version, NEW.schema_ref, NEW.resolved_at)
       IS DISTINCT FROM ROW(OLD.resolved_version, OLD.commit_sha,
           OLD.source_descriptor_sha256, OLD.package_sha256, OLD.source_objects,
           OLD.engine, OLD.engine_version, OLD.schema_ref, OLD.resolved_at) THEN
        RAISE EXCEPTION 'template pin is set once' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
