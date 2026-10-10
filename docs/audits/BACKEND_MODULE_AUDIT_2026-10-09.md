# Audyt mapy backendu — issue #28

Autor: BACKEND / DATABASE ENGINEER. Data: 2026-10-09.
Zakres: statyczna inspekcja `main` = `cc0aaca8ec357506c63d82eeccd6271a90f6d1e2`, checkout `work/audit-backend`, branch `agent/backend/issue-28-module-audit`.

Repozytorium pozostało niezmienione; `git status --short` był pusty. Nie uruchamiano AI, nie zmieniano GitHuba, usług ani polityk biznesowych. Wyniki uruchomienia baseline testów dostarcza ASTRA; poniższe oznaczenia `actual` nie oznaczają nowego wykonania CI. Odczytano README, AUTONOMOUS_BACKEND_PLAN, DEVELOPMENT_STATE, PROJECT_AUDIT oraz audyty B1/B2/B3 i porównano z kodem, trasami, migracjami i testami.

## Wniosek

Działa deterministyczny rdzeń wyceny kosztu, przechowywanie/replay snapshotów, tenantowy RFQ draft CRUD, historia plików i backend ekstrakcji/review. Nie istnieje jeszcze spójny produkcyjny przepływ upload → extraction HTTP → kalkulacja HTTP → cena → zatwierdzona oferta. B3.H1 jest już zaimplementowany. B3.H2, H3 i H4 nadal pozostają pracą do wykonania. B4/B5/B6 nie są zakończone, więc bramka frontendu pozostaje zamknięta.

Legenda: `actual` = implementacja w zdefiniowanym zakresie; `partial` = część warstw działa; `missing` = brak implementacji; `blocked` = wymaga jawnej decyzji/zasobu. Wzmianki o migracjach oznaczają pliki w `services/api/Persistence/Migrations/`.

## Mapa modułów

