-- Widen the durable-intent kind set to admit the cancel-job intent the cancel
-- request surface records (US-010.BOLT-001, SPEC-261126-0252 Phase A.1). When a
-- consumer asks to cancel a running job, the API commits CANCELLING and a
-- cancel-job intent in one transaction; the dispatcher's plan for that intent is
-- US-010.BOLT-002, so until then it sits PENDING and is never delivered.
--
-- Additive and forward-only: it only re-adds the CHECK constraint over a wider
-- allowed set and rewrites no row. The shipped 0003/0005 files are never edited.

ALTER TABLE report_service.intents
    DROP CONSTRAINT intents_kind_check,
    ADD CONSTRAINT intents_kind_check CHECK (kind IN ('start-upload-verification', 'start-job', 'cancel-job'));
