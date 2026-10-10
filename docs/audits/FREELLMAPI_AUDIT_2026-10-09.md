# AI ENGINEER — audyt FreeLLMAPI, 2026-10-09

Zakres: read-only kodu na `cc0aaca8ec357506c63d82eeccd6271a90f6d1e2`, branch `agent/ai/issue-28-provider-audit`, oraz oficjalne źródła publiczne. Nie zmieniono kodu, nie wywołano żadnego API modeli, nie uruchomiono LIVE, nie odczytano wartości sekretów. Raport roboczy dla ASTRY; publikację ustaleń w Issue/PR prowadzi ASTRA.

## Wniosek

Abstrakcja `IAiStructuredProvider` pozwala dodać adapter bez przebudowy gatewaya i silników domenowych. Obecnie działa implementacja OpenAI Responses oraz retry i walidacja wyniku; nie istnieje wybór `AI_PROVIDER` ani obsługa `FREELLMAPI_*`.

Tożsamość FreeLLMAPI posiadanego przez użytkownika NIE jest potwierdzona. Znaleziony oficjalny projekt to self-hosted router `freellmapi.co` → `tashfeenahmed/freellmapi`. Nie ma dowodu, że posiadany klucz należy do instancji tego projektu; domena serwisu informacyjnego nie jest automatycznie bazą API użytkownika. To blokuje finalizację kontraktu konkretnego adaptera i wszelkie LIVE. Bezpieczne mocki i dokumentacja konfiguracji mogą być przygotowane niezależnie po uzgodnieniu kontraktu z ASTRĄ.

## Zweryfikowane źródła