| Moduł | Stan | Dowód w kodzie/testach | Zależność / brak / blokada |
|---|---|---|---|
| TimeEngineV1 / StockEngineV1 / CostEngineV1 | actual, frozen | `services/api/Domain/Calculation/{TimeEngineV1,StockEngineV1,CostEngineV1}.cs`; GoldenW07044Tests, quantity matrix, RepeatabilityTests, CostEngineTests | Nie modyfikować bez konkretnego błędu lub wersjonowanej reguły. Koszt nie jest ceną sprzedaży. |
| Canonical snapshot/hash/historical replay | actual, frozen | `Domain/Quoting/CanonicalSnapshotSerializer.cs`, `CanonicalCostSnapshotSerializer.cs`; `Application/CalculationService.cs:61`; snapshot/run repositories; migracje 003/005 | Replay odtwarza zapisane bajty, waliduje hash/canonical form/version. Brak produkcyjnego replay HTTP (B5.3). |
| Master data: parts, revisions, materials, stock, machines/rates, approved routing | actual model/persistence; partial product API | `Domain/{Parts,Materials,Machines,Routing}`; `Persistence/SnapshotInputRepository.cs`; migracje 001/002/005 | Głównie seed + odczyt na potrzeby kalkulacji; brak kompletnego HTTP authoring/master-data workflow. Nie utożsamiać z B8 AI technology proposal. |
| Migracje, immutable history, audit | actual | `Persistence/DatabaseMigrator.cs`; migracje 001–010; constraints/concurrency tests | 10 migracji, 24 tabele aplikacyjne + `schema_migrations`; checksum/advisory lock/transaction. Nowe zmiany wyłącznie nową migracją. |
| B1 ProblemDetails / correlation | actual foundation, partial coverage nowych RFQ validation paths | `Host/ApiExceptionHandler.cs`, `Program.cs:28`; ApiProblemDetailsTests | Zidentyfikowany klientowski input → 500 opisany niżej. |
| B1 tenant/auth seam | actual boundary; production blocked | `Host/Program.cs:19–26,57`, `ApiAuthentication.cs`, `ClaimsTenantContext.cs`, `Application/Tenancy.cs` | Production uses Closed scheme; provider/issuer/audience/protocol/tenant mapping nadal niezdefiniowane. Development header tylko w Development. |
| DB tenant isolation / RLS | actual composite FK + scoped SQL; RLS missing | Migracje + wszystkie przeglądane repozytoria używają tenant_id; brak CREATE POLICY/ROW LEVEL SECURITY | RLS strategy zależy od docelowego deployment/security design. FK nie jest RLS. |
| B1 OpenAPI | partial | `Host/ApiOpenApiDocumentV1.cs:16,42,66,80`; OpenApiEndpointTests | Tylko health + upload + download. 9 z 11 aktualnych tenant HTTP operations pominięte. Standard security scheme blocked przez auth decision. |
| B2.1 customers/contacts | actual model/repository; HTTP missing | `Domain/Customers/Customers.cs`, `Persistence/CustomerRepository.cs`, migracja 008; CustomerRepositoryTests | Create/find customer; create/find/list contact; brak customer/contact HTTP routes. Brak CRM policy jest celowym ograniczeniem pól. |
| B2.2 RFQ create/get/list | actual | `Host/Program.cs:58–71`, `Persistence/QuoteRequestRepository.cs`; RfqCreateApiTests, QuoteRequestRepositoryTests | Create tylko New. List filtruje status/customer/due-date; bez paginacji. Customer/part są nullable dla draftu. |
| B2.3 RFQ state machine | blocked | `Domain/Quoting/QuoteRecords.cs:6`; migracja 007 | Istnieje enum + constraint progressed inputs; nie ma transition graph, transition service/endpoint ani pełnej readiness policy. Wymaga biznesowego workflow. |
| B2.4a draft edits + optimistic concurrency | actual | `Program.cs:72–78`; `QuoteRequestRepository.cs:24–30`; migracja 009; simultaneous-writers test | Edycja tylko New i `row_version=@expected`. |
| B2.4b post-analysis revisions | blocked | Brak implementacji; plan jawnie blokuje | Zależy od B2.3: granica rozpoczęcia analizy/lock/revision. |
| B2.5 RFQ file versions/manifest | actual | `RfqFileRepository.cs`, `RfqFileManifestRepository.cs`, `Program.cs:79–88`, migracja 006; RfqFile*Tests | Exact versions, metadata SHA256, append-only. Zależność: local store + jawna konfiguracja upload policy. |
| Local object store | actual dev/test; partial production | `Persistence/LocalFileObjectStore.cs:21–105`; LocalFileObjectStoreTests | Hash verification przy zapisie, completed-temp-file rename i in-process striped gates. Brak read integrity verification i multi-process/provider hardening; Host tworzy store bezpośrednio. |
| B3.1 AI DI/config/provider/retry | actual, disabled by default | `Host/AiServiceRegistration.cs`; `Application/Ai/{OpenAiResponsesProviderV1,RetryingAiStructuredProviderV1,AiGatewayV1}.cs`; registration/provider tests | Disabled => AI_DISABLED. Nie uruchamiano sieci/providerów w audycie. |
| B3.2 normalization + fingerprint + CanonicalRFQ schema/guard/prompt | actual | `Application/Ai/AiStructuredBoundary.cs`, `RfqExtractorPromptV1.cs`; `Domain/Quoting/{CanonicalRfqV1,RfqExtractorSchemaV1}.cs`, JSON schema | Project-specific deterministic JSON normalization; nie należy deklarować pełnej JCS zgodności. CanonicalRFQ v1 frozen. |
| B3.3 execution policy | actual fail-closed; production blocked | `Application/Ai/AiExecutionPolicyV1.cs`; `AiServiceRegistration.cs:94–112,135–141` | allow_external_ai + allowlists + positive payload limit; domyślny redactor blokuje. B3.B1 wymaga zatwierdzonych reguł redakcji. |
| B3.4 extraction service | actual internal service; HTTP missing | `Application/Ai/RfqExtractionServiceV1.cs`; service tests | Jawny model + exact logical_key/version_no + document_type; brak automatycznego latest. Materializer hosta blokuje; B3.B2 wymaga formatów/parserów. |
| B3.5 attempts/current draft | actual | `Persistence/RfqExtractionExecutionRepository.cs`, migracja 010; RfqExtractionHistoryRepositoryTests | Raw provider output i immutable attempts osobno od walidowanego current draft. Composite FK wymusza ten sam tenant/RFQ. ListAttempts jest repository API, nie HTTP. |
| B3.6 review/confirmation/readiness/history | actual backend; lifecycle integration blocked | `Application/Ai/RfqCanonicalReviewServiceV1.cs`, `Persistence/RfqCanonicalReviewRepository.cs`, `Program.cs:89–137`; review tests | Whole-fact CONFIRM/REJECT/CORRECT, guarded correction, expected row version, atomic audit; actor z claims. Readiness nie zmienia statusu RFQ i nie definiuje krytycznych pól biznesowych. |
| B3.H1 atomic extraction persistence | actual | `RfqExtractionExecutionRepository.cs:15–119`, service SaveAtomicAsync; `RfqExtractionExecutionRepositoryTests.cs:23,69,112` | Jeden transaction dla execution audit + attempt + optional current draft; test wymusza rollback. Historyczny B3 audit opisuje już nieaktualny brak. |
| B3.H2 execution idempotency | missing / next unblocked | Request/repository contracts bez idempotency key; attempt GUID generowany za każdym razem, draft upsert zwiększa row_version | Nie wykonywano głębokiego audytu #27; osobny QA jest właścicielem. Atomicity nie zapewnia idempotency. |
| B3.H3 extraction HTTP | missing | Kompletny `Program.cs` nie mapuje extraction call | Najpierw H2. Musi zachować tenant group, exact versions, explicit consent i fail-closed seams. |
| B3.H4 AI/review OpenAPI | missing | Operation metadata nie ma canonical review/extraction | Najpierw H3; uzupełnić także już istniejące B2 operations. |
| B4 content verification/archive/scanner | missing | `Program.cs:82` sprawdza deklarowany MIME/length; brak signature detection/scanner/quarantine/ZIP processing | Techniczne seams można przygotowywać, lecz supported formats/limits są jawną polityką. Trzeba zachować fail-closed. |
| B4.4 production storage | partial | IFileObjectStore istnieje w `Application/Repositories.cs`; tylko LocalFileObjectStore | Provider selection/DI, verify-on-read, multi-process semantics pozostają pracą. |
| B5 calculation readiness/calculate/replay/scenarios HTTP | missing API, actual reusable core | `Application/CalculationService.cs`, SnapshotInputRepository; `Program.cs:50` tylko dev golden | Brak ogólnej tenant calculation route/readiness. Wykorzystać frozen core; readiness lifecycle zależy od brakującej polityki. |
| B6 Quote/QuoteVersion/pricing contract/approval skeleton | missing | Brak dedykowanych typów, repozytoriów, migracji i HTTP | QuoteRequest/QuoteSnapshot nie są QuoteVersion. Existing approved routing nie jest approval of quote. |
| B6.4 PricingEngineV1 | blocked | Nie istnieje sale pricing engine | Wymaga margin-vs-markup, rounding, minima, quantity breaks, discount/risk/currency/FX/external costs. |
| B7 geometry/STEP | blocked, missing | Brak worker/parser/schema/golden fixtures | Potrzebne reprezentatywne STEP i oczekiwane wyjścia. |
| B8 technology proposal/rules | missing; partial reusable routing model | Routing.cs zapewnia istniejące approved-route constraints | Brak proposal DTO/AI workflow/capability feasibility validator/human proposal approval. |
| B9 similarity/risk | missing | Brak modułów/vector/history-search/risk policy | Feature schema/history and explicit risk policy needed; nie wprowadzać wymyślonych progów. |
| B10 quote PDF/numbering/email draft/finalize | missing, blocked by pricing/lifecycle | Brak modułów w repo | B6 + zatwierdzona pricing/approval policy. |
| B11 mailbox/ERP/actuals/feedback | missing | Brak mailbox connector, queue/outbox, ERP/actuals schema | Integrations/contracts/policies później. |
| Frontend | missing and intentionally gated | Brak frontend projektu | B1–B5 complete + B6 contracts stable wymagane przez plan. |
| CI / operational guardrails | actual test workflow; partial operations | `.github/workflows/ci.yml`: PostgreSQL16, locked restore, tests, TRX, contents:read | Brak coverage/static/dependency/secret-scanning jobs. Branch protection #8 i visibility wymagają odrębnej aktualnej weryfikacji GitHub; kod ich nie dowodzi. |

