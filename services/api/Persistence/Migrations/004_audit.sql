CREATE TABLE audit_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    entity_type TEXT NOT NULL,
    entity_id UUID NOT NULL,
    action TEXT NOT NULL,
    old_value JSONB,
    new_value JSONB,
    user_id UUID,
    source TEXT NOT NULL,
    correlation_id TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_audit_entity ON audit_events(tenant_id, entity_type, entity_id, created_at);

CREATE FUNCTION audit_manufacturing_decision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous JSONB; current_row JSONB; decision TEXT;
BEGIN
    IF TG_OP <> 'INSERT' THEN previous := to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN current_row := to_jsonb(NEW); END IF;
    CASE TG_TABLE_NAME
        WHEN 'process_routes' THEN
            IF TG_OP = 'UPDATE' AND OLD.status <> 'Approved' AND NEW.status = 'Approved' THEN
                decision := 'ROUTE_APPROVED';
            ELSE RETURN NULL;
            END IF;
        WHEN 'stock_definitions' THEN decision := 'STOCK_CHANGED';
        WHEN 'machine_rates' THEN decision := 'RATE_CHANGED';
        WHEN 'quote_snapshots' THEN decision := 'SNAPSHOT_CREATED';
        WHEN 'calculation_runs' THEN decision := 'CALCULATION_RUN';
        WHEN 'quote_requests' THEN
            IF TG_OP = 'UPDATE' AND OLD.status <> 'Approved' AND NEW.status = 'Approved' THEN
                decision := 'QUOTE_APPROVED';
            ELSE RETURN NULL;
            END IF;
        ELSE RAISE EXCEPTION 'Unknown audit table %', TG_TABLE_NAME;
    END CASE;
    INSERT INTO audit_events(tenant_id, entity_type, entity_id, action, old_value, new_value, user_id, source, correlation_id)
    VALUES (
        (coalesce(current_row, previous)->>'tenant_id')::UUID,
        TG_TABLE_NAME,
        (coalesce(current_row, previous)->>'id')::UUID,
        decision, previous, current_row,
        nullif(current_setting('app.user_id', true), '')::UUID,
        coalesce(nullif(current_setting('app.audit_source', true), ''), 'Database'),
        coalesce(nullif(current_setting('app.correlation_id', true), ''), current_row->>'correlation_id')
    );
    RETURN NULL;
END;
$$;

CREATE TRIGGER audit_route_approval AFTER UPDATE ON process_routes
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();
CREATE TRIGGER audit_stock_decision AFTER INSERT OR UPDATE OR DELETE ON stock_definitions
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();
CREATE TRIGGER audit_rate_decision AFTER INSERT OR UPDATE OR DELETE ON machine_rates
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();
CREATE TRIGGER audit_snapshot_creation AFTER INSERT ON quote_snapshots
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();
CREATE TRIGGER audit_calculation_run AFTER INSERT ON calculation_runs
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();
CREATE TRIGGER audit_quote_approval AFTER UPDATE ON quote_requests
FOR EACH ROW EXECUTE FUNCTION audit_manufacturing_decision();

CREATE FUNCTION reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'AUDIT_IMMUTABLE: audit events are append-only' USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_audit_mutation BEFORE UPDATE OR DELETE ON audit_events
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER guard_audit_truncate BEFORE TRUNCATE ON audit_events
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();
