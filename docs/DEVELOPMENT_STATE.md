# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Full verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current deterministic core is frozen unless a concrete regression requires changes.
- Full audit: docs/PROJECT_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B1 — API foundation and production boundaries**

Current task: **B1.3 Tenant context boundary**

Status: IN_PROGRESS

## Execution rule

One small task at a time:
READY -> IN_PROGRESS -> CI_PENDING -> DONE.

A task becomes DONE only after GitHub Actions passes.

After B1.1-B1.4, run B1 audit before opening B2.

## Known blockers

PricingEngineV1 is BLOCKED on business policy. Do not guess it.
Geometry golden work is BLOCKED until representative STEP fixtures/expected outputs are supplied or a safe public fixture strategy is explicitly adopted.

## Safety constraints

- backend only until frontend gate;
- never commit credentials/secrets/customer confidential files;
- do not weaken immutable history or tenant isolation;
- no silent AI fallback into price/time/cost;
- no destructive migration rewrites after application;
- prefer new versioned behavior over mutating historical rules.


### Completed work

- B1.1 ProblemDetails/error contract — DONE
- PR: #1 `B1.1: standardize API ProblemDetails errors`
- Merge: `958c04196ca70a2157f38f915f20c65d973483bb`
- Verified CI: **638 unit + 157 integration = 795/795 PASS**
- Added centralized exception mapping, consistent ProblemDetails, stable error codes, `correlation_id`, safe 500 responses and integration coverage.
- Reference: current Microsoft ASP.NET Core guidance for `AddProblemDetails`, `UseExceptionHandler` and `IExceptionHandler`.

### Next task

**B1.3 Tenant context boundary — IN_PROGRESS**

Implement API metadata/OpenAPI for production routes, exclude the dev-only golden endpoint from production documentation, add schema smoke coverage, then run full CI.




### Completed work

- B1.2 OpenAPI — DONE
- PR: #2 `B1.2: add OpenAPI schema endpoint`
- Merge: `4b051a1a2e9ea8ae74e8e7f2231a354a33226884`
- Verified PR CI: **638 unit + 159 integration = 797/797 PASS**
- Verified post-merge `main` CI: PASS
- Added deterministic OpenAPI 3.0.3 document at `/openapi/v1.json`.
- Documented production RFQ upload/download and health operations.
- Dev-only golden endpoint and the schema endpoint itself are excluded from the document.
- Added smoke coverage proving schema generation succeeds without a live PostgreSQL connection.
- No new NuGet package, no migration change, no deterministic domain-core change.
- Primary reference: Microsoft ASP.NET Core .NET 8 OpenAPI/API Explorer guidance.

### Next task

**B1.3 Tenant context boundary — READY**

Introduce `ITenantContext` as the trusted application boundary for tenant identity, retain route tenant IDs only as resource identifiers/checks, and add mismatch coverage without choosing a real external identity provider yet.