## Pełna powierzchnia HTTP

Wszystkie 11 tenant operations są pod `/api/tenants/{tenantId:guid}` z jednym `TenantRfqAccess` route group. W każdym przeglądanym handlerze jest `RequireRouteTenant` przed repozytorium.

| Metoda | Sufiks trasy | OpenAPI |
|---|---|---|
| POST | `/rfqs` | brak |
| GET | `/rfqs` | brak |
| GET | `/rfqs/{quoteRequestId:guid}` | brak |
| PUT | `/rfqs/{quoteRequestId:guid}/draft` | brak |
| POST | `/rfqs/{quoteRequestId:guid}/files/{logicalKey}` | obecne |
| GET | `/rfqs/{quoteRequestId:guid}/files` | brak |
| GET | `/rfqs/{quoteRequestId:guid}/files/{logicalKey}/versions/{versionNo:int}` | obecne |
| GET | `/rfqs/{quoteRequestId:guid}/canonical-draft` | brak |
| POST | `/rfqs/{quoteRequestId:guid}/canonical-review` | brak |
| GET | `/rfqs/{quoteRequestId:guid}/canonical-review/readiness` | brak |
| GET | `/rfqs/{quoteRequestId:guid}/canonical-review/history` | brak |

Publiczne: GET `/health` (udokumentowany) i GET `/openapi/v1.json`. Development-only: GET `/api/dev/golden/w07044` (celowo poza produkcyjnym schema). Nie ma endpointów customers/contacts, extraction execute/history, RFQ transitions, production calculation/replay, quote/version/approval ani PDF/mail.

