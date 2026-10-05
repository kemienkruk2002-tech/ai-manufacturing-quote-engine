-- Metadata only. File bytes are deliberately not stored in PostgreSQL.
CREATE TABLE quote_request_documents (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    logical_key TEXT NOT NULL CHECK (length(btrim(logical_key)) > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, quote_request_id, logical_key),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id)
);

CREATE TABLE quote_request_document_versions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    document_id UUID NOT NULL,
    version_no INTEGER NOT NULL CHECK (version_no >= 1),
    original_file_name TEXT NOT NULL CHECK (length(btrim(original_file_name)) > 0),
    mime_type TEXT CHECK (mime_type IS NULL OR length(btrim(mime_type)) > 0),
    byte_size BIGINT NOT NULL CHECK (byte_size >= 0),
    sha256 CHAR(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    source_reference TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, document_id, version_no),
    UNIQUE (tenant_id, document_id, sha256),
    FOREIGN KEY (tenant_id, document_id) REFERENCES quote_request_documents(tenant_id, id)
);
CREATE INDEX ix_quote_request_document_versions_document
    ON quote_request_document_versions(tenant_id, document_id, version_no);

CREATE FUNCTION reject_rfq_file_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'RFQ_FILE_HISTORY_IMMUTABLE: create a new version' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_rfq_document_mutation BEFORE UPDATE OR DELETE ON quote_request_documents
FOR EACH ROW EXECUTE FUNCTION reject_rfq_file_history_mutation();
CREATE TRIGGER guard_rfq_document_truncate BEFORE TRUNCATE ON quote_request_documents
FOR EACH STATEMENT EXECUTE FUNCTION reject_rfq_file_history_mutation();
CREATE TRIGGER guard_rfq_document_version_mutation BEFORE UPDATE OR DELETE ON quote_request_document_versions
FOR EACH ROW EXECUTE FUNCTION reject_rfq_file_history_mutation();
CREATE TRIGGER guard_rfq_document_version_truncate BEFORE TRUNCATE ON quote_request_document_versions
FOR EACH STATEMENT EXECUTE FUNCTION reject_rfq_file_history_mutation();
