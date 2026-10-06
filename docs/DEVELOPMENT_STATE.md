# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Verified `main` head before this run: `b8330dd2ec346aa5ec3f30dc5ea8bf17614e65fe`.
- GitHub Actions run `37435638607` for that head completed successfully.
- Last verified full suite after B3.4: **671 unit + 215 integration = 886/886 passed**.
- Frozen deterministic TimeEngineV1, StockEngineV1, CostEngineV1, canonical snapshots/hashes/replay, immutable RFQ file history and existing migration history remain unchanged.

## Current milestone
**B3 — AI RFQ extraction wired end-to-end**

### Completed
- B3.1 AI configuration + DI — DONE, PR #17.
- B3.2 deterministic AI input normalization — DONE, PR #18.
- B3.3 AI execution policy — DONE, PR #19.
- B3.4 RFQ extraction service — DONE, PR #20; merge `eda1601deddd9e640a756d1e8637733687c501f6`; post-merge run `37435364172` green, **886/886 PASS**.

### B3.5 Persist CanonicalRFQ draft — IN_PROGRESS
Branch: `auto/b3-5-canonical-rfq-draft`
PR: #21

Scope is limited to B3.5: immutable extraction-attempt/history persistence, source lineage, raw provider output retention, and a separate current CanonicalRFQ review draft. B3.6 review/correction APIs are not included.

## Current run findings
- Re-read current main plus `docs/AUTONOMOUS_BACKEND_PLAN.md`, this state file and `docs/PROJECT_AUDIT_2026-10-05.md`.
- Verified the preceding documentation-only main head `b8330dd2ec346aa5ec3f30dc5ea8bf17614e65fe` first; run `37435638607` succeeded.
- Selected exactly B3.5 as the smallest unblocked READY backend task.
- Added migration `010_rfq_extraction_history.sql` with tenant-scoped `rfq_extraction_attempts` and `rfq_canonical_drafts`.
- Extraction attempts are append-only at the database boundary; UPDATE/DELETE/TRUNCATE are rejected by triggers.
- Attempt history stores model/prompt/schema versions, request fingerprint, disposition/code, exact source-lineage JSON and raw provider output separately from the reviewed draft.
- The current CanonicalRFQ draft is stored in a separate tenant/RFQ row and points to the source extraction attempt. A new validated completed extraction replaces only the current draft and increments its row version; immutable attempt history is retained.
- Gateway result now retains provider raw JSON after the existing output guard evaluates it. Invalid/provider-failure paths do not create a CanonicalRFQ draft.
- Source lineage records exact logical key/version/document type and, when resolved, SHA-256/source reference. No implicit latest-version selection was introduced.
- MISSING/CONFLICT semantics remain inside the validated CanonicalRFQ v1 JSON; the schema and classifications were not broadened or rewritten.
- Added focused unit coverage that validated provider output is retained verbatim for history persistence.
- No B3.6 endpoint, pricing, margin, approval, workflow transition, geometry, calculation-engine, snapshot/hash/replay or frontend behavior was changed.

## CI state
- PR #21 is open: `auto/b3-5-canonical-rfq-draft` -> `main`.
- Earlier PR-head run `37437049355` was still running when additional focused test/state commits were published; therefore B3.5 is **not DONE** and must not be merged yet.
- Current branch head must receive a green GitHub Actions run before merge or DONE status.

## Known blockers
- B2.3 RFQ transition/readiness business policy is undefined.
- B2.4b post-analysis revision semantics depend on B2.3.
- Production identity provider is undefined; production auth remains fail-closed.
- PricingEngineV1 commercial policy is undefined.
- Geometry golden work lacks representative STEP fixtures/expected outputs.
- Repository is public; confidential customer/production data must not be committed.

## Exact next task
First inspect GitHub Actions for the exact current PR #21 head. If CI failed, fix only the concrete failure and rerun. If CI is green, perform a focused B3.5 persistence review, update plan/state with verified test counts, merge only while required CI is green, then verify post-merge `main` CI. Do not start B3.6 or frontend until B3.5 is green and merged.
