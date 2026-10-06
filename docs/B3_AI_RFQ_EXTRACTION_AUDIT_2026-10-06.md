# B3 AI RFQ extraction audit — 2026-10-06

Scope: B3.1-B3.6 after PR #23 merged. This is a focused backend audit; it does not define RFQ workflow, pricing, customer, production, geometry or security policy.

## Verified baseline

- B3.6 merge: `1c217030f2c173128d65235281256b56d8ecbb5e`.
- Post-merge GitHub Actions run `37447453645` is green: **678 unit + 229 integration = 907/907 PASS**.
- CanonicalRFQ v1, deterministic TimeEngineV1/StockEngineV1/CostEngineV1, canonical calculation snapshots/hashes/replay, immutable RFQ file/extraction histories and tenant isolation remain unchanged.

## Security/privacy audit

PASS / fail-closed boundaries:
- External AI is default-deny and requires explicit per-execution `allow_external_ai`, configured use-case/model/document-type allowlists and a positive payload limit.
- Host redaction remains fail-closed with `AI_REDACTION_NOT_CONFIGURED`; no PII/redaction policy was invented.
- Source materialization remains fail-closed with `RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED`; no PDF/STEP/e-mail parsing behavior was invented.
- Exact RFQ file versions are selected tenant/RFQ-scoped; there is no implicit latest-version fallback.
- Review actor comes from authenticated `NameIdentifier`, not request JSON.

BLOCKED deployment decisions:
- **B3.B1 approved redaction policy/implementation** — requires explicit security/privacy rules and is not safe to invent.
- **B3.B2 approved source materialization formats/parsers** — requires supported document-type policy and parser choices; B4 upload/content verification must also remain authoritative for untrusted files.

## Hallucination/fallback audit

PASS:
- Provider/policy/redaction/materialization failures route to deterministic `REVIEW_MANUAL`; there is no fabricated fallback model output.
- Provider output must pass the existing CanonicalRFQ v1 guard before it can become the current draft.
- `MISSING` and `CONFLICT` remain explicit. Human `CONFIRM` does not erase them; only a valid `CORRECT` replacement can change the underlying fact.
- Review readiness does not mutate RFQ lifecycle status. The repository still lacks a business-approved definition of lifecycle-critical fields, so B2.3 remains blocked rather than guessed.

## Reproducibility audit

PASS:
- Input JSON normalization is deterministic for the supported project contract.
- Explicit source selections are sorted before request construction and include exact version, SHA-256 and source reference.
- Request fingerprint includes model/prompt/schema and normalized provider-visible input; provider-visible redacted input wins when execution reaches the gateway.
- Extraction attempts retain model/prompt/schema versions, fingerprint, source lineage and raw provider output.
- Human corrections are optimistic-concurrency protected and append-only audited with before/after fact JSON and row versions.

## Concrete hardening findings

### B3.H1 Atomic extraction persistence — READY
`RfqExtractionServiceV1` currently calls `IRfqExtractionExecutionRepository.SaveAsync` and then `SaveAttemptAsync` as two independent persistence operations. A failure between them can leave an append-only `RFQ_AI_EXTRACTION_EXECUTION` audit event without the corresponding extraction attempt/current-draft write. Consolidate the durable execution metadata + immutable attempt + optional current-draft update into one PostgreSQL transaction, preserving existing history and tenant/RFQ scoping. Do not alter provider or business policy.

Acceptance: injected/forced failure proves all-or-nothing persistence; successful execution still writes one immutable attempt and, only for validated COMPLETED output, the current draft; full CI green.

### B3.H2 Extraction retry/idempotency contract — READY after B3.H1
Repeated identical extraction calls currently create new immutable attempts and a completed repeat can advance current draft `row_version` even when request fingerprint and canonical output are unchanged. Add an explicit technical idempotency boundary for concurrent/retried identical execution requests without deleting history or silently conflating genuinely separate executions. The key/semantics must be versioned and deterministic; no business workflow transition may be inferred.

Acceptance: concurrent/retried same idempotency key cannot double-apply the current draft; different keys remain distinct attempts; tenant isolation and immutable history preserved; full CI green.

### B3.H3 Tenant extraction HTTP endpoint — READY after B3.H1/H2
The extraction service is registered but `Program.cs` exposes no tenant HTTP operation that invokes it. Add a tenant-authorized endpoint accepting explicit model, `allow_external_ai`, and exact source selections only. It must call `RfqExtractionServiceV1`, expose deterministic REVIEW_MANUAL/COMPLETED results, and never accept actor identity, final price/cost/time fields, implicit latest files or workflow status changes.

Acceptance: route authorization/tenant mismatch tests, exact-version request tests, default fail-closed behavior and OpenAPI metadata; full CI green.

### B3.H4 AI/review OpenAPI coverage — READY after B3.H3
The custom OpenAPI document currently documents health and RFQ upload/download but not the B3 review/extraction operations. Add metadata for canonical draft/review/readiness/history and extraction once the extraction endpoint exists. This is API-contract hardening only.

## Load/concurrency audit

Existing B3 tests cover stale human review writes and PostgreSQL tenant/history constraints, but there is no focused concurrent duplicate extraction persistence test. B3.H2 owns this gap. No performance target or provider throughput limit exists in repository policy, so this audit does not invent latency/QPS thresholds.

## Milestone conclusion

B3.1-B3.6 are functionally implemented and post-merge green, but the B3 milestone is **AUDITED_WITH_HARDENING** rather than closed: H1-H4 are concrete unblocked technical follow-ups; B3.B1-B3.B2 are explicit deployment/security-policy blockers. Frontend remains gated and must not start.
