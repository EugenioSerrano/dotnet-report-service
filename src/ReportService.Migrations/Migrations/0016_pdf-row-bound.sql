-- The PDF output's admissibility, decided by the validation pass and kept with
-- its result so that a repeated pass returns the same verdict.
--
-- Two nullable columns and their pair check: the code this version writes is the
-- only one the column admits, and a code without the reason it carries is not a
-- refusal any reader can act on. A partition written before this change reads as
-- "no refusal". The output's own failure reason is not this script's: it arrives
-- with the output's row. Additive, forward-only; the runner applies each script
-- in its own transaction.

ALTER TABLE report_service.partitions
    ADD COLUMN pdf_refusal_code   text NULL CHECK (pdf_refusal_code = 'ROW_BOUND_EXCEEDED'),
    ADD COLUMN pdf_refusal_reason text NULL,
    ADD CONSTRAINT partitions_pdf_refusal_pair CHECK ((pdf_refusal_code IS NULL) = (pdf_refusal_reason IS NULL));
