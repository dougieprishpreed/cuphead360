# Handoff

Updated 2026-11-01. This is the canonical recovery state. Update this file and
[NEXT_SESSION_PROMPT.md](NEXT_SESSION_PROMPT.md) at the end of every round of
changes. Keep test evidence in VERIFICATION.md and architecture decisions in
PORT_REVIEW.md rather than creating more handoff documents.

## Current checkpoint and active work

- Repository: `F:\downloads\temp\python\hermes\cuphead360`, branch `main`.
- Latest accepted source: `4c40d42421a9512b5cb459e0417d0758e721dd79` — bounded
  CP1 coroutine ownership/joins/waits and strict fault accounting. Opus and the
  independent Sol host accepted it on 2026-11-01. Both Release builds passed.
- Earlier references: `beb3a87` is the experimental source baseline;
  `694b9f0` is the fundamental audit; `bae43a8` is an intermediate docs commit.
  Inspect actual HEAD and working tree before assuming newer work is accepted.
- **D1 correction r4 is active; D1 is not accepted or committed.** The same
  sole DeepSeek writer `/root/cp1_corrections`, native thread
  `01a194be-38be-7b20-8d15-c1ce11be350a`, was closed after r2 and reused for r4.
- Validated brief: `work/restart-20261031/opus-d1-review-request.json` (r4).
  Host clarification: `sol-d1-r4-dispatch-addendum.md`; validation:
  `d1-r4-request-validation.json`. New evidence: `d1-r4-builder/`; preserve the
  r2 baseline evidence in `d1-builder/`.
- Independent Sol boundary probes found inherited external-interface detection
  and unsupported generic-array dispatch failures after the initial positive
  fixture passed. The same writer corrected them, extended the permanent
  fixture and reran final builds/controls/regressions before review.
  Read `work/restart-20261031/sol-d1-source-findings.md` and the isolated
  `sol-d1-boundary-probe/` .NET/port logs. Do not trust the earlier candidate as
  final source. No production files were edited by the host for those probes.
- Last tested r2 candidate: interpreter SHA47FEDA84, EXE91DBF58D, XEXD4007458 (full hashes in
  VERIFICATION.md and d1-builder/INDEX.md). Host independently rehashed them,
  ran dispatchtest0, scoped D1 strict1 and CP1 victim-root strict1. Tutorial,
  platforming,Flower/Train/Frogs120 and combat272 pass. RootPack150 remains
  NOT_PLAYABLE at the same frame118 null-intro fault. Formal review questions
  and exact provenance are in sol-d1-final-review-context.md.
- The host is consolidating recovery docs and recording independent findings.
  Preserve its changes to HANDOFF, NEXT_SESSION_PROMPT, PORT_REVIEW and README.
  Do not overwrite the builder's active source, STATUS or VERIFICATION edits.
- The earlier pause was revoked. Continue porting through acceptance and the
  next agreed checkpoint; do not stop merely because a handoff exists.

## Required three-agent workflow

| Responsibility | Exact model and route |
| --- | --- |
| Host, second lead and independent technical reviewer | `gpt-6.1-sol` · host |
| Project lead: architecture, briefs, sequence and acceptance | `claude-opus-5-5` · subscription, high requested |
| Sole implementation/test writer | `crack_builder`: `opencode-go/deepseek-v4.1-flash` · router, max |
| Native worker dispatch | Host action only |

Use the installed Codex on Crack skill and external-lead adapter. Do not
substitute models/providers or add a review council. Workers must not delegate.
Give bounded ownership; preserve other edits. Close every finished native child
with interrupt_agent, then reuse the exact worker for appropriate follow-ups.

User authorization: **existing Max subscription quota only, $0 API spend**.
The adapter's numeric budget reference0 is bookkeeping. Do not ask again or
enable API billing/fallback. Runtime routes are already verified; reuse that
evidence. Recheck only when a real failure or changed configuration makes it
necessary. Do not repeatedly run readiness checks or poll route telemetry.
Requested Opus effort is high; its stream does not expose observed effort.

