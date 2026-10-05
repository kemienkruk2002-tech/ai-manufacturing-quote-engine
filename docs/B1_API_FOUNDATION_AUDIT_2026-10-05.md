# B1 API Foundation Audit — 2026-10-05

## Scope

Milestone B1 covered:
- B1.1 ProblemDetails/error contract;
- B1.2 OpenAPI;
- B1.3 trusted tenant context boundary;
- B1.4 authentication/authorization seam.

Audit criteria: API consistency, information leakage, tenant isolation, authorization ordering, regression safety and CI evidence.

## Verified result

B1 is functionally complete as an API-foundation milestone.

Verified post-merge main state after B1.4:
- **641 unit + 166 integration = 807/807 PASS**;
- production tenant RFQ endpoints require authorization;
- production default authentication is fail-closed;
- development authentication is explicit and ignored in Production;
- tenant identity is derived from authenticated claims, never from the route alone;
- route/trusted-tenant mismatch is rejected before repository access;
- unauthenticated and malformed tenant principals are rejected before repository access;
- API errors use ProblemDetails with stable codes and correlation IDs;
- unexpected exceptions do not expose internal exception messages;
- OpenAPI can be generated without a live database;
- dev-only golden endpoint is not part of the production OpenAPI document.

## Security and isolation findings

### PASS — production fails closed

When no real production identity provider is configured, the production authentication scheme returns no identity. Protected RFQ endpoints therefore return 401 rather than becoming anonymously accessible.

The development header authentication scheme is selected only in Development. An integration test proves that Production ignores `X-Dev-Tenant-Id`.

### PASS — tenant route is not trusted identity

`tenantId` in the route is treated only as a resource identifier. `ITenantContext` resolves the trusted tenant from the authenticated principal.

The route tenant is compared with the trusted tenant before any RFQ repository call. Mismatch returns a safe 404 to avoid cross-tenant existence disclosure.

### PASS — authorization happens before data access

Tests with a counting repository prove:
- no authentication => 401, zero repository calls;
- authenticated principal without a valid tenant claim => 403, zero repository calls;
- cross-tenant route => 404, zero repository calls;
- matching authenticated tenant reaches the repository.

### PASS — error leakage boundary

Known domain/application failures are mapped to bounded ProblemDetails. Unknown failures return a generic 500 and are logged by exception type/code/correlation ID without returning the internal message to the caller.

## Follow-up findings

### P1 — calculation-run concurrent idempotency race

Observed during B1.4 verification on unchanged deterministic-core code:

`SnapshotAssociationTests.Concurrent_same_request_calculations_create_one_snapshot_link_run_and_operation_set`

failed once with PostgreSQL:

`23505: duplicate key value violates unique constraint "calculation_runs_pkey"`

The exact same SHA passed on rerun.

This is not an authorization regression, but it is a concrete failing concurrency test and therefore qualifies under the frozen-core exception.

Root-cause candidate from code inspection:
- `CalculationRunRepository.SaveAsync` derives the primary key deterministically from `CalculationHash`;
- the insert uses `ON CONFLICT (tenant_id,quote_snapshot_id,engine_version,calculation_hash) DO NOTHING`;
- concurrent equal calculations can also collide on the deterministic primary key;
- the specified conflict target does not cover a primary-key conflict.

Do not blindly change the SQL. The repair must preserve collision detection for genuinely different calculation hashes that happen to map to the same stable UUID.

Next task: **B1.H1 Calculation-run concurrent idempotency**.

Acceptance:
- reproduce/stress concurrent equal calculations over multiple rounds;
- no duplicate-key exception;
- exactly one calculation run and one operation-result set;
- same hash/result replay remains deterministic;
- stable-ID collision with different content must not be silently accepted;
- full CI green.

### P1 — tenant authorization is currently opt-in per endpoint

Current RFQ routes each call `.RequireAuthorization(TenantRfqAccess)`. This is correct today but omission-prone when B2 adds more tenant-scoped endpoints.

Next task: **B1.H2 Tenant API route-group guardrail**.

Acceptance:
- tenant-scoped routes inherit the policy from one route group/convention;
- current RFQ upload/download behavior stays unchanged;
- metadata test proves every endpoint under the tenant group carries the policy;
- public `/health` and `/openapi/v1.json` remain public intentionally;
- full CI green.

### P1 operational — main branch remains unprotected

The repository's `main` branch is still unprotected. CI is being respected by this development loop, but GitHub itself does not enforce it.

Next operational task: **B1.H3 GitHub branch/CI guardrails**.

Target:
- require the CI test check before merge;
- block force-push/deletion of `main`;
- rename stale workflow title `Stage 1 deterministic core` to a project-level CI name;
- do not weaken repository permissions.

This requires repository-admin support from the connector. If unsupported, keep it documented as an external configuration blocker.

### P0 deployment blocker — real production identity provider not selected

B1 intentionally does not guess Microsoft Entra ID, Auth0, Keycloak, another OIDC issuer, or custom JWT infrastructure.

Current behavior is safe but deliberately unusable for authenticated production traffic until a provider is configured.

Required deployment inputs:
- issuer/authority;
- audience/resource identifier;
- authentication protocol/scheme;
- mapping that produces exactly one trusted `tenant_id` claim;
- signing-key/metadata rotation strategy;
- user/service identity model.

This does **not** block backend B2 development because tests have a dedicated test-only scheme and Production remains fail-closed.

### P2 — OpenAPI does not yet advertise a standard security scheme

401/403 responses are documented, but the document does not emit a standard OpenAPI `securitySchemes` definition because the production authentication protocol has intentionally not been selected.

Resolve together with the production identity-provider decision; do not invent a Bearer/OIDC contract now.

### P2 — repository is public

The repository remains public. No credentials or real customer RFQs/drawings/prices may be committed. Before importing any confidential production data, visibility and data-handling policy must be explicitly resolved.

## B1 exit decision

B1.1-B1.4 are DONE.

Before B2 feature development, execute unblocked P1 engineering findings in this order:
1. B1.H1 calculation-run concurrent idempotency;
2. B1.H2 tenant API route-group guardrail;
3. B1.H3 GitHub branch/CI guardrails when supported.

Then start B2.1 customer/contact model.

The production identity-provider and OpenAPI security-scheme items remain blocked on deployment decisions and must not be guessed.
