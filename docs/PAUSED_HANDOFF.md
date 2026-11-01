# Continuation checkpoint — 2026-10-31

The user has revoked the earlier pause and requested continued porting with
all three configured Codex on Crack agents. This file preserves recovery state;
it is not a request to stop or acceptance of the opening milestone.

## Current work

Source baseline `beb3a87` is experimental. Fundamental audit documentation is
committed as `694b9f0`; PORT_REVIEW.md is the canonical diagnosis and sequence.
The conditional strategy retains original IL plus a native Unity service layer
for a bounded pilot. A whole-engine rewrite has no measured justification.
Scene transitions/lifecycle, animation/events, dialogue/UI, storage and bounded
asset residency remain prerequisites for the agreed opening flow.

CP1 implements coroutine handles/ownership/joins, stop hooks, fault accounting,
strict ancestry filtering, diagnostics and synthetic engine fixtures. Its
source is still uncommitted and NOT accepted. The initial builder completed
and was closed. Opus source review and independent Sol review rejected this
version. Their validated consolidated correction is assigned to the sole
DeepSeek writer. Finish its implementation, tests and acceptance review before
starting the next checkpoint.

Latest original tests/builds, prior to review corrections:
- Windows and Xbox Release builds exited0. Exact hashes/timestamps and commands
  are in VERIFICATION.md. This is compile evidence, not console execution.
- Original CupheadTime wait/owner and synthetic join controls exited0; temporary
  null-handle negative builds exited1 and were restored before final builds.
- Strict ancestry matching native Color.get_gray exited1; unrelated prefix0.
- RootPack600frames exited0, explicitly NOT_PLAYABLE, blocked on Animator
  SetInteger. Ledger output completeness is under review; do not trust its
  group count or strict pass as proof that all failures were accounted for.
- Tutorial play120frames, Flower boot and synthetic combat272frames exited1.
  Tutorial reports UnlockAchievement argument mismatch; Flower reports inactive
  coroutine owners; combat reports synchronous TweenValue failure. Causes must
  distinguish newly surfaced defects from regressions introduced by CP1.
- Direct tutorial/platforming/Train/Frogs boots exited0 despite faults. They are
  smoke tests, not gameplay acceptance.

Sol found pending issues in summary iteration/strict completeness, fault-time
ancestry on truncation or ledger exhaustion, reentrant cancellation and the
synchronous recursion bound. Do not advance on an assumption that CP1 passed.

## Exact workflow and recovery

Sol gpt-6.1-sol stays host and independent second lead. Opus
claude-opus-5-5 subscription at requested high effort owns architecture,
assignments and acceptance. DeepSeek opencode-go/deepseek-v4.1-flash router,
crack_builder at max, implements/tests. Host dispatch only; one writer.
Do not substitute models/providers, reconfigure roles or spawn a review council.

User authorization: existing Max subscription quota only, zero API spend.
The adapter's required numeric budget reference is0, not a quota observation or
API permission. Previous Opus summaries verify exact model, subscription,
apiKeySource none and no fallback. Effort is requested but not observed in its
stream. The native child's turn_context and exact-thread router telemetry
corroborate DeepSeek/max/opencode-go; its header separately says openai.
Preserve that metadata distinction without inventing a route change.

Private ignored recovery files live under work/restart-20261031/:
- opus-next-request.json: validated CP1 assignment; cp1-host-addendum.md: parser
  scope extension; cp1-review-context.md: completed worker context.
- cp1-builder/: detailed positive/negative controls and regression logs.
- opus-cp1-review-run/ and opus-cp1-consolidate-run/: completed source review
  and validated correction. Latest completed valid external run is
  opus-a1-contract-run, Claude session
  7af156a8-bc69-488c-aa67-4e588bf3c329. Resume only a completed valid run.
- sol-cp1-review.md and forthcoming opus-cp1-review.md/corrections request.
- host-runtime-readiness.json and worker-router-activity.json: route evidence.

The activity panel registers exact host/worker logs and adapter runs, launch
disabled. Current host01a193ea-89af-7723-b855-5cbc0f2c06f3; completed/closed
CP1 child01a193fb-f287-7362-9577-6f09884452a8 (/root/cp1_coroutines).
The active correction child is01a194be-38be-7b20-8d15-c1ce11be350a
(/root/cp1_corrections), connected as session-cf4e09f307e7c071. Its exact
DeepSeek/max turn context and successful opencode-go router samples were
verified in cp1-correction-readiness.json. Opus's conditional next decision
places A1 sprite correctness/hard-bounded residency before broad lifecycle;
opus-next-checkpoint.json is schema-valid but NOT dispatched. Sol's contract
review was consolidated in opus-a1-contract-run (rev2): hard texture allocation
cap, eviction between synchronous draws, deterministic over-demand failure,
and one consistent mesh/packing transform. Its acceptance gate remains accepted
CP1. cp1-correction-review-context.md is still a DRAFT until the active child
finishes; do not launch opus-cp1-final-review-prompt.md before completing it.
Reuse the same worker for corrections if available, otherwise only the exact
configured crack_builder. Validate lead-authored requests with the adapter.
Close children on completion. Do not leave a completed worker marked working.

## Acceptance boundaries

The goal remains new game -> Elder Kettle -> visible controllable Isle I ->
RootPack -> authored death/retry -> victory/results -> map with progression.
No scene carousel, forced health, injected animation events or harness reset
may stand in for original gameplay. The tutorial has no authored lethal hazard;
its combat probe is synthetic. Peashot already exists in original new inventory;
do not invent an Elder Kettle inventory grant. Dialogue graph recovery remains
required. Installed-game saves must never be read/written: implement and verify
a port-local path/interface before enabling original storage callbacks.

Rendering is visibly broken, HUD native rectangles, audio stubbed/muted, real
retry unproven. RootPack eagerly retains ~1421MiB raw textures, over the entire
512MiB Xbox budget; observed Windows working set ~1.5GiB and a600frame render
~8minutes. Windows execution, XEX build, Xenia and hardware are separate. No new
Xenia/hardware result exists. A bounded asset/runtime plan is still necessary.

Keep generated data private and bounded. Never commit game assets, decompiled
source, original DLLs, SDK files or work/build bundles. Approved cleanup evidence
and active scene data remain preserved. Commit coherent source/docs checkpoints
only with honest acceptance limits; earlier PASS records are historical.