The existing activity panel tracks the exact host/native logs and Opus runs;
reuse it. Panel launch is disabled and there are no visual approval reviews.
Native turn_context/upstream evidence corroborates DeepSeek/max/opencode-go;
the header's separate openai label is a known metadata discrepancy.

Opus uses Claude session `7af156a8-bc69-488c-aa67-4e588bf3c329`, files-only.
Latest completed run:
`work/restart-20261031/opus-release-review-completion-run`.
The comprehensive review is complete in PORT_REVIEW.md Part A. Sol challenged
and corrected unsupported claims and independently checked key findings;
`sol-release-review-acceptance.md` records the assessment. D1 still needs the
single consolidated r4 correction, now dispatched. No Opus run is active.
Before resuming, choose the latest successfully completed run; never overwrite
its output. Validate lead-authored requests before host dispatch. Reuse working
routes and recorded evidence rather than repeating health checks.

## What is accepted, and what is still broken

CP1 provides real coroutine handles and waits, receiver ownership, joins,
cancellation, explicit post-self-stop starts and a bounded fault ledger with
root/full-stack strict matching. Exact final hashes, commands, negative controls
and semantic limits are in [VERIFICATION.md](VERIFICATION.md). Private final
evidence: `work/restart-20261031/cp1-final-narrow-builder/`,
`opus-cp1-narrow-acceptance.md` and `sol-cp1-acceptance.md`.

- Final-binary RootPack150: exit0/77.840s, wait transition at24, fault at118,
  104starts,148groups/9839observations, explicit NOT_PLAYABLE. The older600-frame
  run is prior-version evidence, not a final-source600 claim.
- The frame101 play failure is inherited interface-slot misdispatch. Original
  OnGrounded calls OnlineInterface.SetStat, but raw class slot23 selects
  OnlineInterfaceGog.UnlockAchievement. Derived stack counts are4vs3, not
  directly logged. Same-arity wrong calls can be silent. D1 repairs the map;
  no padding, native success shim or original IL modification is allowed.
- CP1 has a4096 cumulative-start pilot cap/no reclamation. Cancellation/waiter
  policies, lifecycle and Destroy behavior retain documented approximations.
  Budget/refusal and raw-child suppression branches include source-review limits.
- Root Pack's current null fault reads Level.intro.readyComplete. The prepared
  scene has no record preserving the empty serialized intro object. The binder
  creates nested objects only from child records. See PORT_REVIEW and private
  `sol-rootpack-serialized-object-review.md`; a later generic repair is needed.
  Object creation alone cannot supply the real animation completion callback.
- Animator/events, scene transitions, persistent identity/common lifecycle,
  dialogue/UI, map geometry, audio and save behavior remain incomplete. Other
  audited CLR gaps remain in PORT_REVIEW. CP1/D1 are not full CLR/Unity parity.
- Sprite extraction is visibly wrong; eager Root Pack textures exceed1.4GiB.
  The old A1 crop/LRU shipping plan is superseded by atlas pages plus original
  sprite meshes and a GPU path. Pack/texture/layout choices and484MiB planning
  budgets require target probes; no console fit or frame-time claim is proven.
- The shipping review confirmed game-defined generic types are exported as
  external composites and some boss-state/event logic takes native substitutes.
  A host census found27generic type definitions and74generic method definitions.
  R4 only adds fail-visible interface bounds; G1 is the later structural repair.
- Current Xbox source uses fixed240-frame scripted play. Latest CP1 evidence is
  compilation only, with no new Xenia/hardware run. Historical foundation Xenia
  checks do not validate the current binary or opening flow.

## Next actions and acceptance gates

1. Let the same D1 r4 writer finish. Close it immediately on completion. Honor
   its stop condition if original regressions hit `interface dispatch::generic-base`:
   report exact sites/types/counts and return the evidence to Opus; never hide it
   through native fallback or weaken the guard.
2. Independently review the r4 diff, synthetic mutations, counters, tests and
   post-edit builds, then obtain Opus acceptance. D1 r2 passes do not validate
   r4. Reuse trustworthy evidence and rerun concrete risks only.
