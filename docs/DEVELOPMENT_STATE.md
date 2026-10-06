# Development State

Updated: 2026-10-06
Mode: autonomous backend-first development

## Baseline
- Current verified main CI after B2.5: **641 unit + 183 integration = 824/824 passed**.
- B2.5 merge: `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`; post-merge run `37414197830` succeeded.
- Frozen deterministic engines/snapshots/hashes/replay and existing migration history remain unchanged.
- B2 focused audit: `docs/B2_RFQ_BACKEND_AUDIT_2026-10-06.md`.

## Current milestone
**B2 — Real RFQ backend / focused audit**

### B2.3 RFQ deterministic state machine — BLOCKED_BUSINESS_POLICY
Repository defines status vocabulary and the progressed-input invariant, but no allowed transition graph or READY_FOR_ANALYSIS/readiness policy. Do not invent workflow policy.

### B2.4a Draft optimistic concurrency — DONE
PR #12; merge `ceb458d203c5a298ddfdf0f2adb7a3c865dfb757`; post-merge run `37398187730`; **822/822 PASS**.

### B2.4b Post-analysis revision semantics — BLOCKED_BUSINESS_POLICY
The analysis/lifecycle boundary is undefined until B2.3 policy is supplied.

### B2.5 File manifest endpoints — DONE
PR #13; merge `e8cc222f1cfc3d9c8b43231b6ed9a29fad6877c6`; post-merge main run `37414197830`; **824/824 PASS**.

### B2.H1 Local object-store atomic publication — CI_PENDING
Branch: `auto/b2-audit`
PR: #14

The first audit CI exposed concurrent same-hash publication. The original fix elected a writer with `FileMode.CreateNew` but exposed the final path while bytes were copied. The final audit implementation keeps the complete hash-verified temporary object and uses bounded striped in-process publication gates before atomically renaming it to the content-addressed final path. No immutable metadata/history semantics changed.

### B2.H2 Draft-only RFQ creation guard — READY
B2.2 requires create-draft semantics, but the current create DTO accepts arbitrary status. Restrict creation to `New` without defining any later workflow transition. Start only after PR #14 is merged and post-merge main CI is green.

### B2 audit — CI_PENDING
Audit evidence and findings are recorded. Do not mark DONE until GitHub Actions is green for the exact final PR #14 head.

## Known blockers
- B2.3: RFQ transition/readiness business policy missing.
- B2.4b: post-analysis revision semantics depend on the undefined B2.3 lifecycle boundary.
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
- Re-read current `main`, AUTONOMOUS_BACKEND_PLAN.md, DEVELOPMENT_STATE.md and PROJECT_AUDIT_2026-10-05.md before changing the audit branch.
- Inspected PR #14 first. Head `aa8ba2f585a985a5742b94aac664cb1b04a1bd7a` had green run `37418685168`: **641/641 unit + 183/183 integration = 824/824 PASS**.
- Reviewed the concurrency repair and found an integrity issue despite green CI: writing directly to the final path with `FileMode.CreateNew` makes the content-addressed path visible before the copy finishes and can leave partial final bytes after interrupted I/O.
- Replaced that publication step with hash-verified temporary-file publication guarded by a fixed set of in-process striped semaphores, followed by atomic rename. This preserves the prior complete-object visibility behavior while deterministically electing one in-process writer.
- Wrote the focused B2 audit covering DB constraints, API surface, historical replay and tenant isolation.
- Replay evidence remains green: existing `GoldenPersistenceTests` replay stored snapshots 100 times, preserve historical results after master-data changes, enforce snapshot immutability and tenant scoping.
- Tenant-isolation evidence remains green across authorization middleware, customer/contact composite FKs, RFQ repository filters, RFQ files/manifest and snapshot/calculation repositories.
- Audit finding B2.F2: RFQ creation currently accepts an arbitrary status even though B2.2 specifies create-draft semantics. Split follow-up B2.H2 to constrain create to `New` without inventing transition policy.
- Plan status drift was corrected: B2.2/B2.5 are DONE, B2.4 is partial, B2.3/B2.4b are business-blocked, B2.H1/H2 are explicit.
- No pricing, margin, approval, customer, geometry, production or security policy was invented. No deterministic engine, canonical snapshot/hash/replay bytes, immutable history or migration history was changed.

## CI state
- Verified main: **GREEN**, run `37414197830`, 824/824 PASS.
- PR #14 previous reviewed head `aa8ba2f...`: **GREEN**, run `37418685168`, 824/824 PASS.
- Final audit/code/documentation head after the atomic-publication refinement is **CI_PENDING**.
- Do not merge or mark B2 audit/B2.H1 DONE until that exact head is green.

## Exact next task
Inspect PR #14 CI for the exact current head. If it fails, fix only the concrete failure. If green, merge only that verified head and verify post-merge `main` CI. On the next autonomous run, start **B2.H2 draft-only RFQ creation guard** before any B3 work. B2.3/B2.4b remain blocked unless workflow policy is supplied.
