-- Keep migration 012 and its checksum unchanged. The existing format CHECK and
-- uniqueness index remain in force; add a two-valued completeness guard so SQL
-- CHECK's acceptance of UNKNOWN cannot admit partial identities.
ALTER TABLE rfq_extraction_attempts
    ADD CONSTRAINT ck_rfq_extraction_idempotency_all_or_none
    CHECK (
        (idempotency_version IS NULL
         AND idempotency_key IS NULL
         AND idempotency_request_hash IS NULL)
        OR
        (idempotency_version IS NOT NULL
         AND idempotency_key IS NOT NULL
         AND idempotency_request_hash IS NOT NULL)
    );

-- Keep the original nullable, single-column FK for compatibility and add
-- tenant equality. Existing UNIQUE(tenant_id, id) on audit_events is the target.
-- PostgreSQL MATCH SIMPLE permits legacy NULL audit_event_id pointers.
ALTER TABLE rfq_extraction_attempts
    ADD CONSTRAINT fk_rfq_extraction_attempts_tenant_audit_event
    FOREIGN KEY (tenant_id, audit_event_id)
    REFERENCES audit_events (tenant_id, id);
