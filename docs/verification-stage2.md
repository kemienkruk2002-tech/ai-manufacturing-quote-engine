# Weryfikacja Etapu 2 — CostEngineV1

Data: 2026-09-16  
Zakres: weryfikacja istniejącej implementacji bez zmian funkcjonalnych.

## Wyniki testów

| Przebieg | Wynik | TRX |
| --- | ---: | --- |
| Targeted unit: `CostEngineTests`, `CanonicalCostSnapshotTests` | 9/9 | `artifacts/test-results/stage2-targeted-unit.trx` |
| Targeted integration: `CostEngineIntegrationTests` | 5/5 | `artifacts/test-results/stage2-targeted-integration.trx` |
| Pełny suite — unit | 555/555 | `artifacts/test-results/stage2-full_net8.0_20260916012439.trx` |
| Pełny suite — integration | 118/118 | `artifacts/test-results/stage2-full_net8.0_20260916012450.trx` |

Łączny końcowy wynik: **673/673**, bez testów pominiętych i bez błędów.

## Potwierdzone kryteria

- Golden Q=150: Tj 87.25 PLN, Tpz 4.02 PLN, labor 91.27 PLN, materiał 14.94 PLN, total 106.21 PLN.
- Pełnoprecyzyjny total `106.20437500000000000000000001` pozostaje deterministyczny.
- 100 kolejnych kalkulacji kosztu daje identyczny pełnoprecyzyjny wynik; replay zachowuje również snapshot hash i calculation hash.
- Brak wymaganej stawki lub kosztu materiału daje `BLOCKED` i `TotalCostUnit = null`.
- Replay historycznego snapshotu nie czyta bieżących stawek ani kosztu materiału.
- Zmiana stawki, kosztu materiału lub wersji CostEngine zmienia snapshot hash i calculation hash; `canonical-v1` pozostaje bez zmian.

## Zakres wyłączony

PricingEngineV1 pozostaje zablokowany do czasu uzgodnienia polityki handlowej i precyzji. RFQ, STEP, AI, email oraz SCOUT nie były częścią tej weryfikacji.