## Migracje — aktualny zakres

| Nr | Zakres |
|---|---|
| 001 | tenants, parts, revisions, material, stock, machines, rate intervals |
| 002 | routes, operations, inspections, approved-row guards |
| 003 | quote_requests, canonical snapshots, RFQ/snapshot links, calculation runs/results |
| 004 | append-only audit_events + decision triggers |
| 005 | immutable cost rate versions, explicit quote cost inputs, operation cost results |
| 006 | immutable RFQ logical documents and versions |
| 007 | nullable early RFQ inputs + progressed-input invariant |
| 008 | tenant-scoped customers/contacts + optional RFQ customer FK |
| 009 | positive RFQ row_version |
| 010 | immutable extraction attempts + mutable current canonical draft with same-RFQ attempt FK |

Nie znaleziono migracji idempotency, RLS, quote versions, scanner/quarantine, geometry ani actuals. Nie jest to powód do modyfikowania starych migracji.

## Konkretne defekty i luki

### F1 — P2 — niepoprawne pola RFQ zwracają 500

`Host/Program.cs:58–68` i `72–77` przekazują quantity/currency/id do repozytorium. `Persistence/QuoteRequestRepository.cs:32–34` rzuca `ArgumentException`/`ArgumentOutOfRangeException` dla np. `requestedQuantity: 0`, `currency: "pln"`, `currency: ""`, pustego ID. `Host/ApiExceptionHandler.cs:68–73` mapuje nieznane wyjątki na 500/INTERNAL_SERVER_ERROR. To deterministyczna ścieżka kodu, nie brak decyzji biznesowej: istnieją już reguły walidacji, lecz ich wyjątki nie są kontraktem klientowskiego błędu. Także konflikt istniejącego ID i niewłaściwe customer/part FKs dochodzą do nieobsłużonego PostgresException; należy ustalić i testować bezpieczny, precyzyjny mapping znanych constraints.

