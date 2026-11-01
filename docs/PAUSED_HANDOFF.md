# Continuation checkpoint — 2026-11-01

The user revoked the earlier pause and requests continued porting with all
three configured Codex on Crack agents. This is recovery state, not a request
to stop or acceptance of the playable opening flow.

## Current state

- Experimental source baseline: beb3a87. Fundamental audit docs:694b9f0.
- Opus and the independent Sol host accepted the bounded final CP1 source and
  fixtures on 2026-11-01. This document accompanies the coherent source
  checkpoint; find its commit with git log rather than trusting the old
  docs-only HEAD bae43a8.
- The first CP1 writer finished and was closed. The correction writer completed
  the final narrow correction and was immediately closed, as were its earlier
  completed passes. Reusable sole writer: /root/cp1_corrections, thread
  01a194be-38be-7b20-8d15-c1ce11be350a.
- Validated request: work/restart-20261031/opus-cp1-final-request.json. Review:
  opus-cp1-final-review.md. Host addendum:cp1-final-narrow-host-addendum.md.
  Final evidence:cp1-final-narrow-builder/. Acceptance:opus-cp1-narrow-acceptance.md
  and sol-cp1-acceptance.md. D1 dispatch requires this CP1 source commit first.

The final review accepted the real WaitFor objects, exact timing, complete
stored-group accounting, full fault-time strict ancestry, mixed-entry skip
invariant, raw-child pre-linking, explicit fault states and bounded steps.
The two final corrections are complete: explicit API starts remain allowed after
self-stop (only cancelled raw children are suppressed, visibly); scheduler
step/depth/refusal faults must carry the offending entry's root. The earlier
cancellation wording was overbroad. Do not preserve that policy merely because
its synthetic fixture passed.

## Final accepted evidence

The final Windows and Xbox Release builds both exited0, after the last source
edit. Their hashes and commands are in VERIFICATION.md. Core, extended,
original, strict-root and ledger controls passed as specified; temporary
negatives were restored. Sol independently reproduced strict root match1 and
unrelated0. Explicit starts after self-stop complete once while the original
entry stays stopped. The distinct depth-guard victim never enters MoveNext.
Budget/refusal and raw-child suppression branches retain source-review limits.

RootPack150 on the final binary exited0 in77.840seconds: wait transition at24,
fault at118,104starts,148groups/9839observations,NOTEcount0,explicitNOT_PLAYABLE.
Tutorial/Flower boots exited0, syntheticcombat272 exited0, tutorial120play
exited1 at the inherited frame101 interface bug. The final source did not run
600frames. Opus accepted CP1; Sol accepted independently. No opening-flow claim.

## Evidence already completed, before the narrow correction

Both Release builds exited0 after the source edit. Exact hashes/timestamps and
commands are in VERIFICATION.md and the private correction summary. Core,
extended and original controls passed. All seven negative controls were restored;
the innermost32-only negative yields0 instead of expected strict1, so the
external expectation fails. A zero process code is not a passing negative test.

RootPack600 exited0 in743.262seconds (about0.807FPS), with real WaitForSeconds,
initial state1 and transition atframe24, then startBattle_cr fault atframe118.
It reported NOT_PLAYABLE,104starts,148groups/37963observations,no loss, allocator
peak1553571688bytes. The host independently confirmed those rows and sampled
~1.56GB working set/one CPU core. The earlier interrupted run is not final.
These results do not validate authored attacks, retry, scene transitions or Xbox.

Tutorial/platforming/Train/Frogs/Flower direct boots exited0. Train/Frogs short
play exited0; tutorial/platforming/Flower play exited1 atframe101. Synthetic
combat272 exited0 withhealth0, retaining a Color.Lerp visual-coroutine fault.
New F1/F2 code needs fresh appropriate controls/builds and the lead's150-frame
RootPack comparison, not another600frame run. Do not treat the old binary as the
new source's validation.