3. Commit the coherent accepted D1 source/docs checkpoint only after its gates.
   Update this handoff and NEXT_SESSION_PROMPT at the end of the round.
4. **Current project direction:** PORT_REVIEW.md Part A is the completed user-
   requested shipping review of default.xex plus a standalone converter for
   near-GOG gameplay/presentation on Xbox360. Source was reviewed at varying
   depth with explicit coverage; original content was sampled, not certified.
5. **E0 measurement is the next feature gate**, with a fresh bounded brief from
   the project lead after accepted D1: content/dependency and MemberRef census,
   runtime cost classes, allocation/clone behavior and a representative target
   workload. Do not automatically dispatch the old A1 JSON.
6. Proposed route after measurement: bind native calls once; repair game generic
   types; implement indexed bounded pack volumes and atlas/mesh content; build
   the GPU renderer with format/layout probes; serialization/lifecycle/exception/
   numeric/GC checkpoints; original scene/Animator/physics/UI/audio/input/storage;
   then M1 and content breadth. PORT_REVIEW A8 controls dependencies and go/no-go
   gates. Each implementation brief stays bounded; the full release remains
   unproven and no schedule estimate is established.

## Playable goal and original semantics

**New game → Elder Kettle → visible controllable Isle I → Root Pack → authored
death/retry → victory/results → map return with retained progression.**
See [OPENING_FLOW.md](OPENING_FLOW.md). The goal is not achieved. Use original
logic/content. No scene carousel, forced health, injected boss events or harness
reset may substitute for gameplay. Tutorial has no authored lethal hazard;
its synthetic damage probe is a separate engine test. New inventory already
contains peashot. Kettle requires the real dialogue graph, original messages,
power-up/tutorial behavior, animation completion and input restoration.

Port-local storage must be installed **before** SlotSelectScreen.Awake and
PlayerData.Init: original callbacks can read/create slots immediately. Installed
game saves must be neither read nor written. Empty JSON/fabricated success does
not prove persistence. Private source tracing: `sol-opening-prerequisites.md`.

## Environment, evidence and housekeeping

Build using `scripts\build_win32.bat Release` and
`scripts\build_xbox360.bat Release`; outputs are `build\win32\Cuphead360.exe`
and `build\xbox360\default.xex`. Detailed commands/prerequisites:
[BUILDING.md](BUILDING.md), [VERIFICATION.md](VERIFICATION.md).
VS2010/XDK C++03,32-bit big-endian PowerPC,512MiB shared memory are hard target
constraints. Game data is the user's GOG1.3.2/Unity2017.4.9f1 installation.

Metadata: `work/metadata/metadata.chm`. Reuse current prepared tutorial,
platforming and boss scenes rather than re-extracting them speculatively.
Private decompilation: `work/decomp/`. Tools: VS2010 and Xbox360 SDK under
`C:\Program Files (x86)`, MSBuild under Windows .NET Framework v4.0.30319,
ILSpy under sibling `spire360/out/tools/ilspycmd.exe`, Xenia Canary at
`F:\downloads\xenia_canary_windows\xenia_canary.exe`.

Never stage game/,work/,build/, original assemblies/assets/decompiled source or
proprietary SDK files. Use explicit commit allowlists. Preserve user changes,
active test data and unique evidence. Approved cleanup reclaimed4106863495bytes;
its manifests and41retained captures remain in work/cleanup*. Keep generated
output bounded, previews optional and content immutable; do not delete active
data or rerun cleanup without an identified need.

Read current state here; use [STATUS.md](STATUS.md) for the product summary,
[VERIFICATION.md](VERIFICATION.md) for evidence, [PORT_REVIEW.md](PORT_REVIEW.md)
for decisions, and [ARCHITECTURE.md](ARCHITECTURE.md)/[ASSET_PIPELINE.md](ASSET_PIPELINE.md)
for technical constraints. Historical foundation records are not current PASSes.
