-- The request's identity and the key a consumer may bind to a job: every job
-- records the identity of what it was asked for, a key binds one job per consumer
-- and is never reassigned, and neither ever changes after acceptance.
--
-- The job table already carries a guard for its presentation; its function is
-- replaced here so the one trigger guards the request as well. The columns are
-- nullable only for the jobs accepted before this script: every job accepted
-- afterwards carries a fingerprint, which the store and its contract enforce.
-- Additive and forward-only; no row is rewritten.

ALTER TABLE report_service.report_jobs
    ADD COLUMN idempotency_key text NULL
        CONSTRAINT report_jobs_idempotency_key_form CHECK (idempotency_key ~ '^[!-~]{1,255}$'),
    ADD COLUMN fingerprint     text NULL
        CONSTRAINT report_jobs_fingerprint_form CHECK (fingerprint ~ '^v[1-9][0-9]{0,2}:[0-9a-f]{64}$'),
    ADD CONSTRAINT report_jobs_key_needs_fingerprint
        CHECK (idempotency_key IS NULL OR fingerprint IS NOT NULL);

-- One job per consumer and key, decided by this index inside the acceptance
-- transaction: a request racing another with the same key waits for it and
-- receives its job.
CREATE UNIQUE INDEX report_jobs_idempotency
    ON report_service.report_jobs (consumer_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- What identifies a job's request never changes after acceptance, beside the two
-- presentation members the guard already set once.
CREATE OR REPLACE FUNCTION report_service.report_jobs_locale_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.consumer_id     IS DISTINCT FROM OLD.consumer_id
       OR NEW.upload_id       IS DISTINCT FROM OLD.upload_id
       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
       OR NEW.fingerprint     IS DISTINCT FROM OLD.fingerprint THEN
        RAISE EXCEPTION 'the request of a report job is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.culture IS NOT NULL AND NEW.culture IS DISTINCT FROM OLD.culture THEN
        RAISE EXCEPTION 'the job culture is set once' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.time_zone IS NOT NULL AND NEW.time_zone IS DISTINCT FROM OLD.time_zone THEN
        RAISE EXCEPTION 'the job time zone is set once' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
