-- What produced each rendered block's presentation: the job's fixed settings,
-- the engine that composed it, and the globalisation data of the image.
-- One value is admitted without the seven members: 'unrecorded', which is what
-- a row written before this column existed carries, and what a build without
-- the presentation library inserts when it does not name the column at all.
ALTER TABLE report_service.pdf_part_commits
    ADD COLUMN presentation jsonb NULL;

UPDATE report_service.pdf_part_commits
   SET presentation = '{"source":"unrecorded"}'::jsonb
 WHERE presentation IS NULL;

ALTER TABLE report_service.pdf_part_commits
    ALTER COLUMN presentation SET NOT NULL,
    ALTER COLUMN presentation SET DEFAULT '{"source":"unrecorded"}'::jsonb,
    ADD CONSTRAINT pdf_part_commits_presentation_complete CHECK (
        presentation = '{"source":"unrecorded"}'::jsonb
        OR (
            presentation ?& ARRAY['culture', 'timeZone', 'clock', 'engine',
                                  'engineVersion', 'icuVersion', 'tzDataVersion']
            AND jsonb_typeof(presentation -> 'culture')       = 'string'
            AND jsonb_typeof(presentation -> 'timeZone')      = 'string'
            AND jsonb_typeof(presentation -> 'clock')         = 'string'
            AND jsonb_typeof(presentation -> 'engine')        = 'string'
            AND jsonb_typeof(presentation -> 'engineVersion') = 'string'
            AND jsonb_typeof(presentation -> 'icuVersion')    = 'string'
            AND jsonb_typeof(presentation -> 'tzDataVersion') = 'string'
        )
    );
