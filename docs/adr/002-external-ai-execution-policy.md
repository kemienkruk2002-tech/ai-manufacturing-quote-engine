# ADR 002 — Default-deny external AI execution boundary

Status: Accepted  
Date: 2026-10-06

## Context

The RFQ extractor processes customer-controlled documents and normalized RFQ data. External model calls therefore cross a trust/data boundary.

B3.3 must add:
- an explicit `allow_external_ai` decision;
- permitted use-case/model/document-type checks;
- bounded payload size;
- a redaction seam;
- deterministic human-review/manual fallback instead of invented output.

No production allowlist values, payload limit, PII classification, secret-detection rule or business approval policy has been supplied, so the repository must not invent them.

Primary references considered:
- OWASP LLM Prompt Injection Prevention Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html
- OWASP Input Validation Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Input_Validation_Cheat_Sheet.html
- OpenAI API data controls: https://platform.openai.com/docs/models/default-usage-policies-by-endpoint

OWASP recommends allowlist validation, bounded input, separation of untrusted data from instructions, least privilege and treating model output as untrusted. OpenAI documents that API customer content may be present in abuse-monitoring logs under default data controls, which makes an explicit pre-provider redaction boundary material.

## Decision

Introduce `AiPolicyExecutorV1` as the application-level execution boundary around `AiGatewayV1`.

External execution requires all of the following:
1. per-execution `AllowExternalAi=true`;
2. request use case is in a configured ordinal allowlist;
3. model ID is in a configured ordinal allowlist;
4. every supplied document type is in a configured ordinal allowlist;
5. at least one document type is supplied;
6. a positive `MaxPayloadBytes` is configured;
7. canonical input size is within that limit before redaction;
8. `IAiInputRedactor` explicitly allows and returns valid JSON;
9. redacted canonical input remains within the same byte limit.

The Host binds policy values from `Ai:Policy`. Empty allowlists and a zero limit are valid configuration but deny execution. This allows environments to boot with external AI disabled without inventing policy.

The default Host redactor always blocks with `AI_REDACTION_NOT_CONFIGURED`. A deployment must explicitly replace `IAiInputRedactor` with an approved implementation before provider calls can occur through the policy executor.

All policy, redaction, gateway-output and provider failures return `REVIEW_MANUAL`. No fallback model, fabricated extraction result or automatic business action is introduced.

Payload limits are measured as UTF-8 bytes of canonical JSON both before and after redaction.

## Consequences

Positive:
- default configuration cannot exfiltrate RFQ content to an external model;
- allowlists and limits are explicit deployment inputs rather than repository assumptions;
- redaction policy can be implemented independently of provider/model selection;
- provider-visible input is still processed by the existing deterministic normalization/fingerprint path;
- future B3.4 extraction services can depend on one controlled execution boundary.

Trade-offs:
- external AI remains intentionally unusable through the policy executor until a deployment supplies allowlists, a positive limit and a concrete redactor;
- this ADR does not define what PII/secrets must be removed;
- this ADR does not define which document types/models are commercially or legally approved;
- direct low-level gateway tests remain possible, but production extraction services must use the policy executor.

## Not decided here

- actual permitted model IDs;
- actual permitted document types;
- actual payload byte limit;
- PII/secret redaction rules;
- customer consent or legal-basis policy;
- retention/ZDR organization settings;
- B3.4 source-selection policy;
- any RFQ workflow, pricing, approval or production rule.
