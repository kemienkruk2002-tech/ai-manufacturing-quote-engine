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

Status: BLOCKED_CI_INFRASTRUCTURE

Branch: `auto/b2-2-rfq-crud`
PR: #11
CI: two consecutive PR heads have failed to obtain a GitHub-hosted runner. Run `37364396106` and retrigger run `37370264575` each waited about 15 minutes, then the only `test` job was cancelled with `runner_id=0` and zero executed steps. No code or test failure has executed. Do not merge or mark DONE until GitHub Actions actually runs and is green.

## Execution rule

One small task at a time:
READY -> IN_PROGRESS -> CI_PENDING -> DONE.

A task becomes DONE only after GitHub Actions passes.

## Known blockers

- **GitHub Actions runner availability currently blocks B2.2 completion:** two consecutive PR runs were cancelled before runner assignment; no steps executed.
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

- Continued B2.2 because PR #11 is still open and required CI verification before any new task.
- Latest head `c15f233e6059a6789683c43e13da568121ee085e` produced run `37370264575`.
- Run `37370264575` did not execute tests: its only `test` job was cancelled after approximately 15 minutes with `runner_id=0`, empty runner name, and `steps=[]`.
- This independently reproduces the same infrastructure symptom as previous run `37364396106`; therefore there is still no concrete code/test failure to repair.
- Repeated state-only retriggers are no longer useful; B2.2 is explicitly blocked on GitHub-hosted runner availability.
- No production/domain code, deterministic engines, snapshots, hashes, histories, migration history, tenant isolation, or policy was changed in response to the infrastructure failure.

## Exact next task

**B2.2 CI follow-up** — inspect PR #11 and the newest workflow run first. Do not start B2.3 while B2.2 is unverified. If a workflow actually receives a runner and fails, fix only the concrete failure. If it executes green, update this state to DONE and merge exactly that verified head, then verify post-merge main CI before selecting the next task on a later run.
