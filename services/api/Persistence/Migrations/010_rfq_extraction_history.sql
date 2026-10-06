CREATE TABLE rfq_extraction_attempts (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    request_fingerprint CHAR(64) CHECK (request_fingerprint IS NULL OR request_fingerprint ~ '^[0-9a-f]{64}$'),
    model_id TEXT NOT NULL CHECK (length(btrim(model_id)) > 0),
    prompt_version TEXT NOT NULL CHECK (length(btrim(prompt_version)) > 0),
    schema_version TEXT NOT NULL CHECK (length(btrim(schema_version)) > 0),
    disposition TEXT NOT NULL CHECK (disposition IN ('COMPLETED','REVIEW_MANUAL')),
    result_code TEXT CHECK (result_code IS NULL OR length(btrim(result_code)) > 0),
    source_lineage JSONB NOT NULL CHECK (jsonb_typeof(source_lineage) = 'array'),
    raw_provider_output TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, quote_request_id, id),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id)
);
CREATE INDEX ix_rfq_extraction_attempts_rfq
    ON rfq_extraction_attempts(tenant_id, quote_request_id, created_at, id);

CREATE TABLE rfq_canonical_drafts (
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    source_attempt_id UUID NOT NULL,
    canonical_json JSONB NOT NULL CHECK (jsonb_typeof(canonical_json) = 'object'),
    row_version BIGINT NOT NULL DEFAULT 1 CHECK (row_version >= 1),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, quote_request_id),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id),
    FOREIGN KEY (tenant_id, quote_request_id, source_attempt_id)
        REFERENCES rfq_extraction_attempts(tenant_id, quote_request_id, id)
);

CREATE FUNCTION reject_rfq_extraction_attempt_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'RFQ_EXTRACTION_HISTORY_IMMUTABLE: create a new attempt' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_rfq_extraction_attempt_mutation BEFORE UPDATE OR DELETE ON rfq_extraction_attempts
FOR EACH ROW EXECUTE FUNCTION reject_rfq_extraction_attempt_mutation();
CREATE TRIGGER guard_rfq_extraction_attempt_truncate BEFORE TRUNCATE ON rfq_extraction_attempts
FOR EACH STATEMENT EXECUTE FUNCTION reject_rfq_extraction_attempt_mutation();
