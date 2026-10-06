# Autonomous Backend Development Plan

Status: ACTIVE
Started: 2026-10-05
Scope: backend-first. Do not start frontend until the backend gates below are complete.
Source of truth: repository code/tests + docs/PROJECT_AUDIT_2026-10-05.md + this file.

## Operating model

Development proceeds in small, independently testable increments. Each increment must:
1. read the current state before changing code;
2. choose the smallest unblocked task;
3. research current primary technical references when needed;
4. implement only that task and its required dependencies;
5. add or update tests;
6. push to GitHub;
7. verify GitHub Actions;
8. update DEVELOPMENT_STATE.md;
9. never mark a task DONE unless CI is green;
10. after every completed milestone, perform a focused audit and generate the next small milestone.

Frozen unless a failing test or versioned business rule requires a change:
- TimeEngineV1
- StockEngineV1
- CostEngineV1
- canonical snapshot/hash/replay
- W07044 golden case
- immutable RFQ file history
- CanonicalRFQ v1 contracts
- existing migration history

Never invent business policy. If an item requires unknown pricing, margin, approval, customer or production rules, mark it BLOCKED and continue with the next independent backend task.

## Milestone B1 — API foundation and production boundaries

Goal: make Host a stable production API surface before adding more business flows.

### B1.1 ProblemDetails/error contract
- introduce one consistent production error envelope;
- map DomainValidationException and MissingSnapshotInputException;
- map known RFQ file errors without leaking internals;
- include correlation_id;
- preserve current endpoint behavior where contractually required;
- integration tests for 400/404/409/422/500-safe paths.

Acceptance: all production endpoints return predictable machine-readable errors and CI remains green.

### B1.2 OpenAPI
- enable API metadata/OpenAPI for production routes;
- document RFQ file endpoints and health;
- exclude dev-only golden endpoint from production docs;
- smoke test schema generation.

Acceptance: API schema can be generated without starting external services other than configured test DB where required.

### B1.3 Tenant context boundary
- stop treating arbitrary URL tenantId as trusted identity;
- add ITenantContext abstraction;
- retain explicit route tenant only as resource identifier/check;
- test mismatch behavior;
- no real external identity provider yet.

Acceptance: application services receive tenant identity from a single trusted abstraction.

### B1.4 Authorization seam
- add authorization policy hooks around tenant RFQ endpoints;
- make auth provider replaceable;
- provide deterministic test authentication only in tests/development;
- do not hard-code vendor-specific auth business logic into Domain/Application.

Acceptance: unauthorized/cross-tenant access is rejected before repository execution.

### B1 audit — DONE

Audit: `docs/B1_API_FOUNDATION_AUDIT_2026-10-05.md`

B1.1-B1.4 are complete. The audit produced the following hardening tasks that run before B2.

### B1.H1 Calculation-run concurrent idempotency — DONE

A concrete CI failure exposed a race in concurrent equal calculation persistence.

- stress repeated concurrent saves/calculations;
- make equal calculation writes idempotent across both deterministic primary key and natural unique key;
- preserve detection of any stable-ID collision involving different calculation content;
- exactly one calculation run and one operation-result set;
- do not alter calculation formulas or canonical hashes;
- full CI green.

### B1.H2 Tenant API route-group guardrail — DONE

After H1:
- group all tenant-scoped HTTP routes under one tenant route group/convention;
- apply `TenantRfqAccess` authorization once at the group boundary;
- prevent new B2 tenant endpoints from accidentally omitting authorization;
- retain safe route/trusted-tenant mismatch checks;
- keep `/health` and `/openapi/v1.json` public intentionally;
- metadata/integration tests and full CI.

### B1.H3 GitHub branch/CI guardrails — DONE (external admin blocker #8)

When repository-admin tooling supports it:
- require CI before merge to `main`;
- block force-push/deletion of `main`;
- rename the stale workflow title from `Stage 1 deterministic core` to a project-level CI name;
- retain read-only default workflow permissions.

If branch-protection mutation is unsupported, document it as an external configuration blocker and continue.

### B1 blocked deployment follow-up — production identity provider

Do not guess the production identity provider. Production remains fail-closed until issuer/authority, audience, protocol and tenant-claim mapping are defined. Standard OpenAPI security-scheme metadata remains blocked by the same decision.

## Milestone B2 — Real RFQ backend

Goal: create a complete backend RFQ lifecycle independent of AI.

### B2.1 Customer/contact model — DONE
- migration for customers and contacts;
- tenant-scoped unique rules;
- repository + domain records;
- no guessed CRM fields.