1. [Oficjalna witryna](https://freellmapi.co/) odsyła do repozytorium poniżej i opisuje produkt jako self-hosted router. Samo posiadanie nazwy i klucza nie wskazuje konkretnej instancji.
2. [Oficjalne README](https://github.com/tashfeenahmed/freellmapi/blob/main/README.md) potwierdza `/v1/chat/completions`, `/v1/responses`, `/v1/models` i przekazywanie structured-output parametrów. Projekt opisano jako środowisko eksperymentów; dostępność modeli zależy od konfiguracji i usług upstream. W raporcie nie przyjmujemy reklamowanej liczby modeli jako listy dostępnej dla użytkownika.
3. [API reference](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md): przykład bazy to `http://localhost:3001/v1`, autoryzacja Bearer unified key, chat completions oraz katalog `GET /v1/models`. Katalog może zawierać modele bez dostępnego klucza; `?execution_status=ready` zawęża wynik. Są aliasy routingu i fallback, więc katalog nie gwarantuje konkretnej ścieżki wykonania. Dokumentacja wymienia `X-Routed-Via` jako informację o rzeczywistym providerze/modelu. Te fakty dotyczą opisanego upstream projektu, nie zweryfikowanej instancji użytkownika.
4. [Kod Responses shim](https://github.com/tashfeenahmed/freellmapi/blob/main/server/src/routes/responses.ts) akceptuje `text.format.type=json_schema` i przekłada `name`, `strict`, `schema` na wewnętrzny `response_format`. To potwierdza format wejścia, nie gwarancję poprawności każdego modelu.
5. [Adapter Google](https://github.com/tashfeenahmed/freellmapi/blob/main/server/src/providers/google.ts) przekłada JSON Schema na format Gemini przez `sanitizeForGemini`. Wniosek techniczny: sama zgodność transportu OpenAI nie dowodzi jednakowej obsługi pełnego schematu RFQ. Potrzebny test konkretnego modelu i wersji instancji; lokalny guard jest obowiązkowy.
6. [API domain overview](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/OVERVIEW.md) opisuje idempotencję jako osobny mechanizm dla non-streaming chat completions; wskazuje brak deduplikacji równoległych żądań. Nie wolno uznać routera za zastępstwo idempotencji aplikacji.

Nie potwierdzono: URL instancji użytkownika, jej wersji, modelu i jego dostępności, ścisłego Structured Outputs dla pełnego schematu RFQ, zasad retencji/dopuszczonych upstreamów, ustawień fallback/compression, budżetu LIVE.

Sprawdzenie wyłącznie obecności zmiennych w bieżącym procesie:

| Zmienna | Obecna i niepusta |
|---|---|
| `FREELLMAPI_API_KEY` | false |
| `FREELLMAPI_BASE_URL` | false |
| `FREELLMAPI_MODEL` | false |

Nie sprawdzano innych magazynów sekretów ani wartości konfiguracji użytkownika.

## Dokładne punkty rozszerzenia

Ścieżki względne względem repozytorium `ai-manufacturing-quote-engine`, commit powyżej:

| Plik / linia | Obecny kontrakt i minimalna zmiana |
|---|---|
| `services/api/Application/Ai/AiGatewayV1.cs:5` | `IAiStructuredProvider.ExecuteAsync(AiStructuredRequest, CancellationToken)` zachować. `AiProviderResult` już rozróżnia transient/permanent/cancelled. |
| `services/api/Application/Ai/AiGatewayV1.cs:77` | Każda odpowiedź przechodzi przez `RfqExtractorOutputGuardV1`; żadnego obejścia przy FreeLLMAPI. |
| `services/api/Application/Ai/OpenAiResponsesProviderV1.cs:31` | Wspólne źródło promptu to `RfqExtractorPromptV1.Compile`; wykorzystać je także w nowym adapterze. |
| `services/api/Application/Ai/OpenAiResponsesProviderV1.cs:36` | Obecny payload Responses zawiera `text.format`, `strict=true`, schemat, `store=false`. Zachować zachowanie OpenAI i jego testy. |
| `services/api/Application/Ai/OpenAiResponsesProviderV1.cs:56` | Obecny adapter dokleja `v1/responses`. Nie podłączać do niego bezmyślnie bazy kończącej się `/v1/`, bo powstanie `/v1/v1/responses`. Jawnie określić semantykę BASE_URL i przetestować URI. |
| `services/api/Host/AiServiceRegistration.cs:7` | Opcje są dziś ogólne `Ai:Enabled/BaseUrl/ApiKey/RetryDelaysMs`, z domyślnym endpointem OpenAI. Dodać odrębny provider/config; FreeLLMAPI bez domyślnego URL/modelu i bez fallbacku klucza do `Ai:ApiKey`. |
| `services/api/Host/AiServiceRegistration.cs:60` | Dodać osobny named HttpClient dla FreeLLMAPI; Bearer wyłącznie z `FREELLMAPI_API_KEY`. Nie ujawniać headerów ani body błędów. |
| `services/api/Host/AiServiceRegistration.cs:83` | Retry dekoruje obecnie konkretny OpenAI provider. Wybrać jawnie skonfigurowany provider przed owinięciem wspólnym retry. Nie przełączać automatycznie dostawców po błędzie. |
| `services/api/Host/AiServiceRegistration.cs:88` | Zachować `Ai:Enabled=false` i `DisabledAiStructuredProvider`; provider selection nie może odblokować AI samą obecnością klucza. |
| `services/api/Host/AiServiceRegistration.cs:93` | Redaktor i materializer blokują domyślnie. Nie usuwać blokad dla testów LIVE; dla syntetycznych fixture użyć jawnej konfiguracji testowej. |
| `services/api/Application/Ai/RfqExtractionServiceV1.cs:74` | Model requestu trafia do fingerprintu i persystencji. Nie nadpisywać modelu w adapterze po utworzeniu fingerprintu; wymagać zgodności z `FREELLMAPI_MODEL` albo wybrać go przed wejściem do usługi. |
| `services/api/Application/Ai/AiStructuredBoundary.cs` | Fingerprint v1 obejmuje UseCase, ModelId, PromptVersion, SchemaVersion i canonical input, lecz nie provider/base URL. Nie zmieniać istniejącego kontraktu hash po cichu; rozstrzygnąć kolizje providerów/model IDs przed zastosowaniem w trwałej ekstrakcji. |
| `services/api/Domain/Quoting/RfqExtractorSchemaV1.cs` i `rfq-extractor-v1.schema.json` | Użyć obecnego wersjonowanego schematu bez osłabiania dla modelu. |
| `services/api/Tests/Integration/AiServiceRegistrationTests.cs` | Rozszerzyć testy konfiguracji przy zachowaniu wszystkich istniejących testów OpenAI/default-deny. |
| `.github/workflows/ci.yml` | Obecnie jedyny workflow. LIVE dodać jako oddzielny optional manual workflow, nie warunek wymaganych deterministycznych checks. |

## Plan dwóch małych zadań

### TASK A — adapter i wybór dostawcy, mock-only

AGENT: AI ENGINEER. REVIEWER: SECURITY & QA, ASTRA.

CEL: zachować OpenAI i kontrakt `IAiStructuredProvider`, dodać jawnie skonfigurowany adapter FreeLLMAPI ze sprawdzonym protokołem oraz wybór `AI_PROVIDER`.

ZALEŻNOŚCI: potwierdzenie, jaka usługa/instancja kryje się pod nazwą FreeLLMAPI; decyzja ASTRY o formacie bazy i modelu. Do czasu potwierdzenia nie wpisywać przykładów jako prawdziwego endpointu użytkownika. Jeżeli jest to udokumentowany router, minimalny wariant może korzystać z Responses shim lub chat completions; wybrać jeden zweryfikowany protokół, nie automatyczny fallback między nimi.

DOZWOLONE: nowy adapter w Application/Ai, wyodrębnione opcje i DI w Host, odpowiednie unit/integration tests, dokumentacja konfiguracji. ZAKAZANE: zmiany obliczeń, schematu RFQ, polityk biznesowych, istniejących hashy, migracji, allowlist/redakcji, usuwanie testów, LIVE.

TESTY: dokładny endpoint i payload; model zgodny z konfiguracją; Bearer i brak sekretów w błędach; brak config → startup failure tylko gdy selected/enabled; disabled → zero network; unknown provider → błąd konfiguracji; brak przejęcia klucza OpenAI; malformed/empty/refusal/truncated output → failure/manual review; nieprawidłowy schemat → guard; 400/401/403 permanent; 408/5xx/transport transient; 429 z rozróżnieniem zwykłego limitu i wyczerpanej quota; bounded retry, cancellation; regresja istniejącego OpenAI i idempotencji.

UWAGA: `AiProviderResult` nie przenosi obecnie Retry-After, a retry decorator stosuje wyłącznie stałą listę opóźnień. Nie deklarować obsługi providerowego Retry-After bez implementacji i testów. Szczególnie quota-exhausted 429 nie powinno powodować krótkiej pętli powtórzeń. Osobno ustalić minimalny bezpieczny kontrakt, bez obchodzenia limitów.

DONE: mocki i wszystkie wymagane regresje przechodzą; osobny mały PR z dokumentacją; review potwierdza brak ujawniania sekretów i brak osłabienia ADR 002. Merge tylko po zgodzie Ownera.

### TASK B — opcjonalny workflow LIVE na syntetycznych RFQ

AGENT: DEVOPS + QA (jednoznaczny właściciel workflow: DEVOPS). REVIEWER: SECURITY, AI ENGINEER, ASTRA.

CEL: manualny workflow publikujący metryki poprawności i latencji bez poufnych danych; domyślne CI działa bez sekretu.

ZALEŻNOŚCI: TASK A, zweryfikowana HTTPS instancja/model, klucz w `secrets.FREELLMAPI_API_KEY`, zatwierdzony zakres/budżet: liczba prób i limit tokenów/wywołań oraz upstream/fallback. Obecny brak budżetu blokuje uruchomienie, nie przygotowanie workflow.

DOZWOLONE: oddzielny `workflow_dispatch`, syntetyczne fixtures i test harness, raport testu. ZAKAZANE: pull_request_target, udostępnienie sekretu kodowi niezaufanego PR, automatyczne uruchamianie z forków, pliki klientów, automatyczne pobieranie URL/modelu, instalacja/prowizjonowanie providera, realne żądania przy brakujących inputach.

TESTY: missing secret/URL/model → wyraźny SKIP i zero network; jawny opt-in; syntetyczny RFQ i assertion obowiązującego schematu; odpowiedź z brakującymi danymi nie tworzy danych fikcyjnych; metryki liczby żądań, latencji, schema pass, błędów i retry; limit czasu/concurrency; bounded calls. Payloady/klucz/niesanitowane błędy nie trafiają do logów ani artifacts. Wyniki mają wskazywać commit, wersję fixture/schema/promptu, skonfigurowany model i bezpiecznie odnotowaną ścieżkę wykonania. SKIP nie jest PASS modelu.

DONE: dry run bez sekretu dokumentuje SKIP; przechodzą mocki test harness; dopiero po zatwierdzonym zakresie ręczny run ma link do Actions i raport, niezależne review. Nie uznać samego wygenerowania JSON za zatwierdzenie danych produkcyjnych.

## Blokady i ryzyka do rejestru ASTRY

- `BLOCKED_PROVIDER_IDENTITY`: oficjalny URL dokumentacji/dashboardu i zweryfikowana instancja użytkownika. Nie prosić o wartość klucza.
- `BLOCKED_LIVE_SCOPE`: brak zatwierdzonej liczby żądań/tokenów i retry; nie uruchomiono LIVE.
- Brak konfiguracji w bieżącym procesie (trzy booleany false).
- `BLOCKED_BUSINESS_DECISION`: brak zatwierdzonych allowlist, redakcji i polityki dla prawdziwych dokumentów; jest to już świadoma blokada ADR 002. Nie dotyczy izolowanych mocków.
- Router może zmieniać model/upstream i kompresować prompt; dla audytowalnego testu potrzebna jawna konfiguracja instancji. Publiczny katalog nie dowodzi wykonania na wybranym modelu.
- Fingerprint/audit zapisuje ModelId, nie provider. Ograniczyć pierwszy etap do testowego adaptera i nie zmieniać kontraktu persystencji bez uzgodnienia.
- Aktualne ograniczenie Host wymaga HTTPS; przykład lokalnego HTTP z docs upstream nie jest powodem do samodzielnego osłabienia tej walidacji.

Testy lokalne nie były uruchamiane w tym zadaniu read-only; audyt istniejących testów to przegląd ich kontraktów. Worktree pozostał bez zmian.
