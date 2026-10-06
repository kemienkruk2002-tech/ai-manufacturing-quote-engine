# B2 RFQ backend focused audit — 2026-10-06

## Scope and baseline

Audit scope: Milestone B2 RFQ backend after B2.1, B2.2, B2.4a and B2.5, with B2.3 and the post-analysis portion of B2.4 explicitly business-blocked.

Verified base:
- `main` merge: `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6` (B2.5 / PR #13).
- Post-merge main CI run `37414197830`: **641 unit + 183 integration = 824/824 PASS**.
- Audit branch: `auto/b2-audit`, PR #14.
- Frozen contracts reviewed but not changed: TimeEngineV1, StockEngineV1, CostEngineV1, canonical snapshots/hashes/replay, immutable RFQ file history and existing migrations.

This audit does not invent RFQ workflow, approval, pricing, customer, geometry, production or security policy.

## Database audit

### PASS — migration history and draft input constraints

- Migrations 001 through 009 apply from scratch.
- Migration 007 permits nullable part revision and requested quantity only for `New`, `DataReview` and `Blocked`.
- Progressed statuses require both part revision and requested quantity.
- Non-positive quantities remain rejected.
- Migration 009 adds positive `row_version` without rewriting prior migrations.

Evidence: `RfqDraftInputTests`, `QuoteRequestRepositoryTests`.

### PASS — customer/contact tenant integrity

- Customer/contact records are tenant scoped.
- Contact -> customer uses a composite `(tenant_id, customer_id)` foreign key.
- RFQ -> customer uses the same tenant-scoped composite relationship.
- Cross-tenant contact and RFQ/customer references are rejected by PostgreSQL.

Evidence: migration 008 and `CustomerRepositoryTests`.

### PASS — immutable RFQ file history

- Logical RFQ documents are tenant/RFQ scoped.
- File versions are append-only.
- UPDATE, DELETE and TRUNCATE of document/version history are rejected.
- Version number, byte size and SHA-256 constraints remain database-enforced.

Evidence: migration 006 and RFQ file integration tests.

### PASS — optimistic draft concurrency

- Editable draft updates are limited to `New` RFQs.
- Updates compare `row_version` and increment it.
- Two simultaneous writers using the same expected version produce exactly one winner.

Evidence: `QuoteRequestRepositoryTests.Simultaneous_draft_updates_allow_exactly_one_writer_for_same_version`.

## API audit

### PASS — tenant authorization boundary

All tenant RFQ routes are grouped under:

`/api/tenants/{tenantId:guid}`

and the group requires `TenantRfqAccess`.

Verified behavior:
- unauthenticated requests are rejected before repository access;
- identity without exactly one valid tenant claim is forbidden;
- route/trusted-tenant mismatch is hidden before repository access;
- Development tenant header is accepted only by the Development authentication scheme;
- Production ignores the Development tenant header and remains fail-closed.

Evidence: `Program.cs`, `ClaimsTenantContext`, `AuthorizationBoundaryTests`.

### PASS — RFQ workspace read/create/draft surface

Current API provides:
- create RFQ;
- get RFQ;
- list/filter RFQs by existing status/customer/due-date fields;
- optimistic `New` draft update;
- RFQ file upload;
- RFQ immutable-version download;
- RFQ workspace file manifest.

The file manifest preserves logical document grouping, immutable version ordering, SHA-256/size/MIME/file-name metadata and `source_reference`, and is tenant/RFQ scoped.

Evidence: `Program.cs`, `QuoteRequestRepositoryTests`, `RfqFileApiTests`, `RfqFileManifestRepositoryTests`.

## Historical replay audit

### PASS — stored historical snapshots remain replayable

Existing PostgreSQL integration coverage proves:
- a stored snapshot replays 100 times to the same snapshot hash, calculation hash and serialized result;
- repeated calculation persistence is idempotent;
- snapshot UPDATE/DELETE/TRUNCATE is rejected;
- changing current master data creates a new snapshot/hash while replay of the old stored snapshot preserves the old material data and old result;
- snapshot and calculation repositories do not expose the same hashes through another tenant.

Evidence: `GoldenPersistenceTests`.

No replay formula, canonical serialization or historical bytes were changed by B2.

## Audit findings

### B2.F1 — P1 / BLOCKED_BUSINESS_POLICY — RFQ transition graph is undefined

The repository has a status vocabulary and the progressed-input database invariant, but it does not define:
- allowed `from_status -> to_status` transitions;
- READY_FOR_ANALYSIS semantics/readiness predicate;
- exact readiness requirements beyond existing part/quantity constraints;
- terminal-state reopening rules.

Therefore B2.3 cannot be implemented deterministically without inventing business workflow.

Required input remains the versioned workflow specification already recorded in DEVELOPMENT_STATE.md.

### B2.F2 — P1 / READY — RFQ creation can currently select a progressed status

B2.2 requires **create draft RFQ**, but `CreateRfqRequest` currently accepts an optional status and passes it directly to the domain object. With sufficient part/quantity inputs, a caller can instantiate a progressed status without passing through the future transition/audit boundary.

This does not require inventing workflow policy to fix: creation can be constrained to the existing draft state `New`, leaving all later progression to B2.3.

Follow-up: **B2.H2 — draft-only RFQ creation guard**.

### B2.F3 — P1 / BLOCKED_BUSINESS_POLICY — post-analysis update/version boundary is undefined

B2.4a safely covers optimistic edits while status is `New`. The requested rule that changes after analysis create a new revision/snapshot depends on knowing exactly when analysis begins/locks the RFQ. That lifecycle boundary is part of the missing B2.3 policy.

Do not implement it until that boundary is supplied.

### B2.F4 — P1 / CI hardening — local object-store concurrent publication race

The first audit PR run `37415113531` exposed a real race in concurrent same-hash publication:
- integration tests: 183/183 PASS;
- unit tests: 640/641 PASS;
- failing test: `Concurrent_puts_of_same_hash_create_one_correct_object`.

A first repair using `FileMode.CreateNew` correctly elected one writer but made the final content-addressed path visible while bytes were still being copied. Audit review rejected that publication pattern because interrupted I/O could expose partial final content.

The audit branch now keeps the fully written/hash-verified temporary file and serializes publication through bounded striped in-process gates before atomically renaming the completed temporary file to the final content-addressed path.

This preserves the local store's existing immutable object contract. Multi-process/cloud object-store semantics remain explicitly deferred to B4.4.

Follow-up: **B2.H1 — local object-store atomic publication**, implemented in PR #14. Implementation/audit head `ec2f3a656b48a12f7b007b99c36f282a9770c4bd` passed run `37424052221` with 824/824 tests.

### B2.F5 — P2 / planned B4.4 — production storage boundary is still incomplete

The HTTP upload/download handlers still construct `LocalFileObjectStore` directly from configuration. The interface exists, but production-grade provider selection and integrity verification on reads are not yet wired.

This is already covered by B4.4 and is not duplicated as a new B2 task.

### B2.F6 — P2 / documentation drift

The plan still showed B2.2 as READY and did not reflect completed B2.4a/B2.5. DEVELOPMENT_STATE on main also lagged the B2.5 merge.

The audit updates the plan/state so later autonomous runs select tasks from current repository reality rather than stale labels.

## Follow-up order

1. **B2.H1 — local object-store atomic publication** — implementation in PR #14; require green exact-head CI.
2. **B2.H2 — draft-only RFQ creation guard** — READY after the audit PR is green/merged.
3. **B2.3 — RFQ deterministic state machine** — BLOCKED_BUSINESS_POLICY.
4. **B2.4b — post-analysis revision/snapshot semantics** — BLOCKED_BUSINESS_POLICY on the same lifecycle decision.
5. After B2.H1/H2 are green, a later run may start the next independent backend milestone task (B3.1) while B2.3/B2.4b remain explicitly blocked.

## Audit outcome

The implemented B2 data integrity, tenant isolation, draft concurrency, immutable file history and historical deterministic replay are supported by the current integration suite.

B2 is **not** a complete RFQ lifecycle yet because the state-machine and post-analysis lifecycle boundary require business decisions. Those blockers are explicit rather than guessed.

The audit implementation/content head `ec2f3a656b48a12f7b007b99c36f282a9770c4bd` is **GREEN** in run `37424052221` (641 unit + 183 integration = 824/824). Subsequent documentation-only status commits must also be green before PR #14 is merged.
