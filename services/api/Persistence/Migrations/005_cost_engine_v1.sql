-- Cost Engine V1 data is versioned independently from effective-dated production master rates.
-- The source images do not provide effective dates, so none are invented here.
CREATE TABLE machine_rate_versions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    machine_id UUID NOT NULL,
    rate_version TEXT NOT NULL CHECK (length(btrim(rate_version)) > 0),
    rate_tpz_pln_h NUMERIC(18,6) CHECK (rate_tpz_pln_h >= 0 AND numeric_is_finite(rate_tpz_pln_h)),
    rate_production_pln_h NUMERIC(18,6) CHECK (rate_production_pln_h >= 0 AND numeric_is_finite(rate_production_pln_h)),
    rate_overall_pln_h NUMERIC(18,6) CHECK (rate_overall_pln_h >= 0 AND numeric_is_finite(rate_overall_pln_h)),
    source_reference TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, rate_version, machine_id),
    FOREIGN KEY (tenant_id, machine_id) REFERENCES machines(tenant_id, id)
);

CREATE TABLE quote_cost_inputs (
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    quote_request_id UUID NOT NULL,
    material_cost_unit NUMERIC(18,6) CHECK (material_cost_unit >= 0 AND numeric_is_finite(material_cost_unit)),
    currency CHAR(3) NOT NULL DEFAULT 'PLN' CHECK (currency ~ '^[A-Z]{3}$'),
    source_reference TEXT,
    PRIMARY KEY (tenant_id, quote_request_id),
    FOREIGN KEY (tenant_id, quote_request_id) REFERENCES quote_requests(tenant_id, id)
);

ALTER TABLE calculation_operation_results
    ADD COLUMN rate_overall_pln_h NUMERIC CHECK (rate_overall_pln_h >= 0 AND numeric_is_finite(rate_overall_pln_h)),
    ADD COLUMN rate_tpz_pln_h NUMERIC CHECK (rate_tpz_pln_h >= 0 AND numeric_is_finite(rate_tpz_pln_h)),
    ADD COLUMN tj_cost_unit NUMERIC CHECK (tj_cost_unit >= 0 AND numeric_is_finite(tj_cost_unit)),
    ADD COLUMN tpz_cost_unit NUMERIC CHECK (tpz_cost_unit >= 0 AND numeric_is_finite(tpz_cost_unit)),
    ADD COLUMN labor_cost_unit NUMERIC CHECK (labor_cost_unit >= 0 AND numeric_is_finite(labor_cost_unit));

CREATE FUNCTION reject_cost_rate_version_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'RATE_VERSION_IMMUTABLE: create a new rate_version' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_cost_rate_version_mutation BEFORE UPDATE OR DELETE ON machine_rate_versions
FOR EACH ROW EXECUTE FUNCTION reject_cost_rate_version_mutation();
CREATE TRIGGER guard_cost_rate_version_truncate BEFORE TRUNCATE ON machine_rate_versions
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();
