# Project audit — 2026-10-05

## Verified repository state

Repository: `kemienkruk2002-tech/ai-manufacturing-quote-engine`

The Google Drive project was migrated into GitHub as source code, tests, migrations, seed data, technical documentation, scripts and CI configuration. Build outputs and the original local `.git` directory were intentionally not imported.

Current verified state:
- .NET 8 / ASP.NET Core / PostgreSQL / Npgsql / xUnit.
- 105 files in the repository.
- GitHub Actions runs against PostgreSQL 16.
- Latest full CI result: **638 unit + 152 integration = 790/790 passed**, 0 failed, 0 skipped.
- B069 provider-neutral retry policy is implemented and covered by tests.
- Deterministic Time, Stock and Cost engines are operational.
- RFQ draft records and immutable RFQ file versioning/upload/download are operational.
- CanonicalRFQ v1, strict JSON schema, AI request fingerprinting, output guard, prompt-v1, OpenAI Responses provider and retry wrapper are implemented as library code.

## Overall assessment

The current repository is a strong deterministic **quoting backend core**, but it is not yet a complete application for creating and sending manufacturing quotations.

The strongest part is the calculation and audit foundation. The missing work is mainly above that foundation: real RFQ workflow, customer/quote lifecycle, production wiring of AI, sale-price policy, user interface, approval, generated quote documents and the later geometry/history modules.

The existing deterministic core should be treated as frozen unless a concrete failing test or a new versioned business rule requires a change.

## What is already strong

### Deterministic calculation

`TimeEngineV1`, `StockEngineV1` and `CostEngineV1` use deterministic decimal arithmetic and do not depend on AI, network access, database state, runtime clock or randomness.

Canonical inputs are serialized deterministically and hashed. Historical calculations can be replayed from immutable stored bytes. Changes in input or versioned rules produce different hashes.

### Database integrity

The PostgreSQL model already has unusually strong invariants for this stage:
- tenant-scoped composite foreign keys;
- immutable approved routings;
- immutable snapshots and RFQ file histories;
- append-only audit events;
- finite numeric checks;
- non-overlapping effective machine-rate intervals;
- migration checksums;
- advisory locks;
- concurrency/race-condition tests;
- content-addressed snapshots and file objects.

### RFQ file handling

The backend supports versioned RFQ document metadata and content-addressed local file storage using SHA-256. Re-uploading identical content is idempotent and a changed file creates a new immutable version.

### AI boundary

The AI layer follows the core product principle that AI is not the source of truth:
- strict `RFQ_EXTRACTOR/v1` schema;
- EXPLICIT / INFERRED / MISSING / CONFLICT classifications;
- source references and confidence;
- prompt explicitly forbids inventing prices, costs and manufacturing times;
- strict output validation before data is accepted;
- transient/permanent/cancelled provider errors are distinguished;
- B069 retries only TRANSIENT failures with bounded, explicit delays.

## Critical missing product functionality

### P0 — before this can be a usable quotation application

1. **Authentication, authorization and tenant identity**
   There is no production auth layer. Tenant identity currently comes from URL parameters. PostgreSQL has tenant-scoped FKs but no RLS. This is safe for tests/dev, not a multi-tenant production deployment.

2. **RFQ/customer lifecycle**
   There is no complete production RFQ CRUD/state-machine API. Customer/contact management is not implemented. Existing production HTTP surface is mostly RFQ file upload/download plus health; the full RFQ workspace workflow does not exist yet.

3. **AI is not wired into the Host**
   `AiGatewayV1`, `OpenAiResponsesProviderV1` and the retry wrapper are tested library code, but `Program.cs` does not register them in DI and exposes no RFQ extraction endpoint. There is also no production provider/policy configuration, redaction policy, model budget or AI audit persistence.

4. **Pricing / final sale price**
   CostEngine calculates manufacturing cost, not the sale price. `PricingEngineV1` / margin policy is intentionally blocked because the exact commercial policy has not been supplied. Until this is defined, the system cannot deterministically produce a final quotation price.

5. **QuoteVersion / approval / PDF / sending**
   The target product requires immutable quote versions, approvals, quote numbering, PDF generation and an email draft/send workflow. These modules are not implemented.

6. **Frontend**
   There is no Next.js/React interface. The target screens from the specification — RFQ Inbox, RFQ Workspace, review, quote preview and approval queue — do not exist.

## P1/P2 modules not yet implemented

