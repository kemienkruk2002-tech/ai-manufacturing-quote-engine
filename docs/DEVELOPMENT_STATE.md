# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI after B3.4: **671 unit + 215 integration = 886/886 passed**.
- B3.4 merge: `eda1601deddd9e640a756d1e8637733687c501f6`; post-merge run `37435364172` succeeded.
- Frozen deterministic engines/snapshots/hashes/replay and existing migration history remain unchanged.
- B2 focused audit: `docs/B2_RFQ_BACKEND_AUDIT_2026-10-06.md`.

## Current milestone
**B2 — Real RFQ backend / audited hardening complete except explicit business blockers**

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
Repository defines status vocabulary and the progressed-input invariant, but no allowed transition graph or READY_FOR_ANALYSIS/readiness policy. Do not invent workflow policy.

### B2.4a Draft optimistic concurrency — DONE
PR #12; merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; post-merge run `37398187730`; **822/822 PASS**.

### B2.4b Post-analysis revision semantics — BLOCKED_BUSINESS_POLICY
The analysis/lifecycle boundary is undefined until B2.3 policy is supplied.

### B2.5 File manifest endpoints — DONE
PR #13; merge `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`; post-merge main run `37414197830`; **824/824 PASS**.

### B2.H1 Local object-store atomic publication — DONE
PR #14; merge `2762f8998a7c8dd4e6c871b731d1fac40b7decdb`; post-merge main run `37424238234`; **824/824 PASS**.

### B2 audit — DONE
DB/API/replay/tenant-isolation findings are recorded in `docs/B2_RFQ_BACKEND_AUDIT_2026-10-06.md`.

### B2.H2 Draft-only RFQ creation guard — DONE
PR #16; implementation head `a18b20934ed5ce62e2da704c2c5d2a022bfde749` passed run `37426044035`; final PR head `2e477cd5e52e4096d61ec20a143ccea0c2a3099c` passed run `37426213926`; merge `530383cb1b9ae9f98e5a13f2b22757c6a6852ac5`; post-merge run `37426370315`; **841/841 PASS**.

RFQ creation now accepts only omitted/explicit `New`. Every explicit non-`New` status is rejected by both the API and repository create boundary; no later transition policy was defined.

## Known blockers
- B2.3: RFQ transition/readiness business policy missing.
- B2.4b: post-analysis revision semantics depend on the undefined B2.3 lifecycle boundary.
- Production identity provider: deployment decision missing; production auth remains fail-closed.
- PricingEngineV1: commercial policy missing.
- Geometry golden work: representative STEP fixtures/expected outputs missing.
- Repository is public; no confidential customer/production data may be committed.

## Completed work
- B1 foundation/hardening complete; see B1 audit/history.
- B2.1 customer/contact model: DONE, PR #10, 816/816 PASS.
- B2.2 RFQ create/read/list: DONE, PR #11, merge `a3e78077b25f1396f2931b8d3707a071dc075065`, post-merge 820/820 PASS.
- B2.4a draft optimistic concurrency: DONE, PR #12, merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`, post-merge 822/822 PASS.
- B2.5 file manifest endpoints: DONE, PR #13, merge `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`, post-merge 824/824 PASS.
- B2.H1 local object-store atomic publication: DONE, PR #14, merge `2762f8998a7c8dd4e6c871b731d1fac40b7decdb`, post-merge 824/824 PASS.
- B2 focused audit: DONE, PR #14.
- B2.H2 draft-only RFQ creation guard: DONE, PR #16, merge `530383cb1b9ae9f98e5a13f2b22757c6a6852ac5`, post-merge 841/841 PASS.

