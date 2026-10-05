# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Initial verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current verified main CI: **641 unit + 175 integration = 816/816 passed**.
- Deterministic formulas/canonical hashes remain frozen unless a concrete failing test or versioned rule requires change.
- Full project audit: docs/PROJECT_AUDIT_2026-10-05.md
- B1 audit: docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B2 — Real RFQ backend**

Current task: **B2.2 RFQ create/read/list**

Status: CI_PENDING

Branch: `auto/b2-2-rfq-crud`
PR: #11
Head: `5cf75389e5b4f06d6f24f67bbfc68b5a5e919ec9`
CI: GitHub Actions run not yet visible immediately after PR creation; do not merge or mark DONE.

## Execution rule

One small task at a time:
READY -> IN_PROGRESS -> CI_PENDING -> DONE.

A task becomes DONE only after GitHub Actions passes.

## Known blockers

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

### B1.1 ProblemDetails/error contract — DONE
- PR #1; merge `958c04196ca70a2157f38f915f20c65d973483bb`; 795/795 PASS.
### B1.2 OpenAPI — DONE
- PR #2; merge `4b051a1a2e9ea8ae74e8e7f2231a354a33226884`; 797/797 PASS.
### B1.3 Tenant context boundary — DONE
- PR #3; merge `c4aaa2daed7814d89f04a1694ab64907895b59d6`; 801/801 PASS.
### B1.4 Authorization seam — DONE
- PR #4; merge `7251c6401fc93b8c64a8bf04ca931da30fc6e55f`; 807/807 PASS.
### B1 audit — DONE
- `docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md`.
### B1.H1 Calculation-run concurrent idempotency — DONE
- PR #6; merge `6a9338dc5b2a919adc8449e0ecbe7586c83db71b`; 808/808 PASS.
### B1.H2 Tenant API route-group guardrail — DONE
- PR #7; merge `1de2d6ea97f21749ccc0ed66bda545b8632abdd5`; 809/809 PASS.
### B1.H3 GitHub branch/CI guardrails — DONE with external blocker
- PR #9; 809/809 PASS; branch protection mutation remains external blocker issue #8.
### B2.1 Customer/contact model — DONE
- PR #10; merge `33c1325e726aadcecbe8037916e28e75e1d35f5b`; 816/816 PASS.

## Current run findings

- Continued the already-IN_PROGRESS B2.2 task rather than selecting new work.
- Added `IQuoteRequestRepository` and `QuoteRequestFilter` using only existing persisted RFQ fields.
- Added PostgreSQL `QuoteRequestRepository` with tenant-scoped create/find/list and deterministic list ordering.
- Create preserves migration 007 draft semantics: `New`, `DataReview`, and `Blocked` may omit part revision/quantity; progressed statuses require both.
- Added tenant-authorized POST/GET/list RFQ endpoints under the existing guarded tenant route group.
- List filtering is limited to existing status, customer, and requested due-date fields; no workflow/customer policy was invented.
- Added focused integration tests for nullable draft round-trip, tenant isolation, list filters, and progressed-status validation.
- PR #11 published. GitHub Actions had not appeared for head `5cf75389...` at the end of this run, so task remains CI_PENDING.

## Exact next task

**B2.2 CI follow-up** — inspect PR #11 GitHub Actions first. If failed, fix only those failures on `auto/b2-2-rfq-crud`. If green, update this state to DONE and merge only with the verified green head; then verify post-merge main CI before selecting B2.3 on a later run.
