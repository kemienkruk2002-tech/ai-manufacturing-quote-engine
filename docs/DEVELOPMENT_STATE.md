# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI after B2.4a: **641 unit + 181 integration = 822/822 passed**.
- B2.4a merge: `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; post-merge run `37398187730` succeeded.
- Frozen deterministic engines/snapshots/hashes/replay and existing migration history remain unchanged.

## Current milestone
**B2 — Real RFQ backend**

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
Repository defines status vocabulary and the progressed-input invariant, but no allowed transition graph or READY_FOR_ANALYSIS/readiness policy. Blocker is recorded on `auto/b2-3-rfq-state-machine` at `24b48575d8a72296540e4698f3442c65aad4edd0`. Do not invent workflow policy.

### B2.4a Draft optimistic concurrency — DONE
PR #12; merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; post-merge run `37398187730` succeeded; verified **822/822 PASS**.

### B2.5 File manifest endpoints — CI_PENDING
Branch: `auto/b2-5-file-manifest`

Implemented scope:
- tenant-scoped manifest reader over existing immutable RFQ document/version tables;
- groups all logical documents for one RFQ and returns every immutable version in version order;
- preserves existing metadata including file name, MIME type, byte size, SHA-256, creation time and `source_reference`;
- `GET /api/tenants/{tenantId}/rfqs/{quoteRequestId}/files` returns the workspace manifest and returns 404 for an RFQ outside the trusted tenant scope;
- no migration or file-history mutation semantics changed.

## Known blockers
- B2.3: RFQ transition/readiness business policy missing.
- B2.4 post-analysis revision semantics depend on the undefined B2.3 lifecycle boundary.
- Production identity provider: deployment decision missing; production auth remains fail-closed.
- PricingEngineV1: commercial policy missing.
- Geometry golden work: representative STEP fixtures/expected outputs missing.
- Repository is public; no confidential customer/production data may be committed.

## Completed work
- B1 foundation/hardening complete; see B1 audit/history.
- B2.1 customer/contact model: DONE, PR #10, 816/816 PASS.
- B2.2 RFQ create/read/list: DONE, PR #11, merge `a3e78077b25f1396f2931b8d3707a071dc075065`, post-merge 820/820 PASS.
- B2.4a draft optimistic concurrency: DONE, PR #12, merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`, post-merge 822/822 PASS.

## Current run findings
- Read current main plan, development state and project audit before changing code.
- Verified actual `main` is `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; the previously described `auto/b2-5-rfq-files-api` branch did not exist, so this run created the real task branch `auto/b2-5-file-manifest` from verified main.
- B2.5 is the smallest independent unblocked B2 task because existing upload/download and immutable version persistence already exist; the missing acceptance surface is a workspace-level manifest across logical documents.
- Added `RfqFileManifestDocument` and a read-only `IRfqFileManifestRepository`; no new storage policy was introduced.
- Added `RfqFileManifestRepository`, querying existing tenant-scoped document/version tables and preserving immutable version metadata/source references.
- Added focused integration tests for grouping/version ordering/source references and tenant/RFQ isolation.
- Added the authorized tenant-group manifest endpoint and DI registration.
- No pricing, margin, approval, customer, geometry, production or security policy was invented. No deterministic engine, canonical snapshot/hash/replay behavior, migration history, or immutable file history was modified.

## CI state
- B2.5 implementation is published on `auto/b2-5-file-manifest` and is **CI_PENDING** until GitHub Actions runs on the PR head.
- Do not mark DONE or merge until the exact PR head is green.

## Exact next task
Open/inspect the B2.5 PR and GitHub Actions. If CI fails, fix only the concrete failure. If green, record the verified counts/head, merge only that green head, and verify post-merge `main` CI. Only then run the B2 focused audit; B2.3/B2.4 post-analysis semantics remain blocked unless workflow policy is supplied.