## Current run findings
- Re-read current main plus AUTONOMOUS_BACKEND_PLAN.md, DEVELOPMENT_STATE.md and PROJECT_AUDIT_2026-10-05.md before work.
- Verified the prior state-only main commit `0e837b7b12d2052be9942cc0633b9a66cf0d4fcf` first; run `37424813843` succeeded.
- Selected exactly B2.H2, the smallest unblocked READY backend task from the B2 audit.
- Confirmed the source-level bypass: the create API forwarded `CreateRfqRequest.Status` directly and `QuoteRequestRepository.CreateAsync` accepted non-`New` statuses.
- Added deterministic API rejection `RFQ_CREATE_STATUS_INVALID` for every explicit non-`New` status.
- Added the same draft-only invariant at repository creation so the HTTP guard cannot be bypassed through the application repository abstraction.
- Preserved reading/filtering of existing historical/non-draft RFQs; tests that require a non-draft fixture seed it directly rather than using the draft-create operation.
- Added focused API coverage for omitted status, explicit `New`, every one of the eight non-`New` enum values, and proof that rejected API requests create no database row.
- Added repository coverage rejecting all non-`New` create attempts.
- PR #16 implementation and final heads were green before merge.
- Post-merge main run `37426370315` is green: **641/641 unit + 200/200 integration = 841/841 PASS**.
- No migration, transition graph, readiness/approval policy, deterministic engine, canonical snapshot/hash/replay behavior, immutable history or tenant-isolation contract changed.
- No frontend work started.

## CI state
- Verified runtime main: **GREEN**, merge `530383cb1b9ae9f98e5a13f2b22757c6a6852ac5`, run `37426370315`, **841/841 PASS**.
- This commit only records final B2.H2 state and must itself pass GitHub Actions before the next backend task starts.

## Current milestone
**B3 — AI RFQ extraction wired end-to-end**

### B3.1 AI configuration + DI — DONE
Branch: `auto/b3-1-ai-config-di`.

Scope: register the existing OpenAI Responses provider, retry wrapper and AiGatewayV1 behind validated server-side configuration; configure HttpClient through IHttpClientFactory; require API credentials only when AI is enabled; allow AI to remain disabled without constructing provider services. No extraction endpoint or B3.2 normalization work is included.

## Current run findings
- Verified the preceding main state commit `f2f78ea367c185b523356f6ff2f61e6c74d2876f` first; run `37426572654` completed successfully with **841/841 PASS**.
- Confirmed there were no open PRs or pending prior tasks before starting B3.1.
- Reviewed the existing `OpenAiResponsesProviderV1`, retry wrapper, gateway, host composition root and AI unit tests.
- Primary-source check: OpenAI requires server-side Bearer credentials and recommends keeping API keys out of client code; Responses structured outputs use `text.format.type=json_schema`, matching the existing provider. Microsoft recommends `IHttpClientFactory` for configured clients and `ValidateOnStart` for startup options validation.
- Added `AiIntegrationOptions` with `Ai:Enabled`, `Ai:BaseUrl`, `Ai:ApiKey` and optional `Ai:RetryDelaysMs`.
- AI remains disabled by default. Disabled mode keeps the DI graph resolvable but selects a fail-closed provider returning `AI_DISABLED`; it does not invoke the OpenAI HTTP provider.
- Enabled mode requires a non-empty server-side API key, an absolute HTTPS base URL and non-negative retry delays; validation runs at startup.
- Registered a named `HttpClient`, `OpenAiResponsesProviderV1`, `SystemAiRetryDelay`, `RetryingAiStructuredProviderV1`, `IAiStructuredProvider` selection and `AiGatewayV1`.
- The named client applies Bearer authentication only from runtime configuration. No credential or real secret was committed or logged.
- Retry delays default to an empty list, so B3.1 does not invent a retry schedule; deployments may opt in through configuration.
- Added host integration tests for disabled fail-closed behavior, enabled DI graph/client configuration, missing-key rejection, HTTPS validation, retry-delay validation and disabled-mode tolerance of unused provider settings.
- Updated README with the server-side configuration contract. No AI extraction endpoint, B3.2 normalization, persistence, deterministic calculation engine, canonical snapshot/hash/replay, migrations, immutable histories or tenant isolation was changed.

