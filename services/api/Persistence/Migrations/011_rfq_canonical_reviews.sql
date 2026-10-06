CREATE TABLE rfq_canonical_reviews (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    source_attempt_id UUID NOT NULL,
    draft_row_version BIGINT NOT NULL CHECK (draft_row_version >= 1),
    field_path TEXT NOT NULL CHECK (length(btrim(field_path)) > 0),
    action TEXT NOT NULL CHECK (action IN ('CONFIRM','REJECT','CORRECT')),
    corrected_value JSONB,
    actor TEXT NOT NULL CHECK (length(btrim(actor)) > 0),
    source TEXT NOT NULL CHECK (length(btrim(source)) > 0),
    reason TEXT NOT NULL CHECK (length(btrim(reason)) > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id),
    FOREIGN KEY (tenant_id, quote_request_id, source_attempt_id)
        REFERENCES rfq_extraction_attempts(tenant_id, quote_request_id, id)
);
CREATE INDEX ix_rfq_canonical_reviews_rfq
    ON rfq_canonical_reviews(tenant_id, quote_request_id, created_at, id);

CREATE FUNCTION reject_rfq_canonical_review_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'RFQ_CANONICAL_REVIEW_IMMUTABLE: append a new review event' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_rfq_canonical_review_mutation BEFORE UPDATE OR DELETE ON rfq_canonical_reviews
FOR EACH ROW EXECUTE FUNCTION reject_rfq_canonical_review_mutation();
CREATE TRIGGER guard_rfq_canonical_review_truncate BEFORE TRUNCATE ON rfq_canonical_reviews
FOR EACH STATEMENT EXECUTE FUNCTION reject_rfq_canonical_review_mutation();
