# AI Manufacturing Quote Engine

## Aktualny stan — 2026-10-05

Projekt został przeniesiony do GitHuba i zweryfikowany w GitHub Actions na PostgreSQL 16. Aktualny pełny przebieg CI: **790/790 testów zielonych** (638 unit + 152 integration, 0 failed, 0 skipped). Zaimplementowane są deterministyczne TimeEngineV1, StockEngineV1 i CostEngineV1, immutable canonical snapshots/replay, RFQ draft + wersjonowane pliki, CanonicalRFQ v1, AI Gateway, prompt-v1, OpenAI Responses provider oraz B069 — bounded retry tylko dla błędów TRANSIENT.

AI core jest zarejestrowany w Host/DI, ale domyślnie pozostaje wyłączony i **nie ma jeszcze endpointu ekstrakcji RFQ**. Finalny PricingEngine/margin policy pozostaje BLOCKED do czasu dostarczenia dokładnej polityki handlowej. Nie ma jeszcze kompletnego RFQ CRUD/customer workflow, auth/RLS, frontendu, STEP/geometry, QuoteVersion/approval/PDF/email ani actuals.

Pełny audyt i zalecana kolejność implementacji: [`docs/PROJECT_AUDIT_2026-10-05.md`](docs/PROJECT_AUDIT_2026-10-05.md).

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

B3.4 dodaje `RfqExtractionServiceV1` bez endpointu HTTP. Serwis przyjmuje wyłącznie jawnie wskazane pary `logical_key + version_no` oraz jawny `document_type`; nie wybiera automatycznie najnowszej wersji pliku.

Repozytorium **nie implementuje jeszcze parsera PDF/STEP/e-mail**. `IRfqExtractionSourceMaterializer` jest seamem dla takiego parsera, a domyślna implementacja hosta kończy próbę kodem `RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED`. Dzięki temu binarne pliki nie są arbitralnie konwertowane ani wysyłane do providera.

Po zmaterializowaniu jawnych źródeł serwis:
- buduje deterministyczny canonical JSON wejścia;
- wylicza request fingerprint;
- wykonuje wyłącznie przez `AiPolicyExecutorV1`;
- zapisuje model/prompt/schema/fingerprint/disposition/code jako append-only `audit_events`.

Dedykowane immutable tabele prób oraz trwały CanonicalRFQ draft należą do B3.5 i nie są tworzone w B3.4.

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

Etap udostępnia debug przez API/command oraz produkcyjne endpointy upload/download wersjonowanych plików RFQ. AI Extractor core (CanonicalRFQ, schema, prompt, provider, gateway i retry) jest zaimplementowany, przetestowany i zarejestrowany w Host/DI, ale nie jest jeszcze wystawiony jako endpoint. PricingEngine, parser STEP, frontend i email ingestion nie są jeszcze częścią działającego przepływu aplikacji.

## Migracje i seed

1. `001_core_master_data.sql`: tenants, parts, revisions, material, stock, machines, rate periods.
2. `002_routing_and_time.sql`: routes, operations, inspections, approval/version guards.
3. `003_snapshots_and_calculations.sql`: requests, immutable snapshots, request associations, runs, operation results.
4. `004_audit.sql`: append-only audit i triggery decyzji.
5. `005_cost_engine_v1.sql`: wersjonowane stawki kosztowe, jawny koszt materiału oraz pełnoprecyzyjne wyniki kosztowe operacji.
6. `006_rfq_file_versions.sql`: logiczne dokumenty RFQ i immutable wersje metadanych; baza nie przechowuje bajtów plików.
7. `007_rfq_draft_nullable_inputs.sql`: pozwala tworzyć RFQ w stanie draft bez rozpoznanej rewizji/ilości, ale wymaga tych danych przed przejściem do stanów kalkulacyjnych.

`DatabaseMigrator` wykonuje migracje transakcyjnie pod advisory lock. Zapamiętuje SHA-256 każdej migracji i odrzuca zmianę już zastosowanego pliku. Nowe wdrożone zmiany wymagają nowej migracji. `--migrate` i `--seed-golden` są osobnymi jawnymi akcjami; serwer nie zmienia schematu przy zwykłym starcie.

Seed jest oddzielnym plikiem SQL, obejmuje test tenant, osiem zasobów oraz wersję stawek `w07044-koszty-v1` z jawnym kosztem materiału. Ponowne uruchomienie nie aktualizuje Approved rows i nie powiela audytu. Wykryty konflikt istniejących danych golden kończy seed błędem.

Rollback zmian schematu w bazie zawierającej dane: odtworzenie przetestowanej kopii lub kolejna migracja naprawcza. Nie ma destrukcyjnego polecenia automatycznie cofającego immutable historię. Bazy testów są jednorazowe.

## TODO / dane do decyzji

- **BLOCKED PricingEngineV1:** brak dokładnej polityki handlowej i zasad precyzji dla cen 223.15 PLN oraz 210.15 PLN. CostEngine nie wylicza ceny sprzedaży.
- **TODO:** źródła nie podają autora i daty zatwierdzenia; pola pozostają NULL. Seed zachowuje zatwierdzone statusy wskazane w poleceniu.
- **TODO:** pełna weryfikacja wymagań jakościowych. Zaimportowano osiem pozycji 11A; „Częściowo” zachowano dosłownie, bez zgadywania wartości boolean. Nie dodano osobnych czasów kontroli; 0080 ma potwierdzone 0/0.
- **TODO biznesowe:** postępowanie przy potwierdzonej masie finalnej większej niż normatyw. Obecny silnik stosuje wskazany wzór, bez clampowania lub nowej reguły odrzucenia.
- **CI zdalne:** GitHub Actions jest aktywne; aktualny pełny przebieg na PostgreSQL 16 zakończył się wynikiem **790/790 PASS**.

Wynik i pełna lista plików znajdują się w `docs/verification-stage1.md` oraz `docs/changed-files.txt`.