## CI state
- Implementation is published on `auto/b3-1-ai-config-di`.
- Initial PR head `afd8e33072dbd0bf9f8a9b92c1a4884d5d4da966` failed run `37427440602` at compile time only: CS0123 on method-group projection to `TimeSpan.FromMilliseconds`.
- Fixed only that concrete build error with an explicit `delay => TimeSpan.FromMilliseconds(delay)` projection.
- Verified implementation head `1aac38c1af6d9ed82670a9fd20ee0271b8a38030` passed run `37427572933`: **641/641 unit + 206/206 integration = 847/847 PASS**.
- Final PR head `329b8f290b026381f96d838e58366abbe92e96dd` passed run `37427756955`: **847/847 PASS**.
- PR #17 merged as `666d9d0d5d9b532a194df8ab074496f1a044e81e`.
- Post-merge main run `37428227503` passed **641/641 unit + 206/206 integration = 847/847 PASS**.
- State-sync main commit `727c5e46edea669ac13edbb17f4fd2ddd165f252` passed run `37428385748`; this final documentation-only update requires its own CI verification on the next run.

### B3.2 Deterministic AI input normalization — DONE
Branch: `auto/b3-2-ai-input-normalization`.

Scope: add one deterministic normalization boundary for supported JSON before AI request fingerprinting, prove equivalent supported object inputs are byte-identical/fingerprint-identical, preserve array order and value types, and do not change CanonicalRFQ v1 or provider output schema.

## Current run findings
- Verified the preceding state-only main commit `cd5a7dc634f0f1ad11b2040f78af7d0c41df196b` first; run `37428554033` completed successfully.
- Started exactly B3.2 on `auto/b3-2-ai-input-normalization`.
- Reviewed the current request validation, fingerprint and prompt compilation paths. Before B3.2, all three accepted valid JSON but fingerprinting hashed the caller's original JSON bytes, so whitespace/property-order differences produced different fingerprints.
- Primary-reference review: RFC 8785 confirms recursive object-property sorting and preservation of array order as core JSON canonicalization requirements; Microsoft System.Text.Json supports deterministic DOM-to-writer serialization. B3.2 does not claim full JCS conformance because it uses a project-specific exact-decimal number canonicalization instead of ECMAScript/IEEE-754 number serialization.
- Added `AiInputJsonNormalizerV1`: recursively sorts object properties using ordinal UTF-16/.NET string ordering, preserves array order and JSON value types, removes insignificant formatting, canonicalizes decoded string escaping through `Utf8JsonWriter`, and rejects duplicate property names as ambiguous input.
- Added exact JSON-number normalization without floating-point conversion. Equivalent decimal lexemes such as `1`, `1.0`, `10e-1` and `0.10e1` normalize identically; large exponent text is handled using `BigInteger`, avoiding precision loss.
- `AiStructuredRequestValidatorV1` now rejects inputs the normalizer cannot canonicalize.
- `AiRequestFingerprintV1` hashes canonical input JSON instead of caller formatting.
- `RfqExtractorPromptV1` embeds the same canonical JSON in the provider prompt, so fingerprint and provider-visible input share one deterministic representation.
- Added focused unit coverage for recursive property ordering, whitespace, nested objects in arrays, equivalent numeric lexemes, array-order significance, value-type significance, duplicate-property rejection and equivalent prompt output.
- CanonicalRFQ v1 output schema/guard is unchanged. No provider policy, endpoint, persistence, pricing, workflow, deterministic calculation engine, canonical calculation snapshot/hash/replay, migration, immutable history or tenant isolation behavior changed.

## CI state
- B3.2 implementation is published on `auto/b3-2-ai-input-normalization`.
- Verified PR #18 head `b0f631d9bd375e62e58c42dcc61272537c279f99` is **GREEN** in run `37429166917`: **652/652 unit + 206/206 integration = 858/858 PASS**.
- Final PR #18 head `3aeafbd0e6b7de06d0adc280fb62ddfb4f61f53b` passed run `37429341570`: **858/858 PASS**.
- PR #18 merged as `470f87a2a3e11431f377940de43549535fd62c46`.
- Post-merge main run `37429487560` passed **652/652 unit + 206/206 integration = 858/858 PASS**.

