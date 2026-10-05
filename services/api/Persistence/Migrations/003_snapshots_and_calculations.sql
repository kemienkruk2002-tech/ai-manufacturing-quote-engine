CREATE TABLE quote_requests (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    -- Customer/RFQ integration is outside Stage 1; no unvalidated customer identifier is stored.
    part_revision_id UUID NOT NULL,
    external_rfq_no TEXT,
    requested_quantity INTEGER NOT NULL CHECK (requested_quantity > 0),
    currency CHAR(3) NOT NULL DEFAULT 'PLN' CHECK (currency ~ '^[A-Z]{3}$'),
    requested_due_date DATE,
    status TEXT NOT NULL DEFAULT 'New' CHECK (status IN ('New','DataReview','ReadyForCalc','Calculated','Approved','Sent','Lost','Won','Blocked')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, external_rfq_no),
    FOREIGN KEY (tenant_id, part_revision_id) REFERENCES part_revisions(tenant_id, id)
);

CREATE TABLE quote_snapshots (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    -- Provenance metadata stays outside canonical input/hash; the approved route is immutable.
    source_route_id UUID NOT NULL,
    canonical_json TEXT NOT NULL,
    -- TEXT retains the exact bytes hashed by the canonical serializer; JSONB supports queries.
    payload JSONB GENERATED ALWAYS AS (canonical_json::jsonb) STORED,
    snapshot_hash CHAR(64) NOT NULL CHECK (snapshot_hash ~ '^[0-9a-f]{64}$'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by UUID,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, snapshot_hash),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id),
    FOREIGN KEY (tenant_id, source_route_id) REFERENCES process_routes(tenant_id, id),
    CHECK (jsonb_typeof(payload) = 'object'),
    CHECK (snapshot_hash = encode(sha256(convert_to(canonical_json, 'UTF8')), 'hex'))
);
CREATE INDEX ix_quote_snapshots_request ON quote_snapshots(tenant_id, quote_request_id);

CREATE FUNCTION reject_snapshot_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'SNAPSHOT_IMMUTABLE: create a new snapshot' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_snapshot_mutation BEFORE UPDATE OR DELETE ON quote_snapshots
FOR EACH ROW EXECUTE FUNCTION reject_snapshot_mutation();
CREATE TRIGGER guard_snapshot_truncate BEFORE TRUNCATE ON quote_snapshots
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();

-- Identical content can be shared by multiple requests. quote_snapshots.quote_request_id
-- records its first origin; this relation records every subsequent request explicitly.
CREATE TABLE quote_request_snapshots (
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    quote_snapshot_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, quote_request_id, quote_snapshot_id),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id),
    FOREIGN KEY (tenant_id, quote_snapshot_id) REFERENCES quote_snapshots(tenant_id, id)
);
CREATE INDEX ix_request_snapshots_snapshot ON quote_request_snapshots(tenant_id, quote_snapshot_id);
CREATE TRIGGER guard_request_snapshot_mutation BEFORE UPDATE OR DELETE ON quote_request_snapshots
FOR EACH ROW EXECUTE FUNCTION reject_snapshot_mutation();
CREATE TRIGGER guard_request_snapshot_truncate BEFORE TRUNCATE ON quote_request_snapshots
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();

CREATE TABLE calculation_runs (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_snapshot_id UUID NOT NULL,
    engine_version TEXT NOT NULL,
    time_engine_version TEXT NOT NULL,
    stock_engine_version TEXT NOT NULL,
    -- These engines are deliberately unimplemented in Stage 1; NULL is explicit.
    cost_engine_version TEXT,
    pricing_engine_version TEXT,
    calculation_hash CHAR(64) NOT NULL CHECK (calculation_hash ~ '^[0-9a-f]{64}$'),
    status TEXT NOT NULL CHECK (status IN ('Success','Blocked','Failed')),
    started_at TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ NOT NULL,
    error_code TEXT,
    result_json TEXT,
    correlation_id TEXT,
    duration_ms NUMERIC CHECK (duration_ms >= 0 AND numeric_is_finite(duration_ms)),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, quote_snapshot_id, engine_version, calculation_hash),
    FOREIGN KEY (tenant_id, quote_snapshot_id) REFERENCES quote_snapshots(tenant_id, id),
    CHECK (finished_at >= started_at),
    CHECK (status <> 'Success' OR result_json IS NOT NULL),
    CHECK (result_json IS NULL OR jsonb_typeof(result_json::jsonb) = 'object')
);

CREATE TABLE calculation_operation_results (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    calculation_run_id UUID NOT NULL,
    process_operation_id UUID NOT NULL,
    operation_no TEXT NOT NULL,
    sequence_no INTEGER NOT NULL CHECK (sequence_no > 0),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    -- Unconstrained NUMERIC preserves the full decimal calculation, without database rounding.
    unit_tj_sec NUMERIC NOT NULL CHECK (unit_tj_sec >= 0 AND numeric_is_finite(unit_tj_sec)),
    batch_tpz_min NUMERIC NOT NULL CHECK (batch_tpz_min >= 0 AND numeric_is_finite(batch_tpz_min)),
    unit_tpz_sec NUMERIC NOT NULL CHECK (unit_tpz_sec >= 0 AND numeric_is_finite(unit_tpz_sec)),
    unit_labor_sec NUMERIC NOT NULL CHECK (unit_labor_sec >= 0 AND numeric_is_finite(unit_labor_sec)),
    UNIQUE (tenant_id, id),
    UNIQUE (calculation_run_id, operation_no),
    UNIQUE (calculation_run_id, sequence_no),
    UNIQUE (calculation_run_id, process_operation_id),
    FOREIGN KEY (tenant_id, calculation_run_id) REFERENCES calculation_runs(tenant_id, id),
    FOREIGN KEY (tenant_id, process_operation_id) REFERENCES process_operations(tenant_id, id)
);
