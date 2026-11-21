-- Why a PDF output failed, beside the reason the job's own row already carries
-- (0005, 0009). Additive, forward-only, one transaction: the runner applies each
-- script in its own transaction.
--
-- Nullable, and no value is written for a row that predates this change: a
-- reader treats the absence as "no reason recorded". No index: nothing selects
-- by a reason in this increment.

ALTER TABLE report_service.report_outputs
    ADD COLUMN error_reason text NULL;