## CI state
- PR #18 final head `3aeafbd0e6b7de06d0adc280fb62ddfb4f61f53b`: **GREEN**, run `37429341570`, **858/858 PASS**.
- Merge `470f87a2a3e11431f377940de43549535fd62c46`: post-merge `main` run `37429487560` **GREEN**, **858/858 PASS**.
- State-sync main commit `2378fad41a59079c295b7ad4aaea20ac233609cc`: run `37429665374` **GREEN**, **652/652 unit + 206/206 integration = 858/858 PASS**.
- This final documentation-only commit records that verified state and must itself be checked before the next backend task starts.

### B3.3 AI execution policy — DONE
Branch: `auto/b3-3-ai-execution-policy`.

Scope: add a default-deny external-AI policy boundary around the existing gateway. The policy will require an explicit per-execution `allow_external_ai` flag plus configured allowlists for use case/model/document type and an explicitly configured positive payload limit. It will expose a redaction seam and deterministic REVIEW/MANUAL outcomes for policy/redaction/provider failures. No allowlist entries, payload limit, redaction rules, endpoint, persistence or business workflow will be invented.

## Current run findings
- Verified final B3.2 state commit `66f785c3fac9016c65a33353637d6691116da0a0` first; run `37429863910` completed successfully with **858/858 PASS**.
- Confirmed there were no open PRs before starting B3.3.
- Primary-source review: OWASP recommends allowlist validation, bounded input, separation of untrusted content and treating model output as untrusted; OpenAI documents that API customer content may appear in default abuse-monitoring logs. These findings are recorded in ADR 002.
- Added `AiPolicyExecutorV1` as a default-deny application boundary around `AiGatewayV1`.
- External execution now requires explicit per-call `AllowExternalAi=true`, configured ordinal allowlists for use case/model/document type, at least one document type, and an explicitly configured positive UTF-8 payload limit.
- Input size is checked before redaction and checked again after redaction.
- Added `IAiInputRedactor` seam. The Host default redactor always blocks with `AI_REDACTION_NOT_CONFIGURED`; no redaction/PII rule was invented.
- Policy, redaction, invalid redactor output, gateway output validation and provider failures all terminate in deterministic `REVIEW_MANUAL`; no fallback model or fabricated output is introduced.
- Added `Ai:Policy` host configuration for allowlists and max payload bytes. Empty allowlists and zero limit are valid but deny execution.
- Added focused unit tests proving per-call consent, use-case/model/document-type allowlists, required document type, payload limits, redaction blocking/validation, provider-visible redacted input, and provider failure behavior.
- Added host integration coverage proving default deny, default blocking redactor, startup validation for blank allowlist entries and negative payload limit.
- Added `docs/adr/002-external-ai-execution-policy.md` and README deployment documentation.
- No B3.4 endpoint/service/persistence, CanonicalRFQ v1 schema, deterministic calculation engine, snapshot/hash/replay, migration, immutable history, tenant isolation, pricing, workflow or approval policy changed.

## CI state
- B3.3 implementation is published on `auto/b3-3-ai-execution-policy`.
- Verified PR #19 implementation head `5b7f8145f55d0bb5f9ee052ff724191dbe959328` is **GREEN** in run `37432555854`: **664/664 unit + 210/210 integration = 874/874 PASS**.
- Final PR #19 head `280e39be406b891855df583ac7c12f84d1a5cf8c` passed run `37432765264`: **874/874 PASS**.
- PR #19 merged as `b6ad030f5c4dc2f909bf809c14d870a4180b3627`.
- Post-merge main run `37432937882` passed **664/664 unit + 210/210 integration = 874/874 PASS**.

## CI state
- PR #19 final head `280e39be406b891855df583ac7c12f84d1a5cf8c`: **GREEN**, run `37432765264`, **874/874 PASS**.
- Merge `b6ad030f5c4dc2f909bf809c14d870a4180b3627`: post-merge `main` run `37432937882` **GREEN**, **664/664 unit + 210/210 integration = 874/874 PASS**.
- State-sync main commit `3ed6c042d779c0635e4d94ef518ccb3d39b7dff7` passed run `37433105161`: **664/664 unit + 210/210 integration = 874/874 PASS**.
- This final documentation-only commit records that verified state and must itself be checked before the next backend task starts.