### B2.2 RFQ create/read/list — DONE
- create draft RFQ;
- fetch RFQ;
- list/filter by status/date/customer;
- preserve nullable part/quantity for early draft.

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
- explicit allowed transitions;
- invalid transitions rejected;
- audit every transition;
- requirements for READY_FOR_ANALYSIS / READY_FOR_CALC encoded explicitly.

### B2.4 RFQ update/version semantics — PARTIAL
- B2.4a editable `New` draft fields with optimistic/concurrency protection — DONE;
- simultaneous update coverage — DONE;
- B2.4b changes after analysis create a new revision/snapshot rather than mutating history — BLOCKED_BUSINESS_POLICY on the undefined B2.3 lifecycle boundary.

### B2.5 File manifest endpoints — DONE
- list logical documents and immutable versions;
- source references;
- file metadata attached to RFQ workspace model.

### B2.H1 Local object-store atomic publication — DONE
Audit PR #14 exposed a real concurrent same-hash publication race in the local object store. Preserve temp-file hash verification and atomic publication; exactly one in-process writer reports creation. Production multi-process/object-storage semantics remain B4.4.

### B2.H2 Draft-only RFQ creation guard — DONE
B2.2 specifies creation of a draft RFQ. Creation is now constrained to `New`; explicit non-`New` create attempts are rejected by both API and repository, without defining any later transition. PR #16 implementation head `a18b20934ed5ce62e2da704c2c5d2a022bfde749` passed run `37426044035` with 641 unit + 200 integration = 841/841.

### B2 audit — DONE
Audit: `docs/B2_RFQ_BACKEND_AUDIT_2026-10-06.md`.

PR #14 merged as `2762f8998a7c8dd4e6c871b731d1fac40b7decdb`; post-merge main run `37424238234` is green. DB/API/replay/tenant-isolation evidence is recorded. B2.3 and B2.4b remain explicit business blockers. B2.H2 is the next unblocked hardening task.

## Milestone B3 — AI RFQ extraction wired end-to-end

Goal: turn the already-tested AI library into a controlled production backend feature.

### B3.1 AI configuration + DI — DONE
- register HttpClient, OpenAiResponsesProviderV1, retry wrapper and AiGatewayV1;
- configuration validation;
- no API key in repository/logs;
- AI can be disabled per environment.

Implemented in PR #17. Final PR head `329b8f290b026381f96d838e58366abbe92e96dd` passed run `37427756955`; merge `666d9d0d5d9b532a194df8ab074496f1a044e81e`; post-merge main run `37428227503` passed **641 unit + 206 integration = 847/847**. Disabled mode fails closed with provider code `AI_DISABLED` and performs no external request. Retry timing remains explicit configuration; no schedule was invented.

### B3.2 Deterministic AI input normalization — DONE
- canonicalize normalized JSON before fingerprinting;
- prove semantically identical supported inputs generate identical fingerprints;
- do not broaden CanonicalRFQ schema silently.

Implemented in PR #18. Final PR head `3aeafbd0e6b7de06d0adc280fb62ddfb4f61f53b` passed run `37429341570`; merge `470f87a2a3e11431f377940de43549535fd62c46`; post-merge main run `37429487560` passed **652 unit + 206 integration = 858/858 PASS**. Normalization recursively sorts object properties, preserves array order/types, rejects duplicate names, and canonicalizes exact decimal number lexemes without floating-point conversion. CanonicalRFQ v1 is unchanged.

### B3.3 AI execution policy — DONE
- allow_external_ai flag;
- permitted use cases/models/document types;
- redaction seam;
- bounded payload limits;
- failure => REVIEW/MANUAL, never invented fallback.

Implemented in PR #19. Final PR head `280e39be406b891855df583ac7c12f84d1a5cf8c` passed run `37432765264`; merge `b6ad030f5c4dc2f909bf809c14d870a4180b3627`; post-merge main run `37432937882` passed **664 unit + 210 integration = 874/874 PASS**. The boundary is default-deny: no production allowlist entries, payload limit or redaction rules are guessed. The default Host redactor blocks with `AI_REDACTION_NOT_CONFIGURED` until an approved deployment implementation replaces it. Architectural rationale: `docs/adr/002-external-ai-execution-policy.md`.

### B3.4 RFQ extraction service
- create normalized extraction input from explicitly selected RFQ sources;
- execute gateway;
- persist request fingerprint, prompt/schema/model versions and result status;
- no direct writes to final cost/time/price fields.

### B3.5 Persist CanonicalRFQ draft
- new immutable extraction attempt/history tables;
- current reviewed draft separate from raw provider output;
- source lineage retained;
- conflict/missing fields remain explicit.

### B3.6 Review/confirmation backend
- confirm/reject/correct extracted fields;
- every correction records actor/source/reason;
- critical unresolved CONFLICT/MISSING blocks progression.

