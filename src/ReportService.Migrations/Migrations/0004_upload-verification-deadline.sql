-- The verification deadline of a confirmed upload, and the indexes that let
-- overdue uploads be found without scanning the table.
ALTER TABLE report_service.uploads
    ADD COLUMN verification_deadline timestamptz NULL;

-- Uploads confirmed before this script ran receive the default deadline.
UPDATE report_service.uploads
   SET verification_deadline = completed_at + interval '6 hours'
 WHERE status = 'VERIFYING' AND verification_deadline IS NULL;

ALTER TABLE report_service.uploads
    ADD CONSTRAINT uploads_deadline_when_verifying
        CHECK (status <> 'VERIFYING' OR verification_deadline IS NOT NULL),
    ADD CONSTRAINT uploads_deadline_after_confirmation
        CHECK (verification_deadline IS NULL OR verification_deadline > completed_at);

CREATE INDEX uploads_session_due
    ON report_service.uploads (expires_at)
    WHERE status IN ('CREATED', 'UPLOADING');

CREATE INDEX uploads_verification_due
    ON report_service.uploads (verification_deadline)
    WHERE status = 'VERIFYING';
