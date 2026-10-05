# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Initial verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current verified main CI before B2.2: **641 unit + 175 integration = 816/816 passed**.
- B2.2 verified PR CI: **641 unit + 179 integration = 820/820 passed**.
- Deterministic formulas/canonical hashes remain frozen unless a concrete failing test or versioned rule requires change.
- Full project audit: docs/PROJECT_AUDIT_2026-10-05.md
- B1 audit: docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B2 — Real RFQ backend**

Current task: **B2.2 RFQ create/read/list**

Status: DONE_PR_GREEN

Branch: `auto/b2-2-rfq-crud`
PR: #11
CI: run `37377922203` completed successfully for head `b234374c49a18b0ef51bf5b53b1a524d0be7b8b9`; `test` executed on a GitHub-hosted runner and passed 641 unit + 179 integration = 820/820, 0 failed, 0 skipped.

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
### B2.2 RFQ create/read/list — DONE (green PR head)
- PR #11.
- Verified run `37377922203`: **641 unit + 179 integration = 820/820 PASS**.
- Added tenant-scoped RFQ create/get/list persistence and API surface.
- Reused existing status vocabulary and database-backed draft/progress constraints; no workflow policy was invented.
- Preserved nullable part revision, requested quantity and customer for early draft semantics.
- Added filters only for existing status/customer/due-date fields and focused persistence/API coverage.

## Current run findings

- Started by inspecting the previously blocked PR #11 before any new task.
- The runner-availability blocker cleared: run `37377922203` received a hosted runner and executed the complete workflow successfully.
- CI passed 641 unit tests and 179 real PostgreSQL integration tests, 820/820 total, with no failures or skips.
- Expected PostgreSQL errors in the service log are negative-path integrity tests and did not fail the suite.
- No B2.3 work was started in this run; only the already-implemented B2.2 slice was verified and its state recorded.

## Exact next task

**Post-merge B2.2 verification** — merge PR #11 only at the green verified head after this state commit receives green CI as part of the same PR. Then verify `main` GitHub Actions. Only after green post-merge main CI may a later run select B2.3 RFQ deterministic state machine.
