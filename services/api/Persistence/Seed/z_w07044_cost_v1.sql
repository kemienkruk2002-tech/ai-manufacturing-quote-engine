-- Delta sources supplied for CostEngineV1: Koszty.jpg and kalkulacja kosztu.jpg.
-- Pricing/offer display fields and SCOUT formulas are deliberately not seeded as rules.
DO $$
DECLARE
    tenant UUID := '00000000-0000-0000-0000-000000000001';
    quote UUID := '90000000-0000-0000-0000-000000000001';
    version TEXT := 'w07044-koszty-v1';
    item RECORD;
    machine UUID;
    rate_id UUID;
BEGIN
    FOR item IN SELECT * FROM (VALUES
        (1, '4.52',  145.70, 174.62, 182.83),
        (2, '4.63',  130.76, 147.51, 147.51),
        (3, '4.81',  124.84, 153.54, 160.70),
        (4, '3.2',   208.88, 249.19, 295.23),
        (5, '1.051', 153.31, 202.63, 212.01),
        (6, '5.51',  132.79, 160.65, 160.65),
        (7, '5.61',  118.53, 135.02, 135.02),
        (8, '0.7',     0.00,   0.00,   0.00)
    ) AS expected(seq, machine_code, rate_tpz, rate_production, rate_overall)
    LOOP
        machine := ('60000000-0000-0000-0000-' || lpad(item.seq::TEXT, 12, '0'))::UUID;
        rate_id := ('a0000000-0000-0000-0000-' || lpad(item.seq::TEXT, 12, '0'))::UUID;
        INSERT INTO machine_rate_versions(id,tenant_id,machine_id,rate_version,rate_tpz_pln_h,
            rate_production_pln_h,rate_overall_pln_h,source_reference)
        VALUES(rate_id,tenant,machine,version,item.rate_tpz,item.rate_production,item.rate_overall,'Koszty.jpg')
        ON CONFLICT DO NOTHING;
        IF NOT EXISTS (
            SELECT 1 FROM machine_rate_versions r JOIN machines m ON m.tenant_id=r.tenant_id AND m.id=r.machine_id
            WHERE r.id=rate_id AND r.tenant_id=tenant AND r.rate_version=version
              AND m.machine_code=item.machine_code AND r.rate_tpz_pln_h=item.rate_tpz
              AND r.rate_production_pln_h=item.rate_production AND r.rate_overall_pln_h=item.rate_overall
              AND r.source_reference='Koszty.jpg'
        ) THEN RAISE EXCEPTION 'GOLDEN_COST_SEED_CONFLICT: rate % differs from Koszty.jpg', item.machine_code;
        END IF;
    END LOOP;

    INSERT INTO quote_cost_inputs(tenant_id,quote_request_id,material_cost_unit,currency,source_reference)
    VALUES(tenant,quote,14.94,'PLN','kalkulacja kosztu.jpg') ON CONFLICT DO NOTHING;
    IF NOT EXISTS (
        SELECT 1 FROM quote_cost_inputs WHERE tenant_id=tenant AND quote_request_id=quote
          AND material_cost_unit=14.94 AND currency='PLN' AND source_reference='kalkulacja kosztu.jpg'
    ) THEN RAISE EXCEPTION 'GOLDEN_COST_SEED_CONFLICT: material cost differs from kalkulacja kosztu.jpg';
    END IF;
END;
$$;
