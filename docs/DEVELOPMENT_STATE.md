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
- Continued the already-selected B2.4a slice after confirming no open prior PR existed.
- Added forward-only migration `009_rfq_draft_concurrency.sql` with positive `row_version BIGINT NOT NULL DEFAULT 1`.
- `QuoteRequest` now exposes the persisted row version; create/read/list return the token.
- Added tenant-scoped compare-and-swap `UpdateDraftAsync`: update succeeds only for `status='New'` and matching expected row version, atomically increments the version, and never changes status.
- Added `PUT /api/tenants/{tenantId}/rfqs/{quoteRequestId}/draft`; missing RFQ returns 404, non-New draft returns `409 RFQ_NOT_EDITABLE_DRAFT`, and a stale/racing token returns `409 RFQ_DRAFT_VERSION_CONFLICT`.
- Added focused repository tests proving two simultaneous writers using the same token yield exactly one winner and one stale rejection, plus stale/non-draft rejection coverage.
- During review caught and removed an invalid `updated_at` write because the existing `quote_requests` schema has no such column; existing migration history was not modified.
- No status-transition graph, post-analysis mutation semantics, pricing, approval, geometry, deterministic engine, snapshot/hash/replay or immutable-history behavior was invented or changed.

## CI state
- Implementation is published on `auto/b2-4-draft-concurrency`.
- PR creation/CI verification is the next required gate. Do not mark DONE or merge before GitHub Actions is green for the exact PR head.

## Exact next task
Inspect the B2.4a PR CI first. If CI fails, fix only the concrete failure. If CI is green, record the verified counts/head and merge only that green head, then verify post-merge main CI. Do not start another backend task before this gate completes.
