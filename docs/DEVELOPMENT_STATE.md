# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Current verified main CI: **641 unit + 179 integration = 820/820 passed**.
- B2.2 merge: `a3e78077b25f1396f2931b8d3707a071dc075065`; post-merge main run `37383178770` succeeded.
- Deterministic formulas/canonical hashes remain frozen unless a concrete failing test or versioned rule requires change.
- Full project audit: docs/PROJECT_AUDIT_2026-10-05.md
- B1 audit: docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B2 — Real RFQ backend**

Current task: **B2.3 RFQ deterministic state machine**

Status: BLOCKED_BUSINESS_POLICY

Branch: `auto/b2-3-rfq-state-machine`

## Execution rule

One small task at a time: READY -> IN_PROGRESS -> CI_PENDING -> DONE. A task becomes DONE only after GitHub Actions passes.

## Known blockers

- **B2.3 transition graph/readiness policy is not defined by repository requirements.** The repository defines the status vocabulary and only one progressed-input invariant, but no allowed status-to-status transition graph and no READY_FOR_ANALYSIS concept/readiness requirements. Do not invent these workflow rules.
- Production identity provider is BLOCKED on deployment decisions; Production authentication remains fail-closed.
- PricingEngineV1 is BLOCKED on business policy. Do not guess it.
- Geometry golden work is BLOCKED until representative STEP fixtures/expected outputs are supplied or a safe public fixture strategy is explicitly adopted.
- Repository visibility is public; never commit confidential customer/production data.

## Safety constraints

- backend only until frontend gate;
- never commit credentials/secrets/customer confidential files;
- do not weaken immutable history or tenant isolation;
- no silent AI fallback into price/time/cost;
- no destructive migration rewrites after application;
- preserve deterministic calculation formulas and hashes;
- prefer new versioned behavior over mutating historical rules.

## Completed work

### B1 API foundation/hardening — DONE
- B1.1-B1.4 and B1.H1-H3 completed; see B1 audit and repository history.

### B2.1 Customer/contact model — DONE
- PR #10; merge `33c1325e726aadcecbe8037916e28e75e1d35f5b`; 816/816 PASS.

### B2.2 RFQ create/read/list — DONE
- PR #11; merge `a3e78077b25f1396f2931b8d3707a071dc075065`.
- Post-merge main run `37383178770`: SUCCESS.
- Verified suite: **641 unit + 179 integration = 820/820 PASS**.

## Current run findings

- Continued the already-started B2.3 task; no newer PR/task existed to inspect first.
- Inspected the current RFQ domain, persistence, migrations, audit schema and plan before changing runtime code.
- `QuoteStatus` is exactly: `New`, `DataReview`, `ReadyForCalc`, `Calculated`, `Approved`, `Sent`, `Lost`, `Won`, `Blocked`.
- Migration 007 defines only this readiness-like invariant: statuses other than `New`, `DataReview`, and `Blocked` require both `part_revision_id` and `requested_quantity`.
- The repository contains no `READY_FOR_ANALYSIS` status or equivalent explicit state, and no repository requirement defining when analysis is ready.
- No allowed transition graph is encoded in code, SQL constraints, tests, or the autonomous plan. Inferring e.g. `New -> DataReview -> ReadyForCalc -> Calculated -> Approved -> Sent` would be manufacturing/approval workflow policy and is prohibited by the operating rules.
- Existing migration 004 audits only the special transition into `Approved` as `QUOTE_APPROVED`; it does not define or audit every RFQ transition. Audit history itself is append-only and must remain so.
- Because explicit allowed transitions are a core B2.3 acceptance item, implementing a generic status-update endpoint without the graph would weaken the requested deterministic boundary. B2.3 is therefore blocked rather than partially inventing policy.
- No runtime code, migrations, deterministic engines, snapshots/hashes/replay, immutable histories or tenant isolation were changed in this run.

## Exact blocker to resolve B2.3

Provide/version a repository-backed RFQ workflow specification containing:
1. allowed `from_status -> to_status` transitions for the existing nine statuses;
2. whether `READY_FOR_ANALYSIS` is a new status, an alias/meaning of `DataReview`, or a separate readiness predicate;
3. exact requirements for readiness for analysis;
4. exact requirements for `ReadyForCalc` beyond the already-defined non-null part revision + positive quantity;
5. whether terminal states (`Lost`, `Won`, and/or `Sent`) may transition again and under what explicit rules.

Until those decisions exist, B2.3 must not be marked DONE.

## Exact next task

On a later autonomous run, first check whether B2.3 policy has been supplied/versioned. If yes, implement B2.3 with focused transition/audit/readiness tests. If not, leave B2.3 blocked and select the next independent unblocked backend task allowed by the plan rather than inventing workflow policy.