## Current run findings
### B3.4 RFQ extraction service — DONE
Branch: `auto/b3-4-rfq-extraction-service`.

- Verified final B3.3 documentation commit `7492fe5bd513ae19a088cd394fe8623cb3db0cc9` first; run `37433357030` completed successfully with **874/874 PASS**.
- Confirmed there were no open PRs before starting B3.4.
- Reviewed the RFQ file/version repositories, manifest, immutable file-history migration, audit schema, Host composition root and B3.1-B3.3 AI boundaries before implementation.
- Repository has no PDF/STEP/e-mail parser. B3.4 therefore introduces `IRfqExtractionSourceMaterializer` rather than inventing binary-to-text behavior; Host default materializer fails closed with `RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED`.
- Added `RfqExtractionServiceV1`. Callers must explicitly select each `logical_key + version_no + document_type`; the service never auto-selects latest versions.
- Selected sources are resolved tenant/RFQ-scoped through `IRfqFileRepository.FindVersionAsync`. Missing exact versions produce `REVIEW_MANUAL`; there is no fallback to another version.
- Selected sources are sorted deterministically before canonical input construction, so the same selected set yields the same request fingerprint regardless of caller order.
- The normalized input carries deterministic source key, existing source reference, logical key, exact version, document type, file SHA-256 and materialized content.
- The service executes only through `AiPolicyExecutorV1`. It never calls the provider directly.
- Persisted request fingerprint uses the gateway/provider-visible fingerprint when the gateway executes (therefore reflecting redaction); otherwise it uses the deterministic pre-policy request fingerprint when such a request exists.
- Added `IRfqExtractionExecutionRepository` / `RfqExtractionExecutionRepository`. B3.4 persists only execution metadata (model/prompt/schema/fingerprint/disposition/code) as append-only `audit_events`; it does not create B3.5 extraction-attempt/history tables or persist CanonicalRFQ drafts.
- Audit insertion uses `INSERT ... SELECT` from the exact `quote_requests(tenant_id,id)` pair, so an extraction event cannot be attached to an RFQ outside the tenant.
- Added unit coverage for exact-version selection, deterministic selection ordering/fingerprint, no implicit fallback, materialization failure, policy block persistence and provider-visible redacted fingerprint.
- Added PostgreSQL integration coverage for durable metadata, nullable pre-request fingerprint, tenant/RFQ-scoped audit insertion and audit immutability.
- Added Host integration coverage proving the extraction service resolves through DI and the default source materializer fails closed before external AI.
- No migration was added; existing migration history remains unchanged. No endpoint, B3.5 draft/history persistence, CanonicalRFQ v1 contract, final cost/time/price fields, calculation engines, snapshot/hash/replay, RFQ workflow or approval policy changed.

## CI state
- B3.4 implementation is published on `auto/b3-4-rfq-extraction-service`.
- Verified PR #20 implementation head `cf8b2f9ffe85a17be7c7d8914c41dc71cea7c5a7` is **GREEN** in run `37435066563`: **671/671 unit + 215/215 integration = 886/886 PASS**.
- Final PR #20 head `0f8e9752864ccc0244c055518e460bf727a46457` passed run `37435238467`: **886/886 PASS**.
- PR #20 merged as `eda1601deddd9e640a756d1e8637733687c501f6`.
- Post-merge main run `37435364172` passed **671/671 unit + 215/215 integration = 886/886 PASS**.

## CI state
- PR #20 final head `0f8e9752864ccc0244c055518e460bf727a46457`: **GREEN**, run `37435238467`, **886/886 PASS**.
- Merge `eda1601deddd9e640a756d1e8637733687c501f6`: post-merge `main` run `37435364172` **GREEN**, **671/671 unit + 215/215 integration = 886/886 PASS**.
- This documentation-only state update must itself pass GitHub Actions before the next backend task starts.

