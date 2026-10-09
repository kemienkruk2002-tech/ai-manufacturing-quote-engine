# AI Manufacturing Quote Engine

## Aktualny stan — 2026-10-09

Zweryfikowany `main` po PR #36 to `ce00d06b15a81cbd8bec65ff25c89a089a6e14ad`: **936/936 testów zielonych** (678 unit + 258 integration, 0 failed, 0 skipped), [GitHub Actions 37970757483](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37970757483). Niezależny lokalny przebieg sprawdzonego PR na PostgreSQL 16.15 potwierdził ten sam wynik. Zaimplementowane są deterministyczne TimeEngineV1, StockEngineV1 i CostEngineV1, immutable canonical snapshots/replay, tenantowy RFQ draft create/read/list/update z optimistic concurrency, wersjonowane pliki i manifest, model/repozytoria customers/contacts, CanonicalRFQ v1 oraz AI Gateway, prompt-v1, OpenAI Responses provider i bounded retry dla błędów TRANSIENT.

Backend AI obejmuje trwałe próby ekstrakcji, bieżący CanonicalRFQ draft oraz review/readiness/history. **B3.H1 jest scalony**: audit, immutable attempt i opcjonalny draft są zapisywane atomowo. **B3.H2 jest nieukończony w istniejącym [PR #27](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/27)**. Audyt jego head `c50596e` wykazał niepodłączoną idempotencję, luki constraints oraz osiem brakujących przypadków integracyjnych; naprawy prowadzi [#29](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/29). Wynik tego audytowanego head to 683 unit + 224 integration = 907 PASS, co nie potwierdza ukończenia H2. Aktualne wyniki kolejnych napraw są w rejestrze stanu projektu.

Przywrócenie ośmiu przypadków z [PR #33](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/33) scalono wyłącznie do gałęzi PR #27 w commicie `c4ef0643379879e1fabcf7a458725ee5b9e817f8`. Naprawa `ea1e5957272df3f664861e4e4b6342b14e98c2bc` przeszła niezależny review QA oraz [CI 37966082743](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966082743): **683 unit + 232 integration = 915 PASS**, zero failed/skipped. Po scaleniu zielone są także [PR CI 37966270514](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966270514) i [push CI 37966262994](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966262994). W tamtym checkpointcie `main` był na `cc0aaca` z 910 PASS. Nowszy wynik podano poniżej; H2 nadal jest IN_PROGRESS.

Nowszy checkpoint: [PR #35](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/35) domyka constraints identity i tenant-audit; jest scalony do gałęzi PR #27 jako `f55c42d`, a [CI po scaleniu](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37970708382) potwierdza **939 PASS**. [PR #36](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/36) naprawia walidację RFQ i po jawnej zgodzie właściciela jest scalony do `main` jako `ce00d06`; jego review, lokalne testy i CI przed scaleniem potwierdziły **936 PASS**. Wynik [CI main po scaleniu](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37970757483) potwierdza **936 PASS**, zero failed/skipped; #31 jest zakończone. Kolejne małe zadanie do uruchomienia przez właściciela: [#37 — keyed persistence/replay](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/37). Spięcie serwisu nastąpi osobno; H2 pozostaje nieukończone.

AI pozostaje domyślnie wyłączone i **nie ma jeszcze endpointu ekstrakcji RFQ**. Istnieje tenant/auth boundary, ale produkcyjny provider tożsamości nie jest wybrany i Production pozostaje fail-closed; RLS nie jest zaimplementowane. RFQ lifecycle/readiness i zmiany po analizie wymagają decyzji biznesowych. Nie ma jeszcze customer/contact HTTP, produkcyjnego calculation/replay HTTP, frontendu, STEP/geometry, QuoteVersion/approval/PDF/email ani actuals. PricingEngine/margin policy pozostaje BLOCKED. B1.H3 jest częściowe: ochrona `main` pozostaje otwarta w [#8](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/8).

Bieżący rejestr i podział prac: [`PROJECT_STATUS`](docs/management/PROJECT_STATUS.md). Mapa implementacji: [audyt backendu z 2026-10-09](docs/audits/BACKEND_MODULE_AUDIT_2026-10-09.md). Kolejność i blokady: [plan backendu](docs/AUTONOMOUS_BACKEND_PLAN.md). [Audyt z 2026-10-05](docs/PROJECT_AUDIT_2026-10-05.md) jest historycznym punktem odniesienia.

Deterministyczny rdzeń W07044 / Tuleja 92687521_A: PostgreSQL, zatwierdzony routing, TimeEngineV1, StockEngineV1, CostEngineV1, immutable snapshot oraz wersjonowane metadane plików RFQ z lokalnym content-addressed store SHA-256. ASP.NET Core **.NET 8**, xUnit, Npgsql. Silniki domenowe nie korzystają z AI, sieci, bazy, zegara ani losowości.

## Szybki start — Windows

Z katalogu projektu, w PowerShell:

```powershell
# Start izolowanego PostgreSQL i wszystkie testy (również integracyjne).
.\scripts\test.ps1 -DatabaseMode Portable

# Przygotowanie bazy demonstracyjnej; polecenie można powtarzać.
$env:ConnectionStrings__QuoteEngine = .\scripts\start-test-db.ps1 -Mode Portable
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project services/api/Host/QuoteEngine.Api.csproj -- --migrate --seed-golden --calculate-golden

# Serwer developerski na localhost.
dotnet run --project services/api/Host/QuoteEngine.Api.csproj -- --urls http://127.0.0.1:5080
```

Otwórz `http://127.0.0.1:5080/api/dev/golden/w07044`. Endpoint zwraca dane detalu, materiał, półfabrykat, 8 operacji, wymagania kontroli, wyniki czasu, materiału i kosztu oraz hashe. `calculatedCostResult` zachowuje pełną precyzję, a `calculatedCostDisplay` stosuje prezentacyjne zaokrąglenie do dwóch miejsc. `Dev__GoldenEnabled=false` wyłącza endpoint. W Production i Staging endpoint nie jest rejestrowany. Polecenia golden seed/calculate także wymagają Development.

Portable PostgreSQL jest przechowywany poza repozytorium, w `%LOCALAPPDATA%\Codex\manufacturing-quote-test-postgres`, nasłuchuje wyłącznie na `127.0.0.1:55432`. Skrypt pobiera binarki EDB przy pierwszym uruchomieniu. Zatrzymanie: `.\scripts\stop-test-db.ps1 -Mode Portable`. Połączenie zwracane przez skrypt służy wyłącznie lokalnym testom i demo.

## Konfiguracja AI

AI jest domyślnie wyłączone. W stanie wyłączonym `AiGatewayV1` kończy wykonanie kodem providera `AI_DISABLED` i nie wykonuje requestu sieciowego.

Aby włączyć provider OpenAI, konfiguracja serwerowa musi zawierać:

```text
Ai__Enabled=true
Ai__ApiKey=<server-side secret>
Ai__BaseUrl=https://api.openai.com/
```

`Ai__ApiKey` nie należy do repozytorium ani logów; należy dostarczyć go przez zmienną środowiskową lub system sekretów. `Ai__RetryDelaysMs` jest opcjonalną tablicą opóźnień w milisekundach. Brak wartości oznacza brak ponowień — repo nie narzuca niewymaganej polityki retry. Przy `Ai__Enabled=true` konfiguracja jest walidowana przy starcie: wymagany jest sekret, absolutny adres HTTPS i nieujemne opóźnienia retry.

### Polityka wykonania zewnętrznego AI

Samo `Ai__Enabled=true` nie zezwala na wysyłkę danych. Warstwa B3.3 jest **default-deny** i wymaga jednocześnie:

```text
Ai__Policy__PermittedUseCases__0=<explicit use case>
Ai__Policy__PermittedModels__0=<explicit model id>
Ai__Policy__PermittedDocumentTypes__0=<explicit document type>
Ai__Policy__MaxPayloadBytes=<explicit positive byte limit>
```

Każde wywołanie musi dodatkowo przekazać `allow_external_ai=true`. Puste allowlisty albo `MaxPayloadBytes=0` nie powodują błędu startu aplikacji — oznaczają brak zezwolenia na wykonanie zewnętrznego AI.

Domyślna implementacja `IAiInputRedactor` blokuje wykonanie kodem `AI_REDACTION_NOT_CONFIGURED`. Produkcyjne wdrożenie musi jawnie podmienić ją na zatwierdzoną implementację redakcji; repozytorium nie zgaduje reguł PII/sekretów ani nie przepuszcza danych bez redakcji.

Każde odrzucenie polityki, redakcji lub providera kończy się `REVIEW_MANUAL`; nie ma automatycznego fallbacku tworzącego dane zastępcze.

### RFQ extraction service

`RfqExtractionServiceV1` (B3.4) działa jako wewnętrzny serwis bez endpointu HTTP. Serwis przyjmuje wyłącznie jawnie wskazane pary `logical_key + version_no` oraz jawny `document_type`; nie wybiera automatycznie najnowszej wersji pliku.

Repozytorium **nie implementuje jeszcze parsera PDF/STEP/e-mail**. `IRfqExtractionSourceMaterializer` jest seamem dla takiego parsera, a domyślna implementacja hosta kończy próbę kodem `RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED`. Dzięki temu binarne pliki nie są arbitralnie konwertowane ani wysyłane do providera.

Po zmaterializowaniu jawnych źródeł serwis:

- buduje deterministyczny canonical JSON wejścia;
- wylicza request fingerprint;
- wykonuje wyłącznie przez `AiPolicyExecutorV1`;
- zapisuje model/prompt/schema/fingerprint/disposition/code, immutable attempt, raw output i source lineage; dla poprawnego wyniku COMPLETED aktualizuje także walidowany CanonicalRFQ draft.

Dedykowane tabele prób i bieżącego draftu są zaimplementowane w B3.5 (migracja 010), a B3.H1 łączy audit + attempt + opcjonalny draft w jedną transakcję. B3.6 udostępnia tenantowe endpointy odczytu draftu, review, readiness i historii review. Readiness raportuje blokery; nie definiuje biznesowej krytyczności pól ani nie zmienia statusu RFQ. Idempotencja wykonania pozostaje B3.H2; dopiero po niej B3.H3 dodaje endpoint ekstrakcji, a B3.H4 uzupełnia OpenAPI także dla istniejących tras B2.

### FreeLLMAPI

Adapter ani wybór providera FreeLLMAPI nie są zaimplementowane. [#30](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/30) pozostaje `BLOCKED_PROVIDER_IDENTITY`: nazwa usługi nie potwierdza instancji, bazowego URL ani dostępnego modelu użytkownika. Nie wykonano testów LIVE. Ich uruchomienie wymaga również uzgodnionego zakresu i budżetu. [Audyt providera](docs/audits/FREELLMAPI_AUDIT_2026-10-09.md) opisuje fakty, niewiadome i osobne zadania mock/LIVE; obecny OpenAI, output guard oraz default-deny pozostają obowiązujące.

## Docker / Linux / CI

```sh
docker compose up -d --wait postgres
export QUOTEENGINE_TEST_DATABASE='Host=127.0.0.1;Port=55432;Database=manufacturing_quote_test;Username=quote_test;Password=quote_test_local_only'
dotnet restore QuoteEngine.sln --locked-mode
dotnet test QuoteEngine.sln --configuration Release --no-restore
```

Same testy domeny, bez PostgreSQL:

```sh
dotnet test services/api/Tests/Unit/QuoteEngine.UnitTests.csproj --configuration Release
```

Testy integracyjne wymagają `QUOTEENGINE_TEST_DATABASE` i prawa CREATE DATABASE. Każdy przebieg tworzy własną bazę `quoteengine_it_<id>`, migruje ją od zera i usuwa po teście. Nie czyszczą wskazanej bazy bazowej. Brak połączenia jest błędem testów, a nie pominięciem. CI w `.github/workflows/ci.yml` uruchamia pełny zestaw z PostgreSQL 16 i zapisuje TRX; nie wymaga usług ani kluczy AI.

## Oczekiwany wynik Q=150

| Wartość | Wynik |
| --- | ---: |
| ΣTj | 1375 s/szt. |
| ΣTpz | 230 min/partię |
| Tpz na sztukę | 92 s |
| Pracochłonność na sztukę | 1467 s |
| Pracochłonność partii | 61.125 h |
| Masa finalna | 3.540 kg |
| Normatyw | 5.298 kg/szt. |
| Wykorzystanie materiału | 3.540 / 5.298 |
| Koszt Tj | 87.25 PLN |
| Koszt Tpz | 4.02 PLN |
| Koszt pracy | 91.27 PLN |
| Koszt materiału | 14.94 PLN |
| Koszt całkowity | 106.21 PLN |

Silniki używają `decimal`, bez zaokrągleń do dwóch miejsc. Wartości godzin z tabeli źródłowej są przybliżeniami; test dziewięciu ilości używa tolerancji `1e-9`. Dodatkowy test obejmuje każdą ilość 1–500. Dla dzielenia okresowego dopuszczalny jest wyłącznie ostatni błąd reprezentacji `decimal`.

## Struktura

- `services/api/Domain` — encje, enumy, walidacja, silniki i canonical JSON, bez zależności zewnętrznych.
- `services/api/Application` — interfejsy repozytoriów, przygotowanie kalkulacji, odtwarzanie, logi.
- `services/api/Persistence` — Npgsql, migracje SQL, seed i zapis wyników.
- `services/api/Host` — ASP.NET Core, polecenia developerskie i endpoint.
- `services/api/Tests/Unit` — golden, macierz, canonical, repeatability, routing, stock, stawki.
- `services/api/Tests/Integration` — prawdziwy PostgreSQL, migracje, seed, constraints, równoległość, tenant isolation, API i historyczny replay.
- `docs/sources` — tekst odczytanych źródeł 02, 11, 11A wraz z adresem i datą wersji.
- `docs/adr/001-stage1.md` — decyzje techniczne, format snapshotu i ograniczenia.

Host udostępnia tenantowe RFQ draft create/read/list/update, upload/download i manifest plików oraz canonical draft/review/readiness/history. OpenAPI opisuje obecnie tylko health oraz upload/download; uzupełnienie pozostałych tras należy do B3.H4. AI extraction jest zarejestrowane w Host/DI, lecz nie ma endpointu wykonania. PricingEngine, parser STEP, frontend i email ingestion nie są częścią działającego przepływu aplikacji.

## Migracje i seed

1. `001_core_master_data.sql`: tenants, parts, revisions, material, stock, machines, rate periods.
2. `002_routing_and_time.sql`: routes, operations, inspections, approval/version guards.
3. `003_snapshots_and_calculations.sql`: requests, immutable snapshots, request associations, runs, operation results.
4. `004_audit.sql`: append-only audit i triggery decyzji.
5. `005_cost_engine_v1.sql`: wersjonowane stawki kosztowe, jawny koszt materiału oraz pełnoprecyzyjne wyniki kosztowe operacji.
6. `006_rfq_file_versions.sql`: logiczne dokumenty RFQ i immutable wersje metadanych; baza nie przechowuje bajtów plików.
7. `007_rfq_draft_nullable_inputs.sql`: pozwala tworzyć RFQ w stanie draft bez rozpoznanej rewizji/ilości, ale wymaga tych danych przed przejściem do stanów kalkulacyjnych.
8. `008_customers_contacts.sql`: tenantowe customers/contacts oraz opcjonalne powiązanie RFQ z klientem.
9. `009_rfq_draft_concurrency.sql`: dodatni `row_version` dla optymistycznej kontroli współbieżności draftu.
10. `010_rfq_extraction_history.sql`: immutable extraction attempts i bieżący CanonicalRFQ draft z powiązaniem attempt w obrębie tego samego tenant/RFQ.

To komplet 10 migracji na zweryfikowanym `main`; dodatkowa migracja 012 z nieukończonego PR #27 nie jest częścią tej bazy.

`DatabaseMigrator` wykonuje migracje transakcyjnie pod advisory lock. Zapamiętuje SHA-256 każdej migracji i odrzuca zmianę już zastosowanego pliku. Nowe wdrożone zmiany wymagają nowej migracji. `--migrate` i `--seed-golden` są osobnymi jawnymi akcjami; serwer nie zmienia schematu przy zwykłym starcie.

Seed jest oddzielnym plikiem SQL, obejmuje test tenant, osiem zasobów oraz wersję stawek `w07044-koszty-v1` z jawnym kosztem materiału. Ponowne uruchomienie nie aktualizuje Approved rows i nie powiela audytu. Wykryty konflikt istniejących danych golden kończy seed błędem.

Rollback zmian schematu w bazie zawierającej dane: odtworzenie przetestowanej kopii lub kolejna migracja naprawcza. Nie ma destrukcyjnego polecenia automatycznie cofającego immutable historię. Bazy testów są jednorazowe.

## TODO / dane do decyzji

- **BLOCKED PricingEngineV1:** brak dokładnej polityki handlowej i zasad precyzji dla cen 223.15 PLN oraz 210.15 PLN. CostEngine nie wylicza ceny sprzedaży.
- **TODO:** źródła nie podają autora i daty zatwierdzenia; pola pozostają NULL. Seed zachowuje zatwierdzone statusy wskazane w poleceniu.
- **TODO:** pełna weryfikacja wymagań jakościowych. Zaimportowano osiem pozycji 11A; „Częściowo” zachowano dosłownie, bez zgadywania wartości boolean. Nie dodano osobnych czasów kontroli; 0080 ma potwierdzone 0/0.
- **TODO biznesowe:** postępowanie przy potwierdzonej masie finalnej większej niż normatyw. Obecny silnik stosuje wskazany wzór, bez clampowania lub nowej reguły odrzucenia.
- **CI zdalne:** zweryfikowany `main` `ce00d06` ma **936/936 PASS**; wynik nie oznacza ukończenia otwartego PR #27 ani produkcyjnych blokad.

Wynik i pełna lista plików znajdują się w `docs/verification-stage1.md` oraz `docs/changed-files.txt`.
