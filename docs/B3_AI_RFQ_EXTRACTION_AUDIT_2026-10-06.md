# B3 AI RFQ extraction audit — 2026-10-06

Status: DONE_PR_GREEN

Scope: B3.1-B3.6 only. This audit does not change pricing, RFQ workflow policy, deterministic calculation engines, canonical calculation snapshots/hashes/replay, geometry, or frontend behavior.

Verified baseline before audit:
- B3.6 PR #23 final head `d06c753877fb592d240776390839adb4828923f6` passed run `37447285508`: **678 unit + 229 integration = 907/907 PASS**.
- PR #23 merged as `1c217030f2c173128d65235281256b56d8ecbb5e`.
- Post-merge main run `37447453645` passed **678 unit + 229 integration = 907/907 PASS**.

## Audit dimensions

### 1. Security and privacy

Verified strengths:
- external AI execution is default-deny;
- per-call external-AI permission, use-case/model/document-type allowlists and a positive payload limit are required;
- payload size is checked before and after redaction;
- the Host default `IAiInputRedactor` fails closed;
- the Host default RFQ source materializer fails closed;
- OpenAI request payload explicitly sets `store=false`;
- provider output is treated as untrusted and must pass the strict CanonicalRFQ v1 output guard before becoming the current draft;
- no provider/model fallback invents structured RFQ data;
- tenant-scoped repositories and the tenant route group remain the authorization boundary;
- review actor comes from the authenticated `NameIdentifier` claim, not request JSON;
- review mutation and audit insertion are one PostgreSQL transaction.

Current primary-source re-check:
- OWASP LLM Prompt Injection Prevention recommends treating external documents/model output as untrusted, keeping authorization outside the model, applying least privilege, validating output and using human approval for consequential actions.
- OWASP Input Validation recommends allowlist validation and bounded input.
- OpenAI current API data-controls documentation states API data is not used for training unless explicitly opted in, but abuse-monitoring logs may contain customer content and are retained for up to 30 days by default. Eligible organizations can use Modified Abuse Monitoring / Zero Data Retention. Responses requests use `store=false` here, but that does not itself establish Zero Data Retention.

References:
- https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html
- https://cheatsheetseries.owasp.org/cheatsheets/Input_Validation_Cheat_Sheet.html
- https://developers.openai.com/api/docs/guides/your-data

### 2. Hallucination and fallback

Verified:
- prompt/schema forbid invented manufacturing prices, costs and times;
- output must satisfy CanonicalRFQ v1;
- MISSING and CONFLICT remain explicit states;
- policy, redaction, provider and output-validation failures become `REVIEW_MANUAL`;
- invalid provider output is retained in immutable attempt history but never promoted to current CanonicalRFQ draft;
- human `CORRECT` replaces one complete fact and then re-validates the whole CanonicalRFQ v1 document;
- `CONFIRM` does not silently turn MISSING/CONFLICT into resolved data.

No hallucination/fallback P0 was found.

### 3. Reproducibility and lineage

Verified:
- normalized input JSON is canonicalized before fingerprinting;
- selected source ordering is deterministic;
- exact RFQ file version, document type, SHA-256 and source reference are retained in lineage;
- request fingerprint reflects provider-visible redacted input when a provider call occurs;
- prompt/schema/model versions are persisted;
- raw provider output is retained separately from current reviewed draft;
- extraction attempts are immutable;
- review events record before/after fact JSON, source attempt, actor/source/reason, before/after row version and correlation ID.

### 4. Concurrency, load and idempotency

Existing evidence:
- the OpenAI provider unit suite executes the same request 100 times and verifies identical request bodies/results;
- current-draft review uses compare-and-swap row version;
- stale review writes produce no review event.

Audit addition:
- PostgreSQL integration stress now launches 12 reviews concurrently against the same draft row version and requires exactly one `UPDATED`, eleven `VERSION_CONFLICT`, one audit event and one row-version increment.

Important unresolved behavior:
- extraction execution itself has no single-flight/idempotency contract. Repeating the same request fingerprint can make multiple provider calls and append multiple immutable attempts. Whether identical fingerprints should be deduplicated, replayed, or explicitly rerun is a product/cost policy and must not be guessed.

## Findings

### B3.H1 — P1 stale extraction can overwrite a human-reviewed current draft — READY

`RfqExtractionExecutionRepository.SaveAttemptAsync` updates `rfq_canonical_drafts` for every completed valid extraction using an unconditional `ON CONFLICT ... DO UPDATE`. It does not compare the draft row version observed when extraction started.