### B3.5 Persist CanonicalRFQ draft — DONE
Branch: `auto/b3-5-canonical-rfq-draft`. PR #21.

## Current run findings
- Re-read current `main`, `docs/AUTONOMOUS_BACKEND_PLAN.md`, `docs/DEVELOPMENT_STATE.md` and `docs/PROJECT_AUDIT_2026-10-05.md`.
- Verified final B3.4 state-only main commit `daae79cc97f0bf69745678f95339c874e78ddd0f`; run `37439766594` is GREEN with **671 unit + 215 integration = 886/886 PASS**.
- Found existing open PR #21 for B3.5 and inspected it before starting any new work. Its head `b97babed20c846c1cf9f0284be54574d714ea81b` failed run `37437143687`.
- Concrete failed-CI causes: Npgsql returned `TIMESTAMPTZ` from `ExecuteScalarAsync` as `DateTime`, but the repository cast it directly to `DateTimeOffset`; two migration-count tests still expected 9 migrations after migration 010.
- Fixed timestamp conversion without changing stored time semantics and updated focused migration-count tests to 10.
- Removed default no-op implementations from `IRfqExtractionExecutionRepository`; every implementation must now explicitly support B3.5 persistence instead of silently dropping history.
- Migration 010 adds tenant/RFQ-scoped immutable `rfq_extraction_attempts` plus separate current `rfq_canonical_drafts`.
- Strengthened migration 010 so a current draft can reference only an extraction attempt from the **same tenant and same RFQ** via composite FK `(tenant_id, quote_request_id, source_attempt_id)`.
- Extraction attempts retain model/prompt/schema versions, request fingerprint, disposition/code, source lineage and raw provider output. UPDATE/DELETE/TRUNCATE of attempts are rejected at the database boundary.
- Current CanonicalRFQ draft is separate from raw provider output; a completed validated extraction initializes/replaces only the current draft and increments row version while immutable attempts remain unchanged.
- Repository now reuses the existing `RfqExtractorOutputGuardV1` before accepting any JSON as a CanonicalRFQ draft; an arbitrary JSON object cannot bypass the existing CanonicalRFQ v1 contract.
- Provider raw JSON is retained verbatim in attempt history even when output validation routes the execution to REVIEW_MANUAL; invalid output never becomes the current draft.
- Added focused unit coverage for raw-output/draft separation and source lineage.
- Added PostgreSQL integration coverage for raw output and lineage retention, explicit MISSING/CONFLICT preservation, current-draft row-version replacement, REVIEW_MANUAL non-overwrite, invalid CanonicalRFQ rejection, immutable attempt history, same-RFQ source-attempt FK, and tenant/RFQ scoping.
- No B3.6 review/correction API, RFQ workflow transition, pricing, calculation engine, canonical calculation snapshot/hash/replay, geometry or frontend behavior changed.

## CI state
- Repaired implementation head `afd561f146df0e81124c38832860d8a54eb6116e` passed run `37441176622`: **896/896 PASS**.
- Final PR #21 head `96097df9ad34d25bcb616e319443ed1067f8e6c7` passed run `37441377856`: **896/896 PASS**.
- PR #21 merged as `7fddfcf4c3936b12f8d3255a3b27520cb7852552`.
- Post-merge main run `37441538770` passed **674/674 unit + 222/222 integration = 896/896 PASS**.

## CI state
- PR #21 final head `96097df9ad34d25bcb616e319443ed1067f8e6c7`: **GREEN**, run `37441377856`, **896/896 PASS**.
- Merge `7fddfcf4c3936b12f8d3255a3b27520cb7852552`: post-merge `main` run `37441538770` **GREEN**, **674/674 unit + 222/222 integration = 896/896 PASS**.
- State-sync commit `a79ebdcfa8139a43a49d29b95557dc3995852ae2` exposed one unrelated pre-existing flaky host-startup assertion in run `37441740738`: B3.5 unit suite stayed **674/674 PASS**, while `Negative_policy_payload_limit_fails_startup_validation` received `ObjectDisposedException` from `WebApplicationFactory` instead of the options-validation text.
- Fixed only that test in PR #22 by resolving `AiExecutionPolicyOptions` through the registered `IOptions` validators directly; runtime policy/configuration is unchanged.
- PR #22 head `08aa5a8ff33909ca24f49bb1d4f7e3dc867ceba2`: run `37441990446` **GREEN**, **674/674 unit + 222/222 integration = 896/896 PASS**.
- PR #22 merged as `ab9fa9d0fb034e1ed3781f087288795addc3e714`; post-merge main run `37442192884` **GREEN**, **674/674 unit + 222/222 integration = 896/896 PASS**.
- This final documentation-only commit records that repaired verified state and must itself pass GitHub Actions before the next backend task starts.

