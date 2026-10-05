-- Stage 1: authoritative, tenant-scoped manufacturing master data.
-- UUIDs are supplied by the application; runtime identifiers never enter a calculation payload.
-- PostgreSQL NaN compares greater than ordinary numbers, so >= 0 alone is insufficient.
CREATE FUNCTION numeric_is_finite(value NUMERIC) RETURNS BOOLEAN
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$ SELECT value > '-Infinity'::NUMERIC AND value < 'Infinity'::NUMERIC $$;

CREATE TABLE tenants (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL CHECK (length(btrim(name)) > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE parts (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    part_number TEXT NOT NULL CHECK (length(btrim(part_number)) > 0),
    name TEXT NOT NULL CHECK (length(btrim(name)) > 0),
    default_unit TEXT NOT NULL DEFAULT 'szt',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, part_number)
);

CREATE TABLE materials (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    material_code TEXT NOT NULL CHECK (length(btrim(material_code)) > 0),
    display_name TEXT NOT NULL,
    standard_grade TEXT NOT NULL,
    density_kg_m3 NUMERIC(18,6) CHECK (density_kg_m3 > 0 AND numeric_is_finite(density_kg_m3)),
    active BOOLEAN NOT NULL DEFAULT true,
    valid_from TIMESTAMPTZ,
    valid_to TIMESTAMPTZ,
    source_reference TEXT,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, material_code),
    CHECK (valid_to IS NULL OR (valid_from IS NOT NULL AND valid_to > valid_from))
);

CREATE TABLE stock_definitions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    material_id UUID NOT NULL,
    stock_type TEXT NOT NULL CHECK (stock_type IN ('RoundBar','FlatBar','Plate','Tube','Forging','Casting','Other')),
    diameter_mm NUMERIC(18,6) CHECK (diameter_mm > 0 AND numeric_is_finite(diameter_mm)),
    width_mm NUMERIC(18,6) CHECK (width_mm > 0 AND numeric_is_finite(width_mm)),
    height_mm NUMERIC(18,6) CHECK (height_mm > 0 AND numeric_is_finite(height_mm)),
    thickness_mm NUMERIC(18,6) CHECK (thickness_mm > 0 AND numeric_is_finite(thickness_mm)),
    length_mm NUMERIC(18,6) CHECK (length_mm > 0 AND numeric_is_finite(length_mm)),
    allowance_mm NUMERIC(18,6) CHECK (allowance_mm >= 0 AND numeric_is_finite(allowance_mm)),
    kerf_mm NUMERIC(18,6) CHECK (kerf_mm >= 0 AND numeric_is_finite(kerf_mm)),
    -- NULL or zero is stored honestly; StockEngine returns BLOCKED for either.
    norm_mass_kg_per_unit NUMERIC(18,6) CHECK (norm_mass_kg_per_unit >= 0 AND numeric_is_finite(norm_mass_kg_per_unit)),
    source_type TEXT NOT NULL CHECK (source_type IN ('Document','MasterData','Manual','Calculated')),
    source_reference TEXT,
    rule_version TEXT,
    approval_status TEXT NOT NULL DEFAULT 'Proposed' CHECK (approval_status IN ('Proposed','Approved','Rejected')),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, id, material_id),
    FOREIGN KEY (tenant_id, material_id) REFERENCES materials(tenant_id, id)
);

CREATE TABLE part_revisions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    part_id UUID NOT NULL,
    material_id UUID,
    stock_definition_id UUID,
    revision_code TEXT CHECK (revision_code IS NULL OR length(btrim(revision_code)) > 0),
    variant TEXT,
    process_version TEXT,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Active','Archived')),
    final_mass_kg NUMERIC(18,6) CHECK (final_mass_kg >= 0 AND numeric_is_finite(final_mass_kg)),
    source_reference TEXT,
    valid_from TIMESTAMPTZ,
    valid_to TIMESTAMPTZ,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, part_id) REFERENCES parts(tenant_id, id),
    FOREIGN KEY (tenant_id, material_id) REFERENCES materials(tenant_id, id),
    FOREIGN KEY (tenant_id, stock_definition_id) REFERENCES stock_definitions(tenant_id, id),
    FOREIGN KEY (tenant_id, stock_definition_id, material_id) REFERENCES stock_definitions(tenant_id, id, material_id),
    CHECK (stock_definition_id IS NULL OR material_id IS NOT NULL),
    CHECK (valid_to IS NULL OR (valid_from IS NOT NULL AND valid_to > valid_from))
);
CREATE INDEX ix_part_revisions_part ON part_revisions(tenant_id, part_id);

CREATE TABLE machines (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    machine_code TEXT NOT NULL CHECK (length(btrim(machine_code)) > 0),
    name TEXT NOT NULL,
    machine_group TEXT,
    capabilities JSONB,
    max_x_mm NUMERIC(18,6) CHECK (max_x_mm > 0 AND numeric_is_finite(max_x_mm)),
    max_y_mm NUMERIC(18,6) CHECK (max_y_mm > 0 AND numeric_is_finite(max_y_mm)),
    max_z_mm NUMERIC(18,6) CHECK (max_z_mm > 0 AND numeric_is_finite(max_z_mm)),
    active BOOLEAN NOT NULL DEFAULT true,
    valid_from TIMESTAMPTZ,
    valid_to TIMESTAMPTZ,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, machine_code),
    CHECK (valid_to IS NULL OR (valid_from IS NOT NULL AND valid_to > valid_from))
);

CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE TABLE machine_rates (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    machine_id UUID NOT NULL,
    rate_type TEXT NOT NULL CHECK (rate_type IN ('Tj','Tpz','Overhead','Labor','Other')),
    rate_pln_per_hour NUMERIC(18,6) NOT NULL CHECK (rate_pln_per_hour >= 0 AND numeric_is_finite(rate_pln_per_hour)),
    effective_from TIMESTAMPTZ NOT NULL,
    effective_to TIMESTAMPTZ,
    source_reference TEXT,
    rate_version TEXT NOT NULL CHECK (length(btrim(rate_version)) > 0),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, machine_id) REFERENCES machines(tenant_id, id),
    CHECK (effective_to IS NULL OR effective_to > effective_from),
    EXCLUDE USING gist (
        tenant_id WITH =,
        machine_id WITH =,
        rate_type WITH =,
        tstzrange(effective_from, effective_to, '[)') WITH &&
    )
);