Minimalna poprawka: lokalna walidacja request/domain z istniejącymi regułami i kodami 400, plus bezpieczne odwzorowanie tylko znanych konfliktów; nie mapować globalnie wszystkich ArgumentException na 400. Testy integracyjne POST/PUT muszą udowodnić kod/status i brak zapisu. Obecne RfqCreateApiTests pokrywają New/status guard, nie te pola. Audyt statyczny, bez nowego HTTP reproduktora.

### F2 — P2 — schema OpenAPI pomija działające endpointy

`ApiOpenApiDocumentV1.Build` pomija każdy endpoint bez prywatnego `ApiOperationMetadata` (`:87–89`); `WithName` i AddEndpointsApiExplorer same nie wystarczają. W Program metadata nadano wyłącznie health/upload/download. H4 opisuje AI/review, ale powinien także zamknąć lukę B2: create/get/list/draft/manifest. Dowód w powyższej pełnej tabeli tras. Nie wymaga wyboru produkcyjnego providera auth.

### F3 — P2 — nieznana numeryczna decyzja review nie ma walidacji

Globalny `new JsonStringEnumConverter()` w `Program.cs:17` nie wyłącza wejścia numerycznego. `RfqCanonicalReviewServiceV1.ValidateRequest` (`:188`) nie sprawdza `Enum.IsDefined`, a repository switch (`RfqCanonicalReviewRepository.cs:73–78`) rzuca InvalidOperationException dopiero po UPDATE w transakcji. Dla nieznanej wartości decision efektem powinien być 400; obecna ścieżka kończy jako 500 (UPDATE jest cofany przy disposal transakcji). Do potwierdzenia focused integration testem z `decision: 999`, istniejącym draftem i poprawnym expectedRowVersion; nie należy zaliczać jako uruchomionego testu tego audytu.

### Obserwacja wymagająca doprecyzowania/testu, nie samowolnej reguły biznesowej

Readiness grupuje pełną historię review wyłącznie po FieldPath i wybiera `group.Last()` (`RfqCanonicalReviewServiceV1.cs:165–174`). Repository sortuje po `created_at,id` (`RfqCanonicalReviewRepository.cs:175`), mimo że audit zawiera before/after row versions i source_attempt_id. Nie filtruje do `draft.SourceAttemptId`. Zatem dawny REJECT dla np. `quantities[1]` może pozostać blockerem po nowej ekstrakcji nawet jeśli nowy draft nie ma tego indeksu. Trzeba opisać/testować zasadę dziedziczenia review przy re-extraction i spójność odczytu draft/history; nie zmieniać automatycznie tej polityki w #28. Ponadto kolejność wersji jest silniejszą podstawą wyboru najnowszego eventu niż timestamp z losowym UUID, szczególnie przy współbieżnych transakcjach.

Znane ryzyka upload MIME spoofing/brak magic scan/brak verify-on-read pozostają B4; audit nie twierdzi, że obecna konfiguracja nadaje się do produkcyjnego przyjmowania nieufnych plików.

## Rozbieżności dokumentacji

