ALTER TABLE quote_requests
    ADD COLUMN row_version BIGINT NOT NULL DEFAULT 1
    CHECK (row_version > 0);
