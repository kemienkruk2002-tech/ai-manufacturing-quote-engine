# ASTRA project status — 2026-10-10

Status: **H2 IN_PROGRESS; repository replay accepted, service wiring next**. ASTRA independently accepted PR #38 and integrated it only into the H2 feature branch. Current feature `ca33240` passed 953 tests locally and after integration. Main remains `ce00d06`; documentation PR #32 remains separate and has no main-merge approval. Prior specialist documentation review covers authored content through `ea71fb9`; later operational checkpoints are ASTRA updates.

## Operating model — Owner clarification

ASTRA creates small executable GitHub issues. **The Owner selects and launches the implementation models**, including lower-cost models. ASTRA does not automatically spawn or resume implementers. ASTRA reads their GitHub branches/PRs, reviews the exact diff, verifies tests and CI, identifies mistakes, requests corrections and integrates accepted work. Workers do not approve or merge their own changes. Main merge still requires explicit Owner approval.

ASTRA has not launched implementation workers. Owner-launched #37 delivered PR #38 and is complete. Two independent tasks are ready: [#40 — service idempotency wiring](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/40) on feature `ca33240`, and [#41 — mock-tested FreeLLMAPI adapter](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/41) on main `ce00d06`. Each contains allowed files, tests, escalation rules and a copyable launch instruction.

