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

Status: IN_PROGRESS

Branch: `auto/b2-3-rfq-state-machine`

## Execution rule

One small task at a time: READY -> IN_PROGRESS -> CI_PENDING -> DONE. A task becomes DONE only after GitHub Actions passes.

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

### B1 API foundation/hardening — DONE
- B1.1-B1.4 and B1.H1-H3 completed; see B1 audit and repository history.

### B2.1 Customer/contact model — DONE
- PR #10; merge `33c1325e726aadcecbe8037916e28e75e1d35f5b`; 816/816 PASS.

### B2.2 RFQ create/read/list — DONE
- PR #11; merge `a3e78077b25f1396f2931b8d3707a071dc075065`.
- Post-merge main run `37383178770`: SUCCESS.
- Verified suite: **641 unit + 179 integration = 820/820 PASS**.
- Tenant-scoped create/get/list, existing status/customer/due-date filters and nullable early-draft inputs preserved.

## Current run findings

- Read current main plan/state/audit before new work.
- Verified the required post-merge B2.2 main workflow `37383178770` completed successfully for `a3e78077b25f1396f2931b8d3707a071dc075065`.
- B2.3 is therefore the smallest unblocked backend task in the plan.
- B2.3 requires explicit allowed transitions, invalid-transition rejection, append-only audit, and explicit readiness requirements. Existing repository status vocabulary/database constraints remain the source of truth; no pricing, approval threshold, customer, geometry, production or security policy may be inferred.
- Created task branch `auto/b2-3-rfq-state-machine` from verified main.

## Exact next task

**B2.3 implementation** — inspect the existing RFQ status constraints, audit-event model, repository/API contracts and tests; derive only transitions/readiness rules already encoded by repository requirements. If the repository does not define a transition or readiness policy required by the plan, record that exact portion as BLOCKED rather than inventing it. Implement the smallest deterministic state-machine slice that is fully supported by existing requirements, with focused tests, then publish a PR and require green GitHub Actions.