### B3 audit
Security/privacy audit, hallucination/fallback audit, reproducibility audit, load/idempotency tests and CI.

## Milestone B4 — RFQ file security

Goal: make untrusted customer uploads production-safe.

### B4.1 Content-type verification
- do not trust Content-Type header alone;
- magic/signature detection for supported file types.

### B4.2 Archive policy
- bounded ZIP expansion;
- max file count, total bytes, recursion depth;
- block executable payloads and path traversal.

### B4.3 Malware scanning seam
- provider-neutral scanner interface;
- quarantine/reject state;
- local/test fake scanner;
- external implementation can be plugged in later.

### B4.4 Object storage abstraction hardening
- keep local store for development/tests;
- prepare S3-compatible implementation boundary;
- integrity verification on reads.

### B4 audit
Abuse-case review, upload fuzz/boundary tests, CI.

## Milestone B5 — Calculation API

Goal: expose the deterministic calculation engine through real RFQ/quote APIs instead of only the golden dev endpoint.

### B5.1 Calculation readiness service
- explain blockers: missing part, quantity, route, rate, material cost, conflict.

### B5.2 Calculate RFQ endpoint
- construct SnapshotRequest from approved/versioned configuration;
- persist/reuse immutable snapshot and calculation;
- return calculation trace.

### B5.3 Historical replay endpoint
- retrieve a historical calculation;
- replay and verify hash/result integrity.

### B5.4 Quantity scenarios
- calculate requested quantity variants from the same approved configuration;
- do not introduce sale pricing.

### B5 audit
Regression against W07044, precision, concurrency, historical replay, CI.

## Milestone B6 — Commercial domain skeleton

Goal: create quote/version/approval infrastructure without inventing pricing policy.

### B6.1 Quote + QuoteVersion model
- immutable calculated versions;
- references to exact RFQ/cost snapshot;
- statuses and audit.

### B6.2 Pricing policy contract
- interfaces/value objects/version model only;
- no guessed margins or formulas.

### B6.3 Approval model
- approval events and required-decision representation;
- actual thresholds remain configuration/business inputs.

### B6.4 BLOCKED gate: PricingEngineV1
Requires confirmed policy for margin/markup, rounding, minimum value, quantity breaks, discounts, risk reserve, currency/FX and external cost components.

### B6 audit
Verify no implicit pricing assumptions entered code.

## Milestone B7 — Geometry backend

Start only when representative STEP fixtures and expected outputs are available.

- geometry worker service boundary;
- deterministic geometry schema;
- STEP unit detection;
- topology normalization;
- basic dimensions/volume/surface/bounding box;
- geometry hash;
- golden STEP tests;
- no LLM arithmetic.

## Milestone B8 — Technology / rules backend

- controlled operation vocabulary;
- machine capability model;
- AI technology proposal DTO;
- deterministic feasibility/rule validator;
- draft routing only;
- human approval before approved routing.

## Milestone B9 — Similarity / risk backend

- deterministic feature vectors first;
- historical candidate search;
- explainable score components;
- risk reasons/policy;
- optional embeddings only as a secondary signal.

## Milestone B10 — Quote output backend

After PricingEngine is unblocked:
- deterministic final pricing;
- QuoteVersion finalization;
- approval enforcement;
- quote numbering;
- server-side PDF generation;
- approved email-draft payload;
- numeric validator preventing mismatch between approved quote and outgoing draft.

## Milestone B11 — Email / ERP / actuals

- mailbox ingestion via official OAuth APIs;
- idempotent message/thread model;
- attachment manifest;
- queue/retry/dead-letter;
- ERP handoff/outbox;
- production actuals import;
- quoted-vs-actual dataset.

## Frontend gate

Do not start frontend until at minimum B1-B5 are complete and B6 domain contracts are stable. The backend must expose a coherent, tested API before UI work begins.

## Research policy

External research is allowed and encouraged when it materially improves correctness. Prefer current primary sources:
- Microsoft/.NET/ASP.NET Core
- PostgreSQL
- OpenAI API documentation
- OAuth/OIDC provider documentation
- STEP/OpenCascade documentation
- OWASP
- official cloud/object-storage specifications

Foreign-language and international sources are allowed. Record externally-derived architectural decisions in an ADR when they materially affect the project.

## Autonomous branching rule

After each milestone audit:
- fix P0/P1 findings first;
- split each fix into small tasks;
- if no blocking finding exists, unlock the next milestone;
- if a task is business-blocked, document the exact missing decision and continue with the next independent task;
- never wait on a blocked PricingEngine if RFQ/API/security work can continue.
