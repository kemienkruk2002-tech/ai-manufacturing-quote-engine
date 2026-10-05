# Etap 1 — raport wykonania i weryfikacji

Data: 2026-09-15. Stan: **implementacja i weryfikacja lokalna zakończone, 659/659 testów zielonych**.

## Implementacja

Powstało nowe lokalne repozytorium z sześcioma projektami .NET 8: Domain, Application, Persistence, Host, UnitTests i IntegrationTests. Są wszystkie minimalne encje i enumy, cztery migracje PostgreSQL, seed W07044, czyste TimeEngineV1/StockEngineV1, canonical snapshot/SHA-256, replay, tenant-scoped repozytoria i structured logging.

Osiem operacji 0010–0080 i osiem wymagań kontroli pochodzi ze źródeł 11/11A. Snapshot, Approved routing i audyt mają ochronę również przed bezpośrednim SQL. Odczyt przygotowujący snapshot jest transakcyjny; zapis wyniku jest idempotentny i zachowuje pełną precyzję decimal.

Pliki: [pełny manifest](changed-files.txt). Instrukcja: [README](../README.md). Decyzje: [ADR 001](adr/001-stage1.md).

## Migracje

1. `services/api/Persistence/Migrations/001_core_master_data.sql`
2. `services/api/Persistence/Migrations/002_routing_and_time.sql`
3. `services/api/Persistence/Migrations/003_snapshots_and_calculations.sql`
4. `services/api/Persistence/Migrations/004_audit.sql`

Seed: `services/api/Persistence/Seed/w07044.sql`.

## Polecenie testów i wynik

Z katalogu repozytorium:

```powershell
.\scripts\test.ps1 -DatabaseMode Portable
```

Skrypt uruchamia lokalną bazę, wykonuje `dotnet restore QuoteEngine.sln --locked-mode` i pełny `dotnet test QuoteEngine.sln --configuration Release --no-restore` z raportami TRX. Ostatnie uruchomienie zwróciło kod **0**.

| Zestaw | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Unit | 546 | 0 | 0 |
| PostgreSQL + API integration | 113 | 0 | 0 |
| **Razem** | **659** | **0** | **0** |

Raporty z końcowego uruchomienia:

- [Unit TRX](../artifacts/test-results/stage1_net8.0_20260915184335.trx)
- [Integration TRX](../artifacts/test-results/stage1_net8.0_20260915184402.trx)

Środowisko: Windows, SDK 10.0.102, target/runtime .NET 8, rzeczywisty PostgreSQL 16.15. Docker Desktop nie uruchomił backendu; testy wykonały się na izolowanym portable PostgreSQL. Lokalna baza demonstracyjna pozostaje na `127.0.0.1:55432`, `manufacturing_quote_test`. Tymczasowe bazy testów i smoke zostały usunięte.

## Potwierdzone kryteria

- Migracje od zera i kontrola checksum historii.
- Seed wykonywany wielokrotnie, bez duplikatów oraz bez ponownego audytu zatwierdzenia.
- Routing dokładnie 0010–0080; zatwierdzone operacje i zachowane źródłowe 0/0 dla kontroli końcowej.
- Q=150: **1375 / 230 / 92 / 1467 / 61.125**.
- Dziewięć wskazanych ilości oraz każda ilość 1–500.
- Canonical snapshot niezależny od kultury, kolejności wejściowych kolekcji, skali decimal i runtime metadata.
- Repeatability 100×: jeden snapshot hash, calculation hash i canonical wynik; również replay danych pobranych z DB.
- Brak normatywu, normatyw≤0 i inne krytyczne braki blokują Stock/Time; nie stają się zerami.
- Unique operations/sequence, negative times, missing machine/empty route, immutable Approved route/snapshot/audit, tenant FK.
- Nakładające się okresy stawek, granice `[from,to)`, konkurencyjne zapisy, approval races i stare MVCC snapshoty.
- 66 przypadków NaN/±Infinity, które nie mogą wejść do obliczeń decimal.
- Identyczne zapytania mają jawne osobne powiązania z tym samym snapshotem; zmiana request revision nie zmienia historycznych referencji operacji.
- Zapis okresowych wartości decimal (Q=7) bez utraty skali w PostgreSQL.
- Endpoint dostępny w Development, wyłączony flagą oraz niedostępny w Staging/Production.

## Wynik rzeczywistej bazy demonstracyjnej

Dwukrotnie wykonano `--migrate --seed-golden --calculate-golden`. Potem rzeczywisty GET `/api/dev/golden/w07044` na lokalnym Kestrel zwrócił **HTTP 200** i te same hashe. Serwer użyty do smoke został zatrzymany; uruchomienie opisuje README.

Po powtórkach: 4 migracje, 8 operacji, 1 snapshot, 1 run, 8 wyników operacji, 4 zdarzenia audytu.

```text
engine_version: quote-engine-stage1-v1
snapshot_hash: 60288b739bc69acc39a94e1696412bd2ccd13724d39375223a5075fb3399cb85
calculation_hash: cd3f9117dfa7a5596802ad8620f629afa8ce8d97ab4c190c0a21abe52cde40de
```

Odczytane z bazy artefakty:

- [Canonical JSON, dokładne UTF-8 bez BOM i bez końcowego newline](golden/w07044-q150.canonical.json)
- [Canonical wynik Time+Stock](golden/w07044-q150.result.json)
- [Odpowiedź HTTP](golden/w07044-q150.response.json)

SHA-256 pliku canonical został dodatkowo potwierdzony niezależnym `Get-FileHash`.

## TODO / BLOCKED / zakres kolejnego etapu

1. **BLOCKED dla przyszłych kosztów:** stawki Tj/Tpz ośmiu zasobów i daty obowiązywania nie są dostarczone. W bazie nie utworzono stawek 0. Placeholder `rates-v1` jest jawnie oznaczony jako niewykorzystywany przez Time/Stock.
2. **TODO:** autor i rzeczywista data zatwierdzenia — NULL, ponieważ źródła ich nie podają.
3. **TODO:** potwierdzenie kompletności wymagań jakościowych; obecnie zapisano osiem pozycji arkusza. „Częściowo” pozostaje dosłownym tekstem i nullable boolean.
4. **TODO biznesowe:** reguła reakcji na potwierdzone `final_mass > norm_mass`; obecnie stosowany jest dokładnie wskazany wzór bez clampu.
5. **CI zdalne:** workflow jest dodany i zawiera wszystkie testy; nie wykonano GitHub Actions, bo nie podano ani nie skonfigurowano remote. Lokalnie sprawdzono restore locked-mode i to samo polecenie testowe.

Punkty 1–4 nie blokują zdefiniowanego etapu Time/Stock. Nie implementowano CostEngine, PricingEngine, geometrii STEP, AI Extractora ani email ingestion.

## Decyzje techniczne

Npgsql + wersjonowane SQL, .NET 8 z dopuszczeniem nowszego SDK, brak apphost EXE na dysku Google Drive, techniczna wersja routingu `1`, canonical decimal strings bez nieistotnych zer, TEXT+JSONB, content-addressed snapshot i junction requestów, source route ID poza payload, `NUMERIC` bez skali dla wyników, half-open rate periods, triggery/row locks/edit_version, endpoint zamiast ekranu frontendowego. Szczegóły i uzasadnienie są w ADR 001.

**Potwierdzenie: logika TimeEngine, StockEngine i Snapshot nie zawiera żadnych wywołań AI/LLM, losowości, zapytań DB ani odczytów zegara.**
