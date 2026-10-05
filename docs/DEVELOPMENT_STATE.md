# Development State

Updated: 2026-10-05
Mode: autonomous backend-first development

## Baseline

- Repository migrated from Google Drive.
- Initial verified CI baseline: 638 unit + 152 integration = 790/790 passed.
- Current verified CI: **641 unit + 160 integration = 801/801 passed**.
- Current deterministic core is frozen unless a concrete regression requires changes.
- Full audit: docs/PROJECT_AUDIT_2026-10-05.md
- Development plan: docs/AUTONOMOUS_BACKEND_PLAN.md

## Current milestone

**B1 — API foundation and production boundaries**

Current task: **B1.4 Authorization seam**

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

## Completed work

### B1.1 ProblemDetails/error contract — DONE

- PR: #1 `B1.1: standardize API ProblemDetails errors`
- Merge: `958c04196ca70a2157f38f915f20c65d973483bb`
- Verified CI: **638 unit + 157 integration = 795/795 PASS**
- Added centralized exception mapping, consistent ProblemDetails, stable error codes, `correlation_id`, safe 500 responses and integration coverage.

### B1.2 OpenAPI — DONE

- PR: #2 `B1.2: add OpenAPI schema endpoint`
- Merge: `4b051a1a2e9ea8ae74e8e7f2231a354a33226884`
- Verified CI: **638 unit + 159 integration = 797/797 PASS**
- Added deterministic OpenAPI 3.0.3 document at `/openapi/v1.json`.
- Documented production RFQ upload/download and health operations.
- Dev-only golden endpoint and the schema endpoint itself are excluded.
- Schema generation is covered without requiring a live PostgreSQL connection.

### B1.3 Tenant context boundary — DONE

- PR: #3 `B1.3: add trusted tenant context boundary`
- Merge: `c4aaa2daed7814d89f04a1694ab64907895b59d6`
- Verified PR and post-merge main CI: **641 unit + 160 integration = 801/801 PASS**
- Added `ITenantContext` in Application and claim-based Host tenant resolution.
- Tenant identity is read from exactly one valid `tenant_id` claim on authenticated `HttpContext.User`.
- Route `tenantId` is only a resource identifier and is checked against the trusted context before repository access.
- Missing context returns 401 `TENANT_CONTEXT_REQUIRED`.
- Route/trusted tenant mismatch returns safe 404 `TENANT_RESOURCE_NOT_FOUND`.
- Added unit/integration coverage and updated OpenAPI tenant-bound responses.
- No external identity provider was selected; this remains intentionally deferred to B1.4.
- Primary reference: Microsoft ASP.NET Core documentation for `HttpContext.User` / ClaimsPrincipal and claim-based authorization.

## Next task

**B1.4 Authorization seam — IN_PROGRESS**

Add replaceable authorization policy hooks to tenant-bound RFQ endpoints, use deterministic test/development authentication only for tests/dev, and ensure unauthenticated or cross-tenant access is rejected before repository execution without selecting a vendor-specific production identity provider.
