-- The priority a job was declared with, and the class it is dispatched on. Both
-- are one of 'low', 'normal' or 'high'; a job that declared none, and every job
-- accepted before this script, reads 'normal' for both.
--
-- The declared priority is the consumer's request and is written once, with the
-- job: the guard below rejects any change to it. The effective class is a
-- separate column so a later scheduling rule can move it without rewriting what
-- the consumer declared.
--
-- Additive and forward-only: two columns with a default, a guard function and
-- its trigger. No row is rewritten beyond the default the columns are added with.

ALTER TABLE report_service.report_jobs
    ADD COLUMN declared_priority text NOT NULL DEFAULT 'normal'
        CONSTRAINT report_jobs_declared_priority_check CHECK (declared_priority IN ('low', 'normal', 'high')),
    ADD COLUMN effective_priority_class text NOT NULL DEFAULT 'normal'
        CONSTRAINT report_jobs_effective_priority_class_check CHECK (effective_priority_class IN ('low', 'normal', 'high'));

CREATE FUNCTION report_service.report_jobs_declared_priority_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.declared_priority IS DISTINCT FROM OLD.declared_priority THEN
        RAISE EXCEPTION 'the declared priority of a job is set once' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER report_jobs_declared_priority_guard
    BEFORE UPDATE ON report_service.report_jobs
    FOR EACH ROW EXECUTE FUNCTION report_service.report_jobs_declared_priority_guard();