### B3.6 Review/confirmation backend — CI_PENDING
Branch: `auto/b3-6-review-confirmation`.

Scope is limited to backend operations for explicit human review of the current CanonicalRFQ draft. No RFQ status transition or B2.3 workflow policy is defined by this task.

## Current run findings
- Re-read current `main`, `docs/AUTONOMOUS_BACKEND_PLAN.md`, `docs/DEVELOPMENT_STATE.md` and `docs/PROJECT_AUDIT_2026-10-05.md`.
- Verified final state-only main commit `01a3b4ebeb531eff0dcdbf5ab8cfd998b6ee965c`; run `37442379022` completed successfully with **674 unit + 222 integration = 896/896 PASS**.
- Confirmed there were no open PRs or pending prior tasks before starting B3.6.
- Selected exactly B3.6 as the smallest unblocked READY backend task.
- Added `RfqCanonicalReviewServiceV1` with whole-fact review paths matching the existing CanonicalRFQ validator vocabulary (for example `rfq_number`, `revisions[0]`). Nested property patching is rejected.
- `CORRECT` accepts a complete replacement CanonicalRFQ fact, patches exactly one fact, and re-runs the existing `RfqExtractorOutputGuardV1` over the complete draft. A correction cannot bypass CanonicalRFQ v1.
- `CONFIRM` and `REJECT` do not rewrite extracted fact data. Every review action still advances the draft `row_version`, so stale human reviews are rejected deterministically.
- Added `RfqCanonicalReviewRepository`. Draft update and append-only audit insertion happen in one PostgreSQL transaction with compare-and-swap on `row_version`.
- Reused existing immutable `audit_events` rather than adding a duplicate history table. Review events record field path, decision, authenticated actor, caller-provided source, reason, source attempt, before/after fact JSON, before/after row version and correlation ID.
- Review actor is taken from exactly one authenticated `NameIdentifier` claim. No actor identity is accepted from request JSON.
- Added tenant-scoped API operations to read the current CanonicalRFQ draft, submit field review, read review readiness and read review history.
- Readiness is a report only; it never changes RFQ status. Current `MISSING` and `CONFLICT` facts remain explicit blockers, and a latest `REJECT` adds a blocker even when the underlying extracted fact is otherwise resolved.
- A `CONFIRM` event does not erase a current `MISSING` or `CONFLICT`; only a valid correction that changes the CanonicalRFQ fact can remove that blocker.
- Added focused unit tests for exact-fact replacement, schema-bypass rejection, path validation and readiness semantics.
- Added PostgreSQL integration tests for atomic review+audit, append-only audit immutability, stale-version no-write behavior and tenant isolation.
- Added API integration tests for authenticated actor capture, history, 409 stale review, readiness without RFQ status mutation, valid correction and invalid correction rejection.
- No migration, pricing, margin, workflow transition, geometry, calculation engine, canonical calculation snapshot/hash/replay or frontend behavior was changed.

## CI state
- Implementation is published on `auto/b3-6-review-confirmation`.
- Status: **CI_PENDING** until the exact PR head passes GitHub Actions.

## Exact next task
Inspect CI for the exact B3.6 PR head. If it fails, fix only the concrete failure. If green, perform a focused B3.6 review, record exact counts, merge only the verified head and verify post-merge `main`. Do not start the B3 audit or frontend in this run.