Race:
1. extraction A starts;
2. human reviews/corrects current draft and increments `row_version`;
3. extraction A finishes later;
4. A replaces the reviewed CanonicalRFQ draft and increments row version again.

The immutable extraction attempt is correct to persist, but a stale extraction must not silently replace a draft changed after that extraction began.

Next hardening task: capture the current draft version at extraction start and conditionally publish the completed draft only when that expected version/state is still current. Always retain the immutable extraction attempt even when current-draft publication loses the CAS.

### B3.H2 — P1 no tenant HTTP trigger for RFQ extraction — READY after H1

`RfqExtractionServiceV1` is registered in DI but no tenant-scoped production endpoint invokes it. Review endpoints exist, but an API client cannot start an extraction through the Host.

Add a tenant-authorized extraction endpoint that requires explicit:
- model ID;
- per-call `allow_external_ai`;
- exact source selections (`logical_key + version_no + document_type`).

Do not add implicit latest-file selection.

### B3.H3 — production source materializer remains fail-closed — BLOCKED_BY_B4_SECURITY

The Host `IRfqExtractionSourceMaterializer` always returns `RFQ_EXTRACTION_SOURCE_MATERIALIZER_NOT_CONFIGURED`.

Do not parse customer PDF/STEP/archive bytes in production before the B4 upload-security controls establish supported content identification, archive limits, malware/quarantine behavior and hardened object-store reads. Keep this blocked until the required B4 boundaries are available.

### B3.H4 — external AI deployment policy/redaction/retention — BLOCKED_DEPLOYMENT_POLICY

Production external AI remains intentionally unusable until deployment supplies:
- permitted use cases/models/document types;
- positive payload limit;
- approved redaction/PII/secret policy;
- customer consent/legal-basis decision where required;
- approved OpenAI retention/data-control posture (default abuse monitoring vs Modified Abuse Monitoring / ZDR);
- production identity-provider decision remains separately blocked from B1.

No values are invented in repository code.

### B3.H5 — extraction fingerprint idempotency semantics — BLOCKED_PRODUCT_POLICY

The request fingerprint is reproducible but not an idempotency key. Identical requests can intentionally or accidentally create multiple model calls/attempts.

Required product decision:
- deduplicate identical in-flight/completed fingerprints;
- always allow fresh model execution;
- or require an explicit rerun/force flag.

Do not impose one behavior without the decision.

### B3.H6 — raw provider/customer-content retention — BLOCKED_RETENTION_POLICY

`rfq_extraction_attempts.raw_provider_output` is immutable and has no expiry/purge policy. This supports traceability but may conflict with future customer/legal retention requirements.

Required decision: retention duration, deletion/legal-hold behavior and whether raw provider text must be retained after reviewed CanonicalRFQ is finalized. Preserve current immutable history until that policy exists.

### B3.H7 — B3 production routes are not represented in the custom OpenAPI document — P2 READY

The custom OpenAPI builder emits only endpoints carrying explicit `ApiOperationMetadata`. B3 review/current-draft routes do not currently carry that metadata, so they are absent from `/openapi/v1.json`.

This is documentation/contract drift, not a runtime authorization bypass. Add metadata/tests after P1 hardening.

### B3.H8 — review free-text bounds are not explicitly configured — P2 SECURITY POLICY BLOCKER

`review_source` and `reason` require non-empty values but have no application-specific maximum length. ASP.NET/server limits still apply, but the B3 contract does not define bounded field lengths.

Do not invent product/security limits. Define approved limits before tightening the contract.

## CI evidence

Audit implementation head `355fc71d74994651946c186be9af7cc43f41e1f5` passed GitHub Actions run `37448282017`: **678/678 unit + 230/230 integration = 908/908 PASS**. The added concurrency stress test passed on real PostgreSQL.

## Audit conclusion

B3's deterministic AI boundary, strict output guard, fail-closed policy, immutable extraction lineage and optimistic human review are strong and CI-backed.

B3 is **not yet production end-to-end**:
- there is no HTTP extraction trigger;
- source materialization is intentionally disabled;
- deployment redaction/retention policy is intentionally unspecified.

The highest-priority code issue is B3.H1 because it can overwrite a human-reviewed draft during a concurrent extraction. Fix B3.H1 before adding the extraction endpoint or starting B4 runtime work.

No frontend work is unlocked by this audit. The existing frontend gate remains unchanged.
