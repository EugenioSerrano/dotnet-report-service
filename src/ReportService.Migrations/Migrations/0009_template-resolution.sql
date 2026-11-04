-- The by-name baseline recorded at acceptance, the version a job is pinned to,
-- and the message of a job's failure.
ALTER TABLE report_service.template_references
    ADD COLUMN baseline_version text        NULL,
    -- the pin: names and foreign key as the pin contract fixes them
    ADD COLUMN resolved_version text        NULL,
    ADD COLUMN commit_sha       text        NULL,
    ADD COLUMN package_sha256   text        NULL,
    ADD COLUMN engine           text        NULL,
    ADD COLUMN engine_version   text        NULL,
    ADD COLUMN schema_ref       text        NULL,
    ADD COLUMN resolved_at      timestamptz NULL,
    ADD CONSTRAINT template_references_pin_version
        FOREIGN KEY (template_id, resolved_version)
        REFERENCES report_service.template_versions (template_id, version),
    -- a pin is complete or absent
    ADD CONSTRAINT template_references_pin_complete CHECK (
        num_nonnulls(resolved_version, commit_sha, package_sha256, engine,
                     engine_version, schema_ref, resolved_at) IN (0, 7)),
    -- an exact reference is pinned to exactly the version asked for
    ADD CONSTRAINT template_references_exact_pin CHECK (
        reference_form <> 'EXACT' OR resolved_version IS NULL
        OR resolved_version = requested_version),
    -- a baseline exists only for a by-name reference, and is a release version
    ADD CONSTRAINT template_references_baseline CHECK (
        baseline_version IS NULL OR (reference_form = 'BY_NAME' AND baseline_version ~
        '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')),
    -- a by-name pin is a release version (never a pre-release)
    ADD CONSTRAINT template_references_by_name_release CHECK (
        reference_form <> 'BY_NAME' OR resolved_version IS NULL OR resolved_version ~
        '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$');

-- The reference as written never changes, and a pin is set once.
CREATE FUNCTION report_service.template_references_guard() RETURNS trigger
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
           NEW.package_sha256, NEW.engine, NEW.engine_version, NEW.schema_ref,
           NEW.resolved_at)
       IS DISTINCT FROM ROW(OLD.resolved_version, OLD.commit_sha, OLD.package_sha256,
           OLD.engine, OLD.engine_version, OLD.schema_ref, OLD.resolved_at) THEN
        RAISE EXCEPTION 'template pin is set once' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER template_references_guard
    BEFORE UPDATE ON report_service.template_references
    FOR EACH ROW EXECUTE FUNCTION report_service.template_references_guard();

ALTER TABLE report_service.report_jobs
    ADD COLUMN error_message text NULL CHECK (char_length(error_message) <= 1000);
