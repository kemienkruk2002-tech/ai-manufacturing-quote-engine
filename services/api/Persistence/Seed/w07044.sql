-- Run inside the migration runner's transaction/advisory lock.
-- All fixture IDs are stable; existing approved rows are never updated.
-- Sources: document 11 and sheet 11A, preserved verbatim in docs/sources.
-- TODO: obtain real machine rates/effective dates, the approval actor/date,
-- and the complete quality requirements. None are invented by this seed.
DO $$
DECLARE
    tenant UUID := '00000000-0000-0000-0000-000000000001';
    part UUID := '10000000-0000-0000-0000-000000000001';
    revision UUID := '20000000-0000-0000-0000-000000000001';
    material UUID := '30000000-0000-0000-0000-000000000001';
    stock UUID := '40000000-0000-0000-0000-000000000001';
    route UUID := '50000000-0000-0000-0000-000000000001';
    quote UUID := '90000000-0000-0000-0000-000000000001';
    doc TEXT := 'https://docs.google.com/document/d/1Iw0poV6wJleF3YtEi5IdZJ491-b-3sHMjqpOB7LL87s/edit';
    sheet TEXT := 'https://docs.google.com/spreadsheets/d/1gV2D3PnhZ0QTX65u9QiZS22qmi-_q_hvY-RqDJgqLfQ/edit';
    item RECORD;
    machine UUID;
    operation UUID;
    inspection UUID;
    previous_audit_source TEXT := current_setting('app.audit_source', true);
