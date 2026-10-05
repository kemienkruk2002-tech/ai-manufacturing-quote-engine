# ADR 001 — Deterministyczny rdzeń etapu 1

Status: przyjęte decyzje techniczne dla implementacji etapu 1. Reguły biznesowe i wartości pochodzą z polecenia użytkownika, dokumentów 02/11 i arkusza 11A. Nowsze polecenie etapu 1 ogranicza zakres dokumentu 11: Cost/Pricing nie są implementowane.

## Projekty i wykonanie

Prosty podział Domain → Application → Persistence, Host jako composition root. Oddzielne projekty egzekwują brak zależności domeny od DB i HTTP. .NET 8 zgodny ze specyfikacją i dostępnym runtime; global.json dopuszcza nowszy SDK, CI używa 8.0.x. `UseAppHost=false` uruchamia aplikację jako `dotnet QuoteEngine.Api.dll` i omija błąd mapowania pliku wykonywalnego na dysku Google Drive. Zasady wyboru SDK: [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

Npgsql i jawne, osadzone SQL zamiast ORM umożliwiają kontrolę triggerów, exclusion constraints, precyzji i granic transakcji. Wszystkie zależności NuGet mają wersje i lock files. Nie wprowadzamy EF wyłącznie po to, żeby wykonywał surowe migracje SQL.

## Canonical schema v1

`QuoteSnapshotPayload` zawiera tenant oraz naturalne identyfikatory detalu, rewizji, wersji procesu, materiału, zasobów i routingu. Kolejność właściwości jest jawna w `Utf8JsonWriter`; operacje są defensywnie kopiowane i sortowane po sequence/operation_no, wymagania po stałych kluczach. Decimal jest stringiem invariant culture, bez wykładnika i nieistotnych końcowych zer. Jednostki są częścią nazw pól. NULL pozostaje NULL, a nie pustym stringiem lub zerem.

W payloadzie v1 nie ma dat. Metadata zdarzeń i zapisu używają UTC (`timestamptz`/DateTimeOffset); nie wpływają na hash. Gdy następny format wprowadzi biznesowe effective dates, musi utrzymać UTC/ISO i podnieść canonical schema version. Nie dodajemy wymyślonych dat obowiązywania do seed.

`snapshot_hash = lowercase_hex(SHA256(UTF8(canonical_json)))`.

`calculation_hash = lowercase_hex(SHA256(UTF8(snapshot_hash + '|' + engine_version)))`.

Cały przepływ Time+Stock ma `engine_version=quote-engine-stage1-v1`. Payload zapisuje osobno `time-engine-v1`, `stock-engine-v1`, `canonical-v1`, więc zmiana dowolnej reguły zmienia hash. Replay odrzuca nieobsługiwane wersje. Hash pojedynczego TimeEngine można obliczać tym samym wzorem z jego wersją.

Canonical bytes przechowujemy w TEXT, obok generated JSONB do zapytań. JSONB nie jest źródłem bajtów hashowanych. PostgreSQL dodatkowo sprawdza SHA-256. Snapshot i jego source_route_id są immutable, łącznie z DELETE i TRUNCATE.

Snapshoty są adresowane zawartością: jeden `(tenant_id,snapshot_hash)` może mieć wiele powiązań w append-only `quote_request_snapshots`. `quote_snapshots.quote_request_id` zapisuje pochodzenie pierwszego utworzenia. Parametry requestu są ponownie sprawdzane pod row lock przed zapisem; ich zmiana blokuje nieaktualny prepared snapshot.

`source_route_id` i GUID-y zapisu należą do metadanych poza canonical payloadem. Wyniki operacji wiążą się z tym niezmiennym routingiem, bez czytania aktualnej rewizji requestu. IDs snapshotów i runów są deterministycznie wyprowadzane z hashy. Runtime audit IDs, correlation IDs i timestampy nie uczestniczą w kalkulacji ani jej hashu.

## Precyzja i braki

Wejścia mas/czasów są `NUMERIC(18,6)`, wyniki `NUMERIC` bez wymuszonej skali, aby zachować pełny `decimal`. Baza odrzuca NaN i ±Infinity. Nie ma domyślnego 0 dla krytycznych braków. Potwierdzone źródłowe zero operacji 0080 pozostaje zerem.

Quantity≤0 i niepoprawne wartości/duplikaty to validation error; brak kompletnego Approved routingu, maszyny, normatywu lub masy to Blocked. Stock nie oblicza normatywu z masy finalnej. Nie dodajemy reguł strat/zaokrągleń/sztuk kontrolnych.

Stawki mają okresy `[effective_from,effective_to)` i exclusion constraint per tenant+machine+rate_type; graniczące okresy są dozwolone. Resolver otrzymuje konkretną datę od wywołującego, nigdy nie czyta bieżącego czasu. Wzorcem jest [PostgreSQL range exclusion](https://www.postgresql.org/docs/16/rangetypes.html#RANGETYPES-CONSTRAINT).

## Wersjonowanie, audyt, concurrency

Routing powstaje jako Draft, po skompletowaniu operacji przechodzi na Approved. Approved i Archived nie mogą być aktualizowane; zmiana wymaga nowego ID/wersji i nowych operacji. Wersja routingu `1` jest technicznym pierwszym numerem; nie zastępuje `Podstawowa(1)`.

SQL triggery blokują zmiany child rows i serializują je z zatwierdzeniem parent row lock. `edit_version` zmienia się przy mutacji dziecka, żeby stary RepeatableRead/Serializable snapshot nie zatwierdził nieaktualnych danych. Operacje między draftami blokują rodziców w stałej kolejności.

Zapis CalculationRun, per-operation breakdown i jego audit jest jedną transakcją. Powtórka tego samego hasha porównuje canonical wynik i nie duplikuje wierszy. Czas wykonania i logi mierzy wyłącznie Application. Brak danych na etapie przygotowania też generuje zakończenie próby z Blocked; gdy snapshot jeszcze nie istnieje, jego hash w logu pozostaje NULL.

Tabele biznesowe mają tenant_id i composite FK. Aplikacja filtruje po tenant_id; endpoint ma stałego lokalnego test tenanta. RLS, auth i publiczny interfejs użytkownika pozostają kolejnym etapem przed produkcyjnym dostępem.

## Zakres potwierdzenia

Etap 1 potwierdza Time/Stock, nigdy cenę lub gotową ofertę. Dlatego request seed nie jest automatycznie oznaczany biznesowym Approved ani pełną wyceną kosztową. Źródła i pozostałe TODO są wypisane w README; nie dodano fikcyjnych stawek, jakościowych czasów ani danych zatwierdzającego.
