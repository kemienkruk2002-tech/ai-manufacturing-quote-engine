# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Initial verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current verified CI: **641 unit + 168 integration = 809/809 passed**.
- Deterministic formulas/canonical hashes remain frozen unless a concrete failing test or versioned rule requires change.
- Full project audit: docs/PROJECT_AUDIT_2026-10-05.md
- B1 audit: docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B2 — Real RFQ backend**

Current task: **B2.1 Customer/contact model**

Status: IN_PROGRESS

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
- Tenant RFQ endpoints use named `TenantRfqAccess` authorization.
- Production default auth fails closed.
- Development auth is explicit and Production ignores the dev header.
- Test auth exists only in integration tests.
- 401/403/cross-tenant 404 are proven to occur before repository access.
- Primary references: current Microsoft ASP.NET Core Minimal API and policy-based authorization guidance.

### B1 audit — DONE

- Audit: `docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md`.
- API consistency, leakage boundary and tenant isolation passed.
- P1 finding: concrete intermittent duplicate-PK race in concurrent calculation-run persistence.
- P1 finding: tenant authorization is currently opt-in per endpoint and should become a route-group guardrail before B2.
- P1 operational finding: `main` is unprotected.
- Production OIDC/JWT provider remains intentionally blocked rather than guessed.





### B1.H1 Calculation-run concurrent idempotency — DONE

- PR #6 `B1.H1: harden calculation run idempotency`.
- Merge: `6a9338dc5b2a919adc8449e0ecbe7586c83db71b`.
- Verified PR and post-merge main CI: **641 unit + 167 integration = 808/808 PASS**.
- Replaced targeted calculation-run conflict arbitration with `ON CONFLICT DO NOTHING` so concurrent conflicts on either the deterministic primary key or natural unique key are handled.
- A skipped insert is reused only after exact natural-key and deterministic-ID verification.
- Different full calculation hashes that map to the same stable UUID are rejected with `CALCULATION_ID_COLLISION`.
- Deterministic formulas, canonical serialization and calculation hash generation are unchanged.
- Concurrency coverage now executes 4 rounds x 8 simultaneous equal calculations.
- Added explicit stable-ID collision coverage.
- Primary reference: PostgreSQL `INSERT ... ON CONFLICT` documentation.





### B1.H2 Tenant API route-group guardrail — DONE

- PR #7 `B1.H2: enforce tenant authorization at route-group boundary`.
- Merge: `1de2d6ea97f21749ccc0ed66bda545b8632abdd5`.
- Verified PR and post-merge main CI: **641 unit + 168 integration = 809/809 PASS**.
- Tenant-scoped HTTP routes now live under one `/api/tenants/{tenantId:guid}` route group.
- `TenantRfqAccess` is applied once at the group boundary and inherited by RFQ endpoints.
- Per-endpoint authorization duplication was removed.
- Existing trusted tenant mismatch checks remain inside handlers.
- Added a metadata guard test that fails if any `/api/tenants/*` endpoint lacks `TenantRfqAccess`.
- The same test asserts `/health` and `/openapi/v1.json` stay public intentionally.
- Existing RFQ route shapes and OpenAPI output remain unchanged.
- Primary reference: Microsoft ASP.NET Core Minimal APIs route-group guidance for group-level `RequireAuthorization`.





### B1.H3 GitHub branch/CI guardrails — DONE with external blocker

- PR #9 `B1.H3: clean up CI workflow and track main protection blocker`.
- Verified PR CI: **641 unit + 168 integration = 809/809 PASS**.
- Workflow renamed to `Quote Engine CI`; stale `stage1` test/artifact labels removed.
- Workflow retains least-privilege `permissions: contents: read`.
- Required check context is confirmed as `test`.
- Repository owner permission is admin, but the available GitHub connector does not expose branch-protection/ruleset mutation.
- Branch protection therefore remains **not configured** and is tracked explicitly by issue #8.
- Issue #8 defines the minimum admin action: require `test`, disable force pushes, disable deletion, then verify `main` reports `protected: true`.
- GitHub documentation confirms required status checks gate merges and protected branches/rulesets can block force-push/deletion.
- This external configuration blocker does not block backend B2 development; until resolved, this development loop continues enforcing PR -> green CI -> merge manually.

## Next task

**B2.1 Customer/contact model — IN_PROGRESS**

Add the smallest tenant-scoped customer/contact persistence slice required by the RFQ backend: additive migration, minimal domain/application records and repository coverage. Do not invent CRM, pricing, approval or sales fields that are not required by the current RFQ workflow.
