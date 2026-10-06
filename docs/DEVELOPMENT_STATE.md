# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI: **641 unit + 179 integration = 820/820 passed**.
- B2.2 merge: `a3e78077b25f1396f2931b8d3707a071dc075065`; post-merge run `37383178770` succeeded.
- Frozen deterministic engines/snapshots/hashes/replay and existing migration history remain unchanged.

## Current milestone
**B2 — Real RFQ backend**

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
Repository defines status vocabulary and the progressed-input invariant, but no allowed transition graph or READY_FOR_ANALYSIS/readiness policy. Blocker is recorded on `auto/b2-3-rfq-state-machine` at `24b48575d8a72296540e4698f3442c65aad4edd0`. Do not invent workflow policy.

### B2.4a Draft optimistic concurrency — CI_PENDING
Branch: `auto/b2-4-draft-concurrency`
PR: #12

Implemented scope: optimistic concurrency protection for editable `New` RFQ draft fields only. Post-analysis revision semantics remain deferred while B2.3 policy is undefined.

## Known blockers
- B2.3: RFQ transition/readiness business policy missing.
- Production identity provider: deployment decision missing; production auth remains fail-closed.
- PricingEngineV1: commercial policy missing.
- Geometry golden work: representative STEP fixtures/expected outputs missing.
- Repository is public; no confidential customer/production data may be committed.

## Completed work
- B1 foundation/hardening complete; see B1 audit/history.
- B2.1 customer/contact model: DONE, PR #10, 816/816 PASS.
- B2.2 RFQ create/read/list: DONE, PR #11, merge `a3e78077b25f1396f2931b8d3707a071dc075065`, post-merge 820/820 PASS.

## Current run findings
- Inspected PR #12 CI before starting anything new, as required.
- Run `37395124712` received a runner and failed in the test step; this was a concrete test-maintenance failure, not infrastructure.
- Unit tests were green: **641/641**.
- Integration result was **179 passed, 2 failed, 181 total**. Both failures were stale migration-count assertions expecting 8 after forward-only migration 009 was intentionally added:
  - `PersistenceConstraintTests.Empty_database_migrates_and_seed_is_repeatable` expected 8, actual 9.
  - `RfqDraftInputTests.Migrations_one_through_eight_apply_from_scratch` expected 8, actual 9.
- Updated only those migration-baseline expectations to 9 and renamed the latter test to `Migrations_one_through_nine_apply_from_scratch`.
- No runtime/domain behavior, migration contents/history, deterministic engines, snapshots/hashes/replay, tenant isolation or workflow policy was changed to fix CI.

## CI state
- PR #12 remains **CI_PENDING** after the focused test fix.
- Latest fix head includes commits `1b20a6b50a6df8e728130deddf42974a104d8008` and `bf92f06ae5de94752b934158025f08c7dee117eb`; this state commit follows them.
- Do not mark DONE or merge until GitHub Actions is green for the exact current PR head.

## Exact next task
Inspect PR #12 CI for the new head first. If it fails, fix only the concrete failure. If it is green, record verified counts/head and merge only that green head, then verify post-merge main CI. Do not start another backend task before this gate completes.
