# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI after B2.5: **641 unit + 183 integration = 824/824 passed**.
- B2.5 merge: `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`; post-merge run `37414197830` succeeded.
- Frozen deterministic engines/snapshots/hashes/replay and existing migration history remain unchanged.

## Current milestone
**B2 — Real RFQ backend / focused audit**

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
Repository defines status vocabulary and the progressed-input invariant, but no allowed transition graph or READY_FOR_ANALYSIS/readiness policy. Blocker is recorded on `auto/b2-3-rfq-state-machine` at `24b48575d8a72296540e4698f3442c65aad4edd0`. Do not invent workflow policy.

### B2.4a Draft optimistic concurrency — DONE
PR #12; merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; post-merge run `37398187730` succeeded; verified **822/822 PASS**.

### B2.5 File manifest endpoints — DONE
PR #13; head `8982c2b7e6736ce24401d2215a08515a3a9d77c5`; PR run `37413528883` succeeded with **641 unit + 183 integration = 824/824 PASS**. Merge `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`; post-merge main run `37414197830` also succeeded with **824/824 PASS**.

### B2 audit — CI_FIX_IN_PROGRESS
Branch: `auto/b2-audit`
PR: #14

The first PR run exposed a pre-existing local object-store concurrency race. Audit completion is paused until that concrete CI failure is fixed and the exact new head is green.

## Known blockers
- B2.3: RFQ transition/readiness business policy missing.
- B2.4 post-analysis revision semantics depend on the undefined B2.3 lifecycle boundary.
- Production identity provider: deployment decision missing; production auth remains fail-closed.
- PricingEngineV1: commercial policy missing.
- Geometry golden work: representative STEP fixtures/expected outputs missing.
- Repository is public; no confidential customer/production data may be committed.

## Completed work
- B1 foundation/hardening complete; see B1 audit/history.
- B2.1 customer/contact model: DONE, PR #10, 816/816 PASS.
- B2.2 RFQ create/read/list: DONE, PR #11, merge `a3e78077b25f1396f2931b8d3707a071dc075065`, post-merge 820/820 PASS.
- B2.4a draft optimistic concurrency: DONE, PR #12, merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`, post-merge 822/822 PASS.
- B2.5 file manifest endpoints: DONE, PR #13, merge `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`, post-merge 824/824 PASS.

## Current run findings
- Read current main plan, development state and project audit before taking action.
- Inspected PR #14 CI first. Run `37415113531` failed with **640/641 unit PASS** and **183/183 integration PASS**.
- The sole failure was `LocalFileObjectStoreTests.Concurrent_puts_of_same_hash_create_one_correct_object`: two concurrent writers both returned `Created=true` for the same SHA-256.
- Root cause: `File.Exists(finalPath)` followed by `File.Move(..., overwrite:false)` is not a reliable winner-election contract on the CI filesystem; two writers can observe absence and both report creation.
- Fixed only this concrete failure: the final object path is now acquired atomically with `FileMode.CreateNew`; exactly one writer can create the final object and losers return `Created=false` when the path already exists. Content is still hashed before publication and immutable/content-addressed semantics are preserved.
- Runtime fix commit: `cbe86404ec118e7d2a8cadaa4fdf81733e8d0b54`.
- No deterministic calculation engine, canonical snapshot/hash/replay behavior, migration history, tenant policy or business workflow policy was changed.

## CI state
- Verified main remains **GREEN**, run `37414197830`, 824/824 PASS.
- PR #14 previous head failed only the object-store concurrency unit test; integration was 183/183 green.
- New PR head after the focused fix is **CI_PENDING**. Do not mark the audit DONE or merge until GitHub Actions is green for the exact current head.

## Exact next task
Inspect PR #14 CI for the current head first. If it fails, fix only the concrete failure. If green, continue and complete the focused B2 DB/API/replay/tenant-isolation audit, split findings into small follow-up tasks in AUTONOMOUS_BACKEND_PLAN.md, update this state, and require green CI before marking B2 audit DONE. B2.3/B2.4 post-analysis semantics remain blocked unless workflow policy is supplied.