Primary tracker: [#28](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/28), documentation [PR #32](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/32). Active work uses #29/#40 (H2), #30/#41 (FreeLLMAPI) and existing #8 (branch protection). Issues are the task register. The Owner authorized main merge of PR #36 specifically; other main merges and production deployment remain unapproved.

## Verified baseline

| Target | Commit | Evidence |
| --- | --- | --- |
| current main after Owner-approved PR #36 | `ce00d06b15a81cbd8bec65ff25c89a089a6e14ad` | [CI 37970757483](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37970757483): 678 unit + 258 integration = 936 PASS; zero failed/skipped |
| current PR #27 feature after PR #38 | `ca33240a05aacd155becbb33d8d81fb6d815db0a` | [CI 38080710662](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/38080710662): 683 unit + 270 integration = 953 PASS; zero failed/skipped; ASTRA reproduced the same total locally |
| main at initial audit | `cc0aaca8ec357506c63d82eeccd6271a90f6d1e2` | [CI 37460565034](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37460565034): 678 unit + 232 integration = 910 PASS; zero failed/skipped |
| PR #27, historical audited head | `c50596ef8826e1f53b7682baf0558586e9b5008c` | [CI 37519559807](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37519559807): 683 unit + 224 integration = 907 PASS; zero failed/skipped; eight draft regression cases were missing |
| PR #33, reviewed test repair | `ea1e5957272df3f664861e4e4b6342b14e98c2bc` | [CI 37966082743](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966082743): 683 unit + 232 integration = 915 PASS; zero failed/skipped; exact commit accepted by independent Security & QA |
| PR #27, after feature-branch merge of PR #33 | `c4ef0643379879e1fabcf7a458725ee5b9e817f8` | [PR CI 37966270514](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966270514): 683 unit + 232 integration = 915 PASS; zero failed/skipped. [Push CI 37966262994](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37966262994) also succeeded |

Local main runtime validation on 2026-10-09 also passed 678 unit + 232 integration with PostgreSQL 16.15, locked restore and `scripts/test.ps1 -DatabaseMode Portable`. [PR #33](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/33) changed only `RfqDraftInputTests.cs` and was merged only into the existing [PR #27](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/pull/27) feature branch; main remains unchanged. PR #32 contains documentation only. Final exact-commit CI for PR #32 must be verified in its checks after publication; no final green result is claimed yet. No live AI calls occurred.

At initial inventory there was one open PR (#27), one ordinary issue (#8), ten default labels and no milestones. GitHub Projects administration is not exposed by the connected tools and no board has been created. Full issue inventory was checked before creating #28–#31. Existing #8 was reused. PR #27 had no submitted reviews or review threads at the time of inspection; its stale description has been updated.

## Findings and task order

1. **P0 / #29, existing PR #27 — IN_PROGRESS:** regression restoration, schema repair and repository idempotency are integrated. #37 / PR #38 adds durable keyed save/lookup, conflict handling, current-draft replay, rollback, cancellation and PostgreSQL advisory-lock concurrency. ASTRA accepted exact head `504a07c`, reproduced **683 unit + 270 integration = 953 PASS** locally, then integrated feature merge `ca33240`; post-merge PR/push CI succeeded. Next: Owner-launch #40 service wiring. H1 remains intact and H2 remains incomplete.
2. **P0 operational / #8:** main branch API reports `protected: false`; rulesets list is empty. Detailed protection read returns integration HTTP 403. Required action remains protect main, require `test`, block force-push and deletion. No settings were changed.
3. **P1 / #31 — DONE:** PR #36 returns safe typed 400 errors for invalid RFQ fields, preserves auth ordering and sanitized unexpected 500 errors. ASTRA accepted `899a19b`; independent local PostgreSQL 16.15 and [PR CI 37968534332](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37968534332) both passed **678 unit + 258 integration = 936 PASS**, zero failed/skipped. Owner explicitly approved main merge `ce00d06b15a81cbd8bec65ff25c89a089a6e14ad`; [post-merge CI 37970757483](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/actions/runs/37970757483) passed 936 tests, zero failed/skipped; target main was verified.
4. **P1 / #30 — ADAPTER READY / LIVE BLOCKED:** ASTRA verified the Owner's local healthy FreeLLMAPI container, pinned official upstream image/commit, loopback base and Responses JSON Schema support without reading secrets. #41 is ready for provider selection and mock contract/security tests. LIVE remains blocked because the local router has no enabled serving model and no approved synthetic scope/limit. Preserve OpenAI, provider abstraction, output guard and default-deny policy.
5. After H2: extraction endpoint B3.H3, then OpenAPI B3.H4 including omitted existing B2 routes, then B4/B5 according to the existing plan. Do not start frontend: B1–B5 must be complete and B6 contracts stable.

The issue briefs contain acceptance tests, dependencies, allowed scope, forbidden changes and reviewers. #29 remains the H2 parent: restoration, schema and repository replay are integrated; #40 is the final service subtask before H2 documentation/review. #41 is independent adapter work on main.

## Module and specialist evidence

- [Backend module/dependency/API map](../audits/BACKEND_MODULE_AUDIT_2026-10-09.md): deterministic engines and replay, B1/B2/B3 actual scope, missing product modules, documentation drift, validation findings. Static review, with runtime baseline independently executed by ASTRA.
- [Independent PR #27 security/QA audit](../audits/PR_27_AUDIT_2026-10-09.md): exact removed/restored test inventory, unused identity, SQL NULL semantics, tenant-audit pointer invariant, small repair briefs.
- [FreeLLMAPI research and adapter briefs](../audits/FREELLMAPI_AUDIT_2026-10-09.md): primary sources, verified versus unknown capabilities, configuration seams, mock and optional LIVE layers.

These reports preserve the specialists' findings at the audited commits. Statements about their initial local read-only scope describe the audit phase; ASTRA subsequently publishes the reports through this documentation branch. They are not claims that all findings have been fixed.

## Agent registry at checkpoint

| Agent / role | Scope | Execution / delivered result | Review | Blocker / next action |
| --- | --- | --- | --- | --- |
| ASTRA | #28/#29 integration and GitHub coordination | Verified baseline, issue briefs, exact-head checks and feature-only merge of PR #33 | Receives independent specialist findings; does not substitute author's checks for review | Publish reviewed documentation, verify final PR #32 checks, present owner decisions; main approval pending |
| Backend / database specialist | #28 audit; #29 first repair | Backend audit delivered; `ea1e595` restores the eight cases; PR #33 merged to feature as `c4ef064` | Exact repair accepted by Security & QA; local and remote suites passed | First repair finished; H2 execution/replay still open |
| security_qa_audit | #28/#29 security and regression coverage | Historical PR #27 audit delivered; exact restoration commit and TRX independently checked | Repair ACCEPT; documentation review interrupted by usage limit, **no documentation acceptance** | Documentation review reassigned to ai_provider_audit |
| documentation_engineer | #28; `agent/docs/issue-28-reconcile-status` | `91d953e` reconciles README/state/plan; `38a1495` records feature merge and CI; `ea71fb9` updates management state | Authored four-file reconciliation accepted by independent reviewer through `ea71fb9` | Finished; ASTRA integrates results and records the Owner's process clarification |
| ai_provider_audit | #28 independent documentation review; #30 provider research | Provider audit and exact-commit documentation review delivered | ACCEPT through `ea71fb9`, no findings; final agent message then hit the usage limit | Stopped; no ongoing work. Provider adapter still blocked on identity |
| backend_module_audit / Database Engineer | Earlier interrupted #29 schema attempt | Uncommitted scratch tests only; no authored fix | Historical reproduction only | Superseded by the Owner-launched #34 result below |
| Owner-launched Database Engineer | #34 / PR #35 | `4319f3f`, schema + focused tests, merged to feature `f55c42d` | ASTRA ACCEPT; local, PR CI and post-merge CI 939 PASS | Schema task complete; H2 remains open |
| Owner-launched Backend Engineer | #31 / PR #36 | `899a19b`, narrow RFQ validation + regressions; main merge `ce00d06` after Owner approval | ASTRA ACCEPT; local, PR CI and main post-merge CI 936 PASS | #31 DONE |
| Owner-launched Backend/Database Engineer | #37 / PR #38 | `504a07c`, keyed repository replay/concurrency; feature merge `ca33240` | ASTRA ACCEPT; local, exact-head and post-merge CI 953 PASS | #37 DONE; H2 service task #40 next |
| Next Backend Engineer | #40, not launched by ASTRA | Service preflight replay, keyed terminal writes and concurrent losing-response contract ready | ASTRA will review the resulting PR | Owner launches selected model from exact feature base `ca33240` |
| Next AI Engineer | #41, not launched by ASTRA | Configurable FreeLLMAPI adapter and mock/security tests ready on main | ASTRA will review; main merge requires Owner approval | LIVE stays blocked on enabled model, secret and approved scope |
| Other roles | Later manufacturing/frontend/DevOps work | No additional implementation or frontend start claimed | No new review result | Business/deployment decisions and frontend gate remain applicable |

Implementation specialists use separate branches/worktrees; reviewers inspect exact commits independently. These rows retain the history of the earlier automatically launched agents. None is running now. Future implementation starts only when the Owner launches the chosen model from a GitHub task; ASTRA coordinates and reviews the results.

## Decisions and remaining initialization work

| Owner decision / resource | What is needed | Existing reference |
| --- | --- | --- |
| Main merge approval | Explicit approval for the concrete reviewed change after required checks; no blanket main authorization is inferred | #28 / PR #32 and #29 / PR #27 linked above |
| Main branch protection | Repository-admin capability to require `test` and block force-push/deletion; current read limitation remains documented | [#8](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/8), [plan B1.H3](../AUTONOMOUS_BACKEND_PLAN.md) |
| Production identity | Provider/protocol, issuer/authority, audience and tenant-claim mapping | [Backend audit](../audits/BACKEND_MODULE_AUDIT_2026-10-09.md), plan B1 deployment follow-up |
| RFQ lifecycle | Transition/readiness rules, critical fields, analysis-lock boundary and post-analysis revisions | [Plan B2.3/B2.4b](../AUTONOMOUS_BACKEND_PLAN.md), backend audit |
| External AI / FreeLLMAPI | Adapter #41 can proceed. LIVE requires an enabled serving model, secure unified key, approved synthetic scope/limit, allowlists/redaction and supported source parsers. Do not send secret values in issues/chat | [#30](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/30), [#41](https://github.com/kemienkruk2002-tech/ai-manufacturing-quote-engine/issues/41), [provider audit](../audits/FREELLMAPI_AUDIT_2026-10-09.md) |
| Commercial policy | Pricing, margin/markup, rounding and approval rules | [Plan B6/B10](../AUTONOMOUS_BACKEND_PLAN.md) |
| Geometry fixtures | Representative STEP files and expected outputs | [Plan B7](../AUTONOMOUS_BACKEND_PLAN.md) |

Publish the integrated documentation through PR #32 and verify its final-commit checks before Owner approval. Independent review accepted the authored reconciliation through `ea71fb9`; ASTRA subsequently recorded newer operational checkpoints. Historical milestone evidence is preserved rather than treated as active instructions. On continuation, refresh main/PR heads and CI and read #28–#30, #40, #41 and #8. Review new worker PRs if present; otherwise leave the ready tasks for the Owner to launch.

Next Owner-launch tasks are #40 and #41. Do not start workers automatically. Do not invent values or change frozen engines, canonical hashes/replay or applied migration history. Additional backend findings remain in the module audit for later scoping. Frontend remains gated; no production deployment or live provider run is included.
