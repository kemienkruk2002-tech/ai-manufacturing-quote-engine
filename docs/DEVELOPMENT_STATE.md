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

Status: CI_PENDING

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



### B1.3 implementation pending CI

- Branch: `auto/b1-3-tenant-context`
- Added `ITenantContext` in Application and a claim-based Host implementation using authenticated `HttpContext.User`.
- Tenant identity is read from exactly one non-empty GUID `tenant_id` claim; route `tenantId` is no longer trusted.
- RFQ file routes compare route tenant to trusted tenant before repository access.
- Missing trusted context => 401 `TENANT_CONTEXT_REQUIRED`.
- Route/trusted tenant mismatch => safe 404 `TENANT_RESOURCE_NOT_FOUND`.
- Added unit guard tests and integration coverage for missing context and mismatch.
- Updated OpenAPI responses for tenant-bound endpoints.
- Existing integration factories inject a deterministic test tenant context only in tests.
- No external identity provider selected; authentication/authorization policy remains B1.4.
- Primary reference: Microsoft ASP.NET Core guidance that `HttpContext.User` is the request `ClaimsPrincipal` populated by authentication.