The three play failures are INHERITED INTERFACE-SLOT MISDISPATCH, not an original
achievement request. OnGrounded pc375 token0x06003a9c declares OnlineInterface.
SetStat (method15004,signature2320,3explicitargs+receiver=4,interfaceslot23).
InvokeVirtual instead selects OnlineInterfaceGog.UnlockAchievement (method15040,
signature2303,PlayerId/string+receiver=3,classslot23). The previous actual2 claim
was inferred and wrong. Counts4vs3 derive from metadata/current code, not an
explicit runtime count log. sol-t3-dispatch-evidence.json and the builder's
t3-dispatch-clarification.md agree; InvokeVirtual is unchanged sincebeb3a87.
Arity-compatible wrong dispatch can fail silently elsewhere. Never pad arguments
or add a success shim to conceal it.

## Exact workflow and next sequence

Sol gpt-6.1-sol stays host/independent second lead. Opus claude-opus-5-5
subscription at requested high effort owns architecture, briefs and acceptance.
DeepSeek opencode-go/deepseek-v4.1-flash router/crack_builder at max implements
and tests. Host dispatch only, one writer, no model/provider substitutions.
Close every completed child with interrupt_agent; reuse the exact worker for
bounded follow-ups when available.

User authorization persists: existing Max subscription quota only, zero API
spend. Adapter budget reference0 is bookkeeping, not an API permission or quota
observation. Do not ask again merely because the numeric field is mandatory.
Completed Opus runs verify exact model/subscription/apiKeySource none/no fallback.
High effort is requested, not observable in its stream. Native turn_context and
exact-thread opencode-go upstream samples corroborate DeepSeek/max; the parent
header separately saysopenai. cp1-correction-readiness.json retains evidence.

Latest completed valid Opus run:work/restart-20261031/opus-cp1-narrow-acceptance-run.
Same Claude session7af156a8-bc69-488c-aa67-4e588bf3c329. Resume a completed valid
run only, files-only profile unchanged. The activity panel registers exact host,
child and external runs, launch disabled; child session-cf4e09f307e7c071.

After accepted CP1, Opus recommends D1 game-interface dispatch correctness,
then A1 sprite correctness/hard-bounded residency, then common lifecycle and
separate required semantic fixes, scene pump, Animator/events, dialogue/UI/map,
and the actual opening flow. D1 request:opus-d1-request.json. Sol identified
an inherited-map defect in its first proposed rule; Opus is correcting the
contract in opus-d1-contract-run. Validate the amended request and independently
review it before dispatch. A1 rev3 is NOT dispatched, and requires D1 acceptance;
the earlier review clarified UV origin and quarter-rotation bounds.

A1's measured basis:8Player pages share a texture name; the current cache
incorrectly treats them as one. All2392entries are tightly packed;356also need
packing transforms. Ghost/hit crops can be completely transparent. Keep page
identity, reconstruction, placement, immutable content and hard residency bounds
separate from animation/gameplay claims. Never claim Xbox fit from a pixel-cache
budget alone. Source tests use synthetic pixels; originals stay private work data.

## Acceptance boundaries

Goal: new game -> Elder Kettle -> visible controllable Isle I -> RootPack ->
authored death/retry -> victory/results -> map with retained progression.
No scene carousel, health writes, injected boss events or harness reset may
substitute for gameplay. Tutorial has no authored lethal hazard. New inventory
already has peashot; preserve Kettle's actual dialogue/tutorial/power-up behavior.
Recover dialogue. Port-local storage must be installed before enabling original
save callbacks; installed-game saves must be neither read nor written.

Numeric unordered/unsigned branches, array GetType, PlayerEvent.Shared,
IEnumerable dispatch, finally/exception behavior and lifecycle/identity remain
explicit audit gaps in PORT_REVIEW.md. CP1 is no full CLR/Unity parity claim.
Rendering is broken, HUD rectangles limited, audio stubbed/muted, retry unproven.
RootPack eagerly retains~1421MiB textures against512MiB total Xbox memory.
Current Xbox entrypoint is fixed240frame auto-play; no runtime fixture selector
or new Xenia/hardware result exists. Compilation is separate from execution.

Preserve active extraction data and cleanup evidence. Never stage game/,work/,
build/,decompiled source,original DLLs,SDK files or giant bundles. Commit coherent
source/docs checkpoints with honest acceptance limits, then continue; no pause
has been requested now.