BEGIN
    PERFORM set_config('app.audit_source', 'Document', true);
    INSERT INTO tenants(id, name) VALUES (tenant, 'W07044 Golden Test Tenant') ON CONFLICT DO NOTHING;
    INSERT INTO parts(id, tenant_id, part_number, name)
    VALUES (part, tenant, 'W07044', 'Tuleja 92687521_A') ON CONFLICT DO NOTHING;
    INSERT INTO materials(id, tenant_id, material_code, display_name, standard_grade, source_reference)
    VALUES (material, tenant, 'M00320', 'Pręt 48 g/w S355J2', 'S355J2', doc) ON CONFLICT DO NOTHING;
    INSERT INTO stock_definitions(id, tenant_id, material_id, stock_type, diameter_mm, norm_mass_kg_per_unit, source_type, source_reference, approval_status)
    VALUES (stock, tenant, material, 'RoundBar', 48, 5.298000, 'Document', sheet, 'Approved') ON CONFLICT DO NOTHING;
    INSERT INTO part_revisions(id, tenant_id, part_id, material_id, stock_definition_id, revision_code, variant, process_version, status, final_mass_kg, source_reference)
    VALUES (revision, tenant, part, material, stock, NULL, '2', 'Podstawowa(1)', 'Active', 3.540000, sheet) ON CONFLICT DO NOTHING;
    INSERT INTO process_routes(id, tenant_id, part_revision_id, route_code, version, status, source_reference)
    SELECT route, tenant, revision, 'W07044', '1', 'Draft', sheet
    WHERE NOT EXISTS (SELECT 1 FROM process_routes WHERE id = route);

    IF NOT EXISTS (SELECT 1 FROM tenants WHERE id = tenant AND name = 'W07044 Golden Test Tenant')
       OR NOT EXISTS (SELECT 1 FROM parts WHERE id = part AND tenant_id = tenant AND part_number = 'W07044' AND name = 'Tuleja 92687521_A' AND default_unit = 'szt')
       OR NOT EXISTS (SELECT 1 FROM materials WHERE id = material AND tenant_id = tenant AND material_code = 'M00320' AND display_name = 'Pręt 48 g/w S355J2' AND standard_grade = 'S355J2' AND active AND source_reference = doc)
       OR NOT EXISTS (SELECT 1 FROM stock_definitions WHERE id = stock AND tenant_id = tenant AND material_id = material AND stock_type = 'RoundBar' AND diameter_mm = 48 AND norm_mass_kg_per_unit = 5.298000 AND source_type = 'Document' AND approval_status = 'Approved' AND source_reference = sheet)
       OR NOT EXISTS (SELECT 1 FROM part_revisions WHERE id = revision AND tenant_id = tenant AND part_id = part AND material_id = material AND stock_definition_id = stock AND revision_code IS NULL AND variant = '2' AND process_version = 'Podstawowa(1)' AND status = 'Active' AND final_mass_kg = 3.540000 AND source_reference = sheet)
       OR NOT EXISTS (SELECT 1 FROM process_routes WHERE id = route AND tenant_id = tenant AND part_revision_id = revision AND route_code = 'W07044' AND version = '1' AND status IN ('Draft','Approved') AND source_reference = sheet)
    THEN
        RAISE EXCEPTION 'GOLDEN_SEED_CONFLICT: W07044 master data differs from the source fixture';
    END IF;

    FOR item IN SELECT * FROM (VALUES
        (1, '0010', 'Cięcie wstępne',   '4.52',  'PIŁY TARCZOWE',          30, 20, NULL::TEXT),
        (2, '0020', 'Planowanie',       '4.63',  'NAKIEŁCZARKA',          140, 30, NULL::TEXT),
        (3, '0030', 'Toczenie',         '4.81',  'TAE 35 N HANKA',        200, 30, 'Mocować w kłach'),
        (4, '0040', 'Toczenie',         '3.2',   'NLX',                   600, 60, 'Twarde szczęki'),
        (5, '0050', 'Toczenie',         '1.051', 'SBL-500 Obr dokładna 2',285, 60, 'Miękkie przetoczone szczęki'),
        (6, '0060', 'Przeciąganie',     '5.51',  'PRZECIĄGARKI',           80, 20, 'Wielowypust'),
        (7, '0070', 'Mycie',            '5.61',  'ST. GRADOWANIA',         40, 10, 'Mycie i konserwacja'),
        (8, '0080', 'Kontrola końcowa', '0.7',   'KONTROLA JAKOŚCI',        0,  0, 'Na razie 0 w źródle')
    ) AS expected(seq, op_no, op_name, machine_code, machine_name, tj, tpz, notes)
    LOOP
        machine := ('60000000-0000-0000-0000-' || lpad(item.seq::TEXT, 12, '0'))::UUID;
        operation := ('70000000-0000-0000-0000-' || lpad(item.seq::TEXT, 12, '0'))::UUID;
        INSERT INTO machines(id, tenant_id, machine_code, name)
        VALUES (machine, tenant, item.machine_code, item.machine_name) ON CONFLICT DO NOTHING;
        INSERT INTO process_operations(id, tenant_id, route_id, operation_no, sequence_no, operation_type, operation_name, machine_id, tj_sec, tpz_min_per_batch, setup_description, source_reference, origin, approval_status)
        SELECT operation, tenant, route, item.op_no, item.seq, item.op_name, item.op_name, machine, item.tj, item.tpz, item.notes, sheet, 'Document', 'Approved'
        WHERE NOT EXISTS (SELECT 1 FROM process_operations WHERE id = operation);

        IF NOT EXISTS (SELECT 1 FROM machines m WHERE m.id = machine AND m.tenant_id = tenant AND m.machine_code = item.machine_code AND m.name = item.machine_name AND m.active)
           OR NOT EXISTS (
                SELECT 1 FROM process_operations o WHERE o.id = operation AND o.tenant_id = tenant AND o.route_id = route
                  AND o.operation_no = item.op_no AND o.sequence_no = item.seq AND o.operation_type = item.op_name AND o.operation_name = item.op_name
                  AND o.machine_id = machine AND o.tj_sec = item.tj AND o.tpz_min_per_batch = item.tpz
                  AND o.setup_description IS NOT DISTINCT FROM item.notes
                  AND o.source_reference = sheet AND o.origin = 'Document' AND o.approval_status = 'Approved'
           )
        THEN RAISE EXCEPTION 'GOLDEN_SEED_CONFLICT: machine/operation % differs from the source fixture', item.op_no;
        END IF;
    END LOOP;

    FOR item IN SELECT * FROM (VALUES
        (1, 3, '01', 'Dimension', 'Ø45h10 (0/-0,1)',                    'Transametr MMCf 25-50',             '1',                    'Nie',       false),
        (2, 3, '02', 'Roughness', 'Ra 3,2',                            'Chropowatościomierz',              '20',                   'Nie',       false),
        (3, 4, '01', 'GdT',       'Pozycja Ø0,15',                     'Maszyna pomiarowa',                'Sztuka ustawcza',       'Tak',       true),
        (4, 5, '01', 'Thread',    'M40x1,5 6g',                        'Sprawdzian gwintu pierścieniowy',   '5',                    'Nie',       false),
        (5, 5, '02', 'Roughness', 'Ra 0,8',                            'Chropowatościomierz',              '10',                   'Nie',       false),
        (6, 5, '03', 'Roughness', 'Ra 1,6',                            'Chropowatościomierz',              '10',                   'Nie',       false),
        (7, 6, '01', 'Spline',    '16/32, z=9, kąt 30°',               'Sprawdzian GO/NOGO',               '5 / sztuka ustawcza',   'Częściowo',  NULL::BOOLEAN),
        (8, 6, '02', 'Dimension', 'Wymiar przez wałeczki 10,088 mm',    'Maszyna pomiarowa',                'Sztuka ustawcza',       'Tak',       true)
    ) AS expected(seq, op_seq, req_no, req_type, description, method, sampling, protocol, protocol_bool)
    LOOP
        operation := ('70000000-0000-0000-0000-' || lpad(item.op_seq::TEXT, 12, '0'))::UUID;
        inspection := ('80000000-0000-0000-0000-' || lpad(item.seq::TEXT, 12, '0'))::UUID;
        INSERT INTO inspection_requirements(id, tenant_id, process_operation_id, requirement_no, requirement_type, description, measurement_method, sampling_text, requires_protocol, protocol_text, source_reference, origin, approval_status)
        SELECT inspection, tenant, operation, item.req_no, item.req_type, item.description, item.method, item.sampling, item.protocol_bool, item.protocol, sheet, 'Document', 'Approved'
        WHERE NOT EXISTS (SELECT 1 FROM inspection_requirements WHERE id = inspection);
        IF NOT EXISTS (
            SELECT 1 FROM inspection_requirements i WHERE i.id = inspection AND i.tenant_id = tenant AND i.process_operation_id = operation
              AND i.requirement_no = item.req_no AND i.requirement_type = item.req_type AND i.description = item.description
              AND i.measurement_method = item.method AND i.sampling_text = item.sampling AND i.protocol_text = item.protocol
              AND i.requires_protocol IS NOT DISTINCT FROM item.protocol_bool AND i.source_reference = sheet
              AND i.origin = 'Document' AND i.approval_status = 'Approved'
        ) THEN RAISE EXCEPTION 'GOLDEN_SEED_CONFLICT: inspection % differs from the source fixture', item.seq;
        END IF;
    END LOOP;

    IF (SELECT count(*) FROM process_operations WHERE tenant_id = tenant AND route_id = route) <> 8
       OR (SELECT sum(tj_sec) FROM process_operations WHERE tenant_id = tenant AND route_id = route) <> 1375
       OR (SELECT sum(tpz_min_per_batch) FROM process_operations WHERE tenant_id = tenant AND route_id = route) <> 230
       OR (SELECT count(*) FROM inspection_requirements i JOIN process_operations o ON o.tenant_id = i.tenant_id AND o.id = i.process_operation_id WHERE o.tenant_id = tenant AND o.route_id = route) <> 8
    THEN RAISE EXCEPTION 'GOLDEN_SEED_CONFLICT: expected exactly 8 operations, 8 sourced inspections, Tj=1375 and Tpz=230';
    END IF;

    UPDATE process_routes SET status = 'Approved' WHERE id = route AND tenant_id = tenant AND status = 'Draft';
    INSERT INTO quote_requests(id, tenant_id, part_revision_id, requested_quantity, currency, status)
    VALUES (quote, tenant, revision, 150, 'PLN', 'New') ON CONFLICT DO NOTHING;
    IF NOT EXISTS (SELECT 1 FROM quote_requests WHERE id = quote AND tenant_id = tenant AND part_revision_id = revision AND requested_quantity = 150 AND currency = 'PLN')
    THEN RAISE EXCEPTION 'GOLDEN_SEED_CONFLICT: W07044 quote request differs from quantity 150 fixture';
    END IF;
    PERFORM set_config('app.audit_source', coalesce(previous_audit_source, ''), true);
END;
$$;
