-- When a published artifact stops being downloadable. Additive, forward-only,
-- one transaction: the runner applies each script in its own transaction.
--
-- Rows published before this change become expired immediately: the backfill
-- sets expires_at to created_at, so nothing published earlier is suddenly
-- downloadable for a period it never promised.
--
-- No index on expires_at: nothing selects by it in this increment.

ALTER TABLE report_service.artifacts ADD COLUMN expires_at timestamptz NULL;
UPDATE report_service.artifacts SET expires_at = created_at WHERE expires_at IS NULL;
ALTER TABLE report_service.artifacts
    ALTER COLUMN expires_at SET NOT NULL,
    ADD CONSTRAINT artifacts_expiry_after_publication CHECK (expires_at >= created_at);
