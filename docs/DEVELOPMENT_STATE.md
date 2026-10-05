# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Initial verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current verified CI: **641 unit + 175 integration = 816/816 passed**.
- Deterministic formulas/canonical hashes remain frozen unless a concrete failing test or versioned rule requires change.
- Full project audit: docs/PROJECT_AUDIT_2026-10-05.md
- B1 audit: docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B2 — Real RFQ backend**

Current task: **B2.2 RFQ create/read/list**

Status: IN_PROGRESS

Branch: `auto/b2-2-rfq-crud`

## Execution rule

One small task at a time:
READY -> IN_PROGRESS -> CI_PENDING -> DONE.

A task becomes DONE only after GitHub Actions passes.

Unblocked P0/P1 audit findings are fixed before B2 feature development.

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
- PR #1; merge `958c04196ca70a2157f38f915f20c65d973483bb`.
- Verified 795/795 PASS.

### B1.2 OpenAPI — DONE
- PR #2; merge `4b051a1a2e9ea8ae74e8e7f2231a354a33226884`.
- Verified 797/797 PASS.

### B1.3 Tenant context boundary — DONE
- PR #3; merge `c4aaa2daed7814d89f04a1694ab64907895b59d6`.
- Verified 801/801 PASS.

### B1.4 Authorization seam — DONE
- PR #4 `B1.4: add replaceable authorization seam`.
- Merge: `7251c6401fc93b8c64a8bf04ca931da30fc6e55f`.
- Verified PR and post-merge main CI: **641 unit + 166 integration = 807/807 PASS**.

### B1 audit — DONE
- Audit: `docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md`.

### B1.H1 Calculation-run concurrent idempotency — DONE
- PR #6; merge `6a9338dc5b2a919adc8449e0ecbe7586c83db71b`.
- Verified **808/808 PASS**.

### B1.H2 Tenant API route-group guardrail — DONE
- PR #7; merge `1de2d6ea97f21749ccc0ed66bda545b8632abdd5`.
- Verified **809/809 PASS**.

### B1.H3 GitHub branch/CI guardrails — DONE with external blocker
- PR #9; verified **809/809 PASS**.
- Branch protection mutation remains external blocker issue #8.

### B2.1 Customer/contact model — DONE
- PR #10 `B2.1: add minimal customer and contact model`.
- Merge: `33c1325e726aadcecbe8037916e28e75e1d35f5b`.
- Verified PR and post-merge main CI: **641 unit + 175 integration = 816/816 PASS**.
- Added migration 008 with tenant-scoped `customers` and `customer_contacts`.
- Added nullable `quote_requests.customer_id` with composite tenant FK.
- Added minimal domain records and `ICustomerRepository` without invented CRM fields.

## Current run findings

- No open PR or pending prior task was found; current `main` already records B2.1 as merged with green post-merge CI.
- B2.2 is the smallest unblocked READY backend task.
- Existing RFQ status vocabulary is database-backed: `New`, `DataReview`, `ReadyForCalc`, `Calculated`, `Approved`, `Sent`, `Lost`, `Won`, `Blocked`; B2.2 will reuse it and will not invent workflow transitions.
- Migration 007 explicitly permits nullable part revision and quantity for `New`, `DataReview`, and `Blocked`; B2.2 create semantics will preserve that existing rule.
- Tenant-scoped API routes inherit `TenantRfqAccess` at the route-group boundary and handlers must continue resolving trusted tenant identity through `ITenantContext`.

## Next task

**B2.2 RFQ create/read/list — IN_PROGRESS**

Implement one focused RFQ CRUD slice on `auto/b2-2-rfq-crud`: repository/domain contract plus tenant-authorized create/get/list endpoints, filtering only by existing status/date/customer fields, preserving nullable draft inputs. Add focused persistence/API tests, then publish PR and require green GitHub Actions before marking DONE.
