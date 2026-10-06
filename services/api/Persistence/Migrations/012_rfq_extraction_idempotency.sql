ALTER TABLE rfq_extraction_attempts
    ADD COLUMN idempotency_version TEXT,
    ADD COLUMN idempotency_key TEXT,
    ADD COLUMN idempotency_request_hash CHAR(64),
    ADD COLUMN audit_event_id UUID REFERENCES audit_events(id);

ALTER TABLE rfq_extraction_attempts
    ADD CONSTRAINT ck_rfq_extraction_idempotency_complete
    CHECK (
        (idempotency_version IS NULL AND idempotency_key IS NULL AND idempotency_request_hash IS NULL)
        OR
        (length(btrim(idempotency_version)) > 0
         AND length(btrim(idempotency_key)) > 0
         AND idempotency_request_hash ~ '^[0-9a-f]{64}$')
    );

CREATE UNIQUE INDEX ux_rfq_extraction_attempts_idempotency
    ON rfq_extraction_attempts(tenant_id, quote_request_id, idempotency_version, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX ix_rfq_extraction_attempts_idempotency_lookup
    ON rfq_extraction_attempts(tenant_id, quote_request_id, idempotency_version, idempotency_key, idempotency_request_hash)
    WHERE idempotency_key IS NOT NULL;
