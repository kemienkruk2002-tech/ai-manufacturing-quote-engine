CREATE TABLE process_routes (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    part_revision_id UUID NOT NULL,
    route_code TEXT NOT NULL,
    version TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Approved','Archived')),
    edit_version BIGINT NOT NULL DEFAULT 0 CHECK (edit_version >= 0),
    approved_by UUID,
    approved_at TIMESTAMPTZ,
    source_reference TEXT,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, part_revision_id, route_code, version),
    FOREIGN KEY (tenant_id, part_revision_id) REFERENCES part_revisions(tenant_id, id)
);

CREATE TABLE process_operations (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    route_id UUID NOT NULL,
    operation_no TEXT NOT NULL CHECK (length(btrim(operation_no)) > 0),
    sequence_no INTEGER NOT NULL CHECK (sequence_no > 0),
    operation_type TEXT NOT NULL,
    operation_name TEXT NOT NULL,
    machine_id UUID,
    tj_sec NUMERIC(18,6) NOT NULL CHECK (tj_sec >= 0 AND numeric_is_finite(tj_sec)),
    tpz_min_per_batch NUMERIC(18,6) NOT NULL CHECK (tpz_min_per_batch >= 0 AND numeric_is_finite(tpz_min_per_batch)),
    setup_description TEXT,
    source_reference TEXT,
    confidence NUMERIC(18,6) CHECK (confidence BETWEEN 0 AND 1 AND numeric_is_finite(confidence)),
    origin TEXT NOT NULL CHECK (origin IN ('Manual','Document','AiProposed','Imported')),
    approval_status TEXT NOT NULL DEFAULT 'Proposed' CHECK (approval_status IN ('Proposed','Approved','Rejected')),
    UNIQUE (tenant_id, id),
    UNIQUE (route_id, operation_no),
    UNIQUE (route_id, sequence_no),
    FOREIGN KEY (tenant_id, route_id) REFERENCES process_routes(tenant_id, id),
    FOREIGN KEY (tenant_id, machine_id) REFERENCES machines(tenant_id, id)
);
CREATE INDEX ix_operations_machine ON process_operations(tenant_id, machine_id);

CREATE TABLE inspection_requirements (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    process_operation_id UUID NOT NULL,
    requirement_no TEXT NOT NULL,
    requirement_type TEXT NOT NULL CHECK (requirement_type IN ('Dimension','GdT','Roughness','Thread','Spline','Visual','Other')),
    description TEXT NOT NULL,
    nominal_value TEXT,
    tolerance_text TEXT,
    measurement_method TEXT,
    sampling_text TEXT,
    -- "Częściowo" is not mapped to either true or false; preserve the source text.
    requires_protocol BOOLEAN,
    protocol_text TEXT,
    source_reference TEXT,
    origin TEXT NOT NULL CHECK (origin IN ('Document','AiExtracted','Manual')),
    approval_status TEXT NOT NULL DEFAULT 'Proposed' CHECK (approval_status IN ('Proposed','Approved','Rejected')),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, process_operation_id, requirement_no),
    FOREIGN KEY (tenant_id, process_operation_id) REFERENCES process_operations(tenant_id, id)
);

CREATE FUNCTION enforce_route_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'Draft' THEN
            RAISE EXCEPTION 'ROUTE_MUST_START_DRAFT: insert operations, then approve the complete route' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.status IN ('Approved', 'Archived') THEN
        RAISE EXCEPTION 'APPROVED_ROUTE_IMMUTABLE: create a new Draft version' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    -- UPDATE already holds this parent row lock. Child mutations acquire the same lock.
    IF NEW.status = 'Approved' THEN
        IF NOT EXISTS (SELECT 1 FROM process_operations WHERE tenant_id = NEW.tenant_id AND route_id = NEW.id) THEN
            RAISE EXCEPTION 'ROUTE_EMPTY: an Approved route must contain operations' USING ERRCODE = '23514';
        END IF;
        IF EXISTS (
            SELECT 1 FROM process_operations
            WHERE tenant_id = NEW.tenant_id AND route_id = NEW.id
              AND (approval_status <> 'Approved' OR machine_id IS NULL)
        ) THEN
            RAISE EXCEPTION 'ROUTE_INCOMPLETE: every operation must be approved and have a machine' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER guard_route_lifecycle
BEFORE INSERT OR UPDATE OR DELETE ON process_routes
FOR EACH ROW EXECUTE FUNCTION enforce_route_lifecycle();

CREATE FUNCTION lock_editable_routes(old_route UUID, new_route UUID) RETURNS void LANGUAGE plpgsql AS $$
DECLARE parent RECORD;
BEGIN
    -- Stable ordering prevents deadlocks when moving operations between two drafts.
    -- FOR UPDATE serializes changes with approval, including a concurrent transition.
    FOR parent IN SELECT id, status FROM process_routes
        WHERE id = old_route OR id = new_route ORDER BY id FOR UPDATE
    LOOP
        IF parent.status IN ('Approved', 'Archived') THEN
            RAISE EXCEPTION 'APPROVED_ROUTE_IMMUTABLE: operations and inspection requirements are frozen' USING ERRCODE = '23514';
        END IF;
        -- A row lock alone does not invalidate a RepeatableRead snapshot. Touching
        -- the draft also makes stale concurrent approval fail with serialization_error.
        UPDATE process_routes SET edit_version = edit_version + 1 WHERE id = parent.id;
    END LOOP;
END;
$$;

CREATE FUNCTION enforce_operation_route_immutability() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE old_route UUID; new_route UUID;
BEGIN
    IF TG_OP <> 'INSERT' THEN old_route := OLD.route_id; END IF;
    IF TG_OP <> 'DELETE' THEN new_route := NEW.route_id; END IF;
    PERFORM lock_editable_routes(old_route, new_route);
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER guard_operation_route
BEFORE INSERT OR UPDATE OR DELETE ON process_operations
FOR EACH ROW EXECUTE FUNCTION enforce_operation_route_immutability();

CREATE FUNCTION enforce_inspection_route_immutability() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE old_route UUID; new_route UUID;
BEGIN
    -- Lock operation rows first so moving an operation cannot race an inspection edit.
    PERFORM id FROM process_operations
    WHERE id IN (
        CASE WHEN TG_OP <> 'INSERT' THEN OLD.process_operation_id END,
        CASE WHEN TG_OP <> 'DELETE' THEN NEW.process_operation_id END
    ) ORDER BY id FOR UPDATE;
    IF TG_OP <> 'INSERT' THEN
        SELECT route_id INTO old_route FROM process_operations WHERE id = OLD.process_operation_id;
    END IF;
    IF TG_OP <> 'DELETE' THEN
        SELECT route_id INTO new_route FROM process_operations WHERE id = NEW.process_operation_id;
    END IF;
    PERFORM lock_editable_routes(old_route, new_route);
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER guard_inspection_route
BEFORE INSERT OR UPDATE OR DELETE ON inspection_requirements
FOR EACH ROW EXECUTE FUNCTION enforce_inspection_route_immutability();

CREATE FUNCTION reject_protected_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'IMMUTABLE_DATA: TRUNCATE is not allowed on %', TG_TABLE_NAME USING ERRCODE = '23514';
END;
$$;
CREATE TRIGGER guard_routes_truncate BEFORE TRUNCATE ON process_routes
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();
CREATE TRIGGER guard_operations_truncate BEFORE TRUNCATE ON process_operations
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();
CREATE TRIGGER guard_inspections_truncate BEFORE TRUNCATE ON inspection_requirements
FOR EACH STATEMENT EXECUTE FUNCTION reject_protected_truncate();
