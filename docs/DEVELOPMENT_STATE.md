# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI after B2.H2: **641 unit + 200 integration = 841/841 passed**.
- B2.H2 merge: `530383cb1b9ae9f98e5a13f2b22757c6a6852ac5`; post-merge run `37426370315` succeeded.
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

### B3.1 AI configuration + DI — CI_PENDING
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
- Status: **CI_PENDING**. Do not mark DONE or merge until GitHub Actions is green for the exact PR head.

## Exact next task
Open the B3.1 PR and inspect GitHub Actions. If CI fails, fix only the concrete failure. If green, record that exact green head, update plan/state, merge only the verified head, and verify post-merge `main` CI. Do not start B3.2 or frontend in this run. B2.3/B2.4b remain blocked unless workflow policy is supplied.
