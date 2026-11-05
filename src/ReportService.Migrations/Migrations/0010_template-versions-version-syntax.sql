-- A version is the exact semantic version the take read from the package's
-- manifest: major.minor.patch without leading zeros, an optional pre-release
-- of dot-separated identifiers and an optional build of dot-separated
-- identifiers, ASCII only, at most 128 characters. The character-set check of
-- 0007 admits any letters, so it accepts 'latest', which no take ever writes;
-- this check states the syntax the product does write, so the table refuses
-- every other spelling without the store having to police it.
ALTER TABLE report_service.template_versions
    ADD CONSTRAINT template_versions_version_syntax CHECK (
        length(version) <= 128
        AND version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)\.)*(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))?(\+([0-9A-Za-z-]+\.)*[0-9A-Za-z-]+)?$'
    );
