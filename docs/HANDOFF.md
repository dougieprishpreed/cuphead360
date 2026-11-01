# Handoff

**PAUSED 2026-11-01 at the user's request.** The worker was interrupted; no
further implementation, builds or tests are requested before continuation.
This is the canonical recovery state. Update this file and
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
- **D1 r4 is interrupted, partial, unvalidated and uncommitted.** The user
  explicitly asked to interrupt the worker and wrap up now. The sole writer
  `/root/cp1_corrections`, thread `01a194be-38be-7b20-8d15-c1ce11be350a`, was
  interrupted with interrupt_agent. Do not resume this worker or the old split.
- Preserved pause snapshot:
  `work/restart-20261031/pause-opus-only-20261101T133312Z/` contains a manifest,
  tracked diff and exact copies of partial source, both untracked D1 fixture
  files and their docs. The working tree is the actual continuation state.
- Interrupted source: interpreter.cpp SHA632D4A01..., interpreter.h0E6808CF...,
  app.cpp4E242AD5..., fixture294B9942..., fixture build55FD9695.... Full hashes
  are in the snapshot manifest. Existing EXE91DBF58D/XEXD4007458 are still r2:
  **they do not validate the current r4 working tree**. No post-interrupt build
  or test was run, and none is claimed.
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
- **Latest user instruction: interrupt now and pause.** Do not finish r4,
  start E0, launch another review or do further implementation in this session.
  At resume, Opus5.5 alone takes over with full authority in NEXT_SESSION_PROMPT.
  No workflow/toolchain rewrite was undertaken during wrap-up.

## Historical: workflow used for the completed round

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

**Next session supersedes the multi-agent arrangement above:** Opus5.5 is the
only agent. The user authorizes Opus to reconsider/rewrite any code, tools,
architecture, tests, plans or docs and adapt the project to a single-agent
workflow to reach a playable alpha release candidate on console hardware.
That adaptation is future work, not something to execute during this wrap-up.

The user also requires the next-session prompt to state: once genuine in-game
status is verified in Xenia with the XDK version, immediately begin using
**xSDK and vulkan4xenon** to finish the port. The current native240-frame scaffold
is not such original-gameplay verification. Hardware validation remains distinct.

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
single consolidated r4 correction, now interrupted. No Opus run is active.
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

## Continuation actions (Opus5.5 only)

1. Read the preserved partial r4 working tree, manifest and r4 request. Decide
   whether to finish, revise or replace it using the user's full authority.
   Do not assume it compiles or that r2 binaries validate it. No source since
   CP1 is accepted. If continuing the bound, preserve visible generic-base
   failures rather than hiding them through native fallback.
2. Adapt the current project and documentation to a single-agent Opus workflow.
   Previous lead/worker rules describe history; the latest user instruction
   supersedes them. Do not spawn Sol/DeepSeek/other agents.
3. PORT_REVIEW.md Part A is the completed engineering review, not a binding
   design. Opus may reconsider any implementation, architecture, converter,
   test, format, doc, plan or priority to reach a console alpha candidate.
4. The review proposes measurement/content/API census, native binding, generic
   runtime support, packed atlas/mesh data and GPU rendering before broad
   gameplay systems. Opus may change this path when justified. No E0 work began.
5. Verify genuine in-game status in Xenia with the XDK build, then immediately
   begin using xSDK and vulkan4xenon to finish the port, as the user requires.
   The existing finite native scaffold is not that verification. Final console
   hardware acceptance remains separate.
6. Maintain HANDOFF.md and NEXT_SESSION_PROMPT.md together at each round's end,
   and keep explicit evidence/provenance for coherent source checkpoints.

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