- STEP/geometry worker and deterministic geometry summary.
- Manufacturing feature recognition.
- Drawing-requirements extraction as its own workflow.
- Technology proposal + deterministic feasibility/rules validator.
- Similar-parts search.
- Risk engine.
- Quantity pricing / margin policy.
- E-mail ingestion via Gmail/Microsoft 365.
- ERP/CRM handoff.
- Production actuals and quoted-vs-actual.
- Feedback dataset / offline model improvement loop.
- Production-grade object storage, malware/format scanning and ZIP sandbox.

## Technical findings to resolve

### 1. README and workflow naming are stale

README still says the AI Extractor is not implemented and that remote GitHub Actions have not been run. That is no longer true. Migration 007 is also missing from the README migration list. The workflow name still says “Stage 1 deterministic core” although the repository is beyond Stage 1.

### 2. Repository is public

The GitHub repository is currently public. Before adding real customer RFQs, drawings, STEP files, prices or production history it should be made private unless public visibility is explicitly intended.

### 3. Main branch is unprotected

The current `main` branch has no branch protection. Once development continues through PRs, require the CI job before merge and prevent accidental force-push/deletion.

### 4. AI input fingerprint assumes normalized JSON

`AiRequestFingerprintV1` hashes `NormalizedInputJson` exactly as supplied. Semantically identical JSON with different whitespace/property ordering can produce different fingerprints unless a caller canonicalizes it first. Add a deterministic normalization boundary before production caching/idempotency relies on this fingerprint.

### 5. Production file validation is incomplete

The RFQ upload API checks declared MIME type and maximum length, but does not yet perform content magic validation, malware scanning or archive sandboxing. The target architecture explicitly expects these controls.

### 6. Cost-rate semantics need business confirmation

The cost snapshot stores `rate_tpz_pln_h`, `rate_production_pln_h` and `rate_overall_pln_h`. Current CostEngine uses overall rate for Tj and Tpz rate for setup; `rate_production_pln_h` is stored but not used in the formula. Do not change this until the meaning of the three source rates is confirmed.

### 7. Final mass greater than norm mass remains undefined

StockEngine intentionally allows utilization > 1 and negative scrap when confirmed final mass exceeds norm mass. The project documentation explicitly leaves this as a business decision. Define whether this should BLOCK, WARN or use another rule.

### 8. CI hardening remains

Current CI proves build/test/reproducibility well, but does not yet run coverage thresholds, formatting/static analysis, dependency vulnerability scanning or secret scanning.

## Recommended implementation order

1. **Platform safety** — auth/OIDC, authenticated tenant context, authorization, RLS strategy, production error model, OpenAPI.
2. **Real RFQ domain/API** — customer/contact, RFQ CRUD, state machine, versioning, source/file links and audit.
3. **Wire AI extraction end-to-end** — provider configuration, policy/redaction, retry, extraction endpoint, persistence of CanonicalRFQ draft, review/confirmation flow, manual fallback.
4. **Commercial rules** — obtain the exact pricing/margin/rounding/quantity-break policy and implement versioned `PricingEngineV1`.
5. **Quote lifecycle** — QuoteVersion, approval, quote numbering, calculation trace, generated PDF and approved e-mail draft.
6. **First usable UI** — Next.js RFQ Inbox + Workspace + Review + Quote Preview + Approval.
7. **Geometry/STEP** — separate worker, units/topology normalization, deterministic output and golden STEP fixtures.
8. **Technology / rules / similarity / risk**.
9. **E-mail ingestion, ERP handoff, actuals and learning loop**.

## Business inputs required before PricingEngineV1

Do not guess these values. The project needs explicit decisions for:
- gross-margin vs markup policy;
- permitted margin bands and approval thresholds;
- rounding stage and rounding rule;
- minimum order / minimum quote value;
- quantity-break behavior;
- customer-specific pricing/discount rules;
- risk reserve/surcharge;
- currency and FX policy if currencies other than PLN are supported;
- whether external processes/tooling/inspection are separate cost components and how they enter price.

## Next milestone recommendation

The next coding milestone should **not** be Geometry yet. The fastest route to a usable product is:

**RFQ CRUD + authenticated tenant context + AI RFQ extraction wired to the API + CanonicalRFQ review/persistence.**

That creates a real end-to-end user flow from RFQ upload to confirmed structured data while preserving the already-tested deterministic calculation core. After that, implement the commercial Pricing/Quote/Approval path as soon as the pricing policy is supplied.
