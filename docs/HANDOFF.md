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
- **D1 r2 interface dispatch is in progress**, assigned to the same sole
  DeepSeek writer, `/root/cp1_corrections`, native thread
  `01a194be-38be-7b20-8d15-c1ce11be350a`. Its previous CP1 passes finished and
  were closed; this is a new bounded follow-up, not accepted D1 source.
- Validated brief: `work/restart-20261031/opus-d1-request.json`.
  Authoritative mapping rule: `opus-d1-contract-decision.md`; host constraints:
  `sol-d1-dispatch-addendum.md`; validation: `d1-r2-request-validation.json`.
  D1 evidence belongs under `work/restart-20261031/d1-builder/`.
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
Latest completed run at this update:
`work/restart-20261031/opus-opening-prerequisite-review-run`.
Its decision is `opus-opening-prerequisite-decision.md`.
Before resuming, select the latest successfully completed run; never overwrite
its output. Validate lead-authored requests with lead.mjs before host dispatch.
See private adapter records for exact lifecycle, not a repeated health probe.

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
  A1 addresses sprite correctness and hard-bounded pixel residency. This alone
  cannot prove512MiB total Xbox fit, adequate frame time or GPU support.
- Current Xbox source uses fixed240-frame scripted play. Latest CP1 evidence is
  compilation only, with no new Xenia/hardware run. Historical foundation Xenia
  checks do not validate the current binary or opening flow.

## Next actions and acceptance gates

1. Let the healthy D1 writer finish. Honor its stop conditions if existing
   metadata cannot express a correct map. Close it immediately on completion.
2. Independently review the actual diff, untracked synthetic fixture and final
   evidence; `sol-d1-review-checklist.md` records material checks. Send one
   consolidated result to Opus. Reuse valid tests; rerun concrete risks only.
3. Accept and commit a coherent D1 source/docs checkpoint only after its gates.
   Update this handoff and NEXT_SESSION_PROMPT in that round.
4. Validate/review and dispatch the existing A1 request
   `work/restart-20261031/opus-next-checkpoint.json` (rev3) only after accepted D1.
   Earlier A1 UV/cache/placement constraints remain mandatory. It is not yet
   implemented or accepted.
5. Opus confirmed the generic empty-object defect and placed S1 serialized
   object-graph fidelity after A1 and before common lifecycle. Follow with
   bounded exception/semantic repairs, S2 port-local filesystem storage, scene
   pump, Animator/events, dialogue/UI/map and the actual opening flow. S1/S2 are
   architecture decisions, not dispatched requests. Their future briefs must
   validate decoder eligibility/consumption, binding paths, version compatibility
   and reference semantics before implementation. Avoid an omnibus CLR rewrite.

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