1. README nagłówek mówi `Aktualny stan — 2026-10-05` i 790 testów; obecny kod zawiera B2/B3 i H1. README twierdzi brak auth i customer workflow w sposób zbyt szeroki: seam i model/repository istnieją, production identity/full lifecycle nie. Migracje w README kończą się na 007, faktycznie są 001–010. Fragment B3.4 mówi, że trwałe próby/current draft należą do przyszłego B3.5, choć są obecne.
2. DEVELOPMENT_STATE jest narastającym logiem, z wieloma `Current milestone`, `Current run findings`, `CI state`, `Exact next task`. Na górze baseline B3.4=886; dalej B2=841 i polecenie merge #23; na końcu poprawny H1=910 i H2 next. Automatyczny selektor może wybrać nieaktualny blok. Potrzebny jeden bieżący synopsis, a reszta oznaczona jako historia.
3. PROJECT_AUDIT_2026-10-05 jest poprawnym historycznym baseline, ale nie powinien być bieżącą mapą: brak auth/DI/customer/normalization już nie jest prawdziwy. Przechować jako historyczny dokument i linkować aktualną mapę.
4. B1 audit historycznie opisuje opt-in auth i calculation race; plan/kod zawierają poprawki H1/H2. B1.H3 oznaczone DONE jest tylko częściowym operacyjnym zakończeniem: branch protection nadal ma osobny blocker #8 według dokumentu; wymaga świeżej weryfikacji GitHub przez ASTRA.
5. B2 audit opisuje create progressed status jako otwarty F2; B2.H2 już blokuje go w API/repository. Plan nadal ma zdanie `B2.H2 is the next unblocked hardening task` poniżej DONE tego zadania.
6. B3 audit H1 READY i niezależne SaveAsync + SaveAttemptAsync jest historyczne; produkcyjny service używa SaveAtomicAsync. Plan i koniec DEVELOPMENT_STATE właściwie oznaczają H1 DONE.
7. B3.6 `critical unresolved ... blocks progression` należy czytać z uściśleniem: obecny kod raportuje review blockers i nie implementuje żadnej progression RFQ. Krytyczność biznesowa pozostaje B2.3 blocked.
8. Deklaracje 790/841/886/907/910 w dokumentach są historycznymi dowodami dla różnych commitów, nie bieżącym pomiarem. Aktualną liczbę/powodzenie ustala baseline ASTRA dla dokładnego SHA.

## Małe następne zadania

1. W #28 opublikować bieżący synopsis/mapę i usunąć sprzeczne `next task` z aktywnego nagłówka dokumentacji, zachowując historię i linki dowodowe. Bez zmian deterministycznych silników/migracji.
2. Osobne małe issue/PR na F1 (RFQ validation error contract), z focused POST/PUT tests. F3 może być osobnym małym patch/testem review boundary. Żaden nie wymaga biznesowej decyzji.
3. B3.H2 / #27 według odrębnego audytu QA: versioned idempotency, tenant/RFQ scope, concurrency/retry/replay contract. Nie deklarować go ukończonym na podstawie samej H1 atomicity.
4. Po H2: B3.H3 extraction HTTP; następnie H4 metadata wszystkich działających B2/B3 routes i test pokrycia route/schema. Default materializer/redactor muszą nadal blokować.
5. B4 w małych increments: content verification → archive policy → scanner seam → storage DI/integrity. Supported format/limit decisions jawnie wydzielić, bez zgadywania polityki.
6. B5 tenant calculation/readiness/replay/scenarios wykorzystujące istniejący core; B2.3/B2.4b i production auth muszą mieć osobne decyzje, a Pricing/Geometry pozostają blocked. Frontend nie startuje.

Do dostarczenia przez właściciela produktu/deployment: transition/readiness + analysis lock policy; identity provider/claims; redaction/supportowane parsers; pricing; STEP fixtures. Poza tymi blokadami istnieją małe bezpieczne zadania techniczne, więc cały backend nie jest globalnie zablokowany.
