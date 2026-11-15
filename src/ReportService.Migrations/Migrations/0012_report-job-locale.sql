-- The presentation a job was accepted with, completed once from the template it
-- uses: a culture and a time zone, each nullable, each written at most once.
ALTER TABLE report_service.report_jobs
    ADD COLUMN culture   text NULL CHECK (char_length(culture) BETWEEN 1 AND 64),
    ADD COLUMN time_zone text NULL CHECK (char_length(time_zone) BETWEEN 1 AND 64);

-- A value already set is never changed: a later writer may repeat it, never replace
-- it. The rule is the one the template reference's guard already uses, applied to the
-- two columns of the job.
CREATE FUNCTION report_service.report_jobs_locale_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.culture IS NOT NULL AND NEW.culture IS DISTINCT FROM OLD.culture THEN
        RAISE EXCEPTION 'the job culture is set once' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.time_zone IS NOT NULL AND NEW.time_zone IS DISTINCT FROM OLD.time_zone THEN
        RAISE EXCEPTION 'the job time zone is set once' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER report_jobs_locale_guard
    BEFORE UPDATE ON report_service.report_jobs
    FOR EACH ROW EXECUTE FUNCTION report_service.report_jobs_locale_guard();
