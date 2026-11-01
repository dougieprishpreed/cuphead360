# Prompt for continuing the port

Continuation note (2026-11-01): the fundamental audit is complete in
`PORT_REVIEW.md`, committed as `694b9f0`. Opus and the independent Sol host have
accepted the bounded CP1 coroutine/fault-ledger checkpoint. D1 interface
dispatch is next, before A1 asset correctness/residency. Read
the current `PAUSED_HANDOFF.md` and working tree before repeating tests or
starting another checkpoint. The user revoked the earlier pause and requested
continued porting with all three configured agents. Do not stop at a handoff
or repeat the fundamental audit mechanically.

Use Codex on Crack to take over the Cuphead360 native port at:

`F:\downloads\temp\python\hermes\cuphead360`

The purpose of this session is to challenge the port's foundations, establish a
credible plan, and then make measurable progress toward real playable gameplay.
Do not continue the previous plan mechanically. The framework, architecture,
implementation and tests are all open to redesign or replacement when justified.

## Required working arrangement

Use these existing Codex on Crack roles exactly; do not replace or reconfigure
them merely because another route is convenient:

| Configured role | Exact model and route | Configuration status |
| --- | --- | --- |
| Codex lead | `gpt-6.1-sol` · host | Configured |
| External project lead | `claude-opus-5-5` · subscription | Configured |
| DeepSeek builder | `opencode-go/deepseek-v4.1-flash` · router | Configured |
| Native worker dispatch | host | Host action only |

- **External project lead — Opus.** Own the fundamental review, architecture decisions,
  implementation sequence, worker briefs and acceptance review. Use the
  supported Codex on Crack external-lead route with the existing official
  Claude Code subscription login. The installed adapter currently targets
  `claude-opus-5-5`; verify current support rather than assuming availability.
- **Codex lead — GPT-6.1 Sol, acting as second lead and host.** Keep Sol selected in the composer.
  Independently challenge consequential assumptions, validate Opus's proposed
  work against the repository and original game, dispatch the native worker,
  handle host-only tools, and check evidence. Sol must be an active technical
  reviewer, not merely relay messages.
- **DeepSeek builder — implementation and tests, max effort.** Use
  `crack_builder`, currently `opencode-go/deepseek-v4.1-flash`. Give it bounded
  assignments with explicit file ownership and acceptance. Keep one writer by
  default; do not spawn an extra review council or let workers delegate.

Read the installed `codex-on-crack:crack`, external-lead reference, relevant
setup/selection instructions and panel skill. Run the native worker and Opus
readiness checks. These roles are configured; configuration is distinct from
runtime readiness. This session successfully ran the official Claude Code CLI
on the exact Opus subscription model. The CP1 child's exact model/max effort
and opencode-go router activity were corroborated. Reuse recorded evidence,
and verify current access on resumed runs. The user explicitly authorized
Max subscription quota only and zero API spend; the adapter's numeric budget
reference is zero and does not enable API billing. Do not ask again merely
because the adapter requires that field.
Do not silently replace a requested model, switch providers, invent a budget,
or fall back to an API billing route. Respect existing permissions and approved
limits. Validate external-lead requests through the adapter; the host chooses
and executes its own tools.

Open the Codex on Crack activity panel. Register this host's exact session log,
the native worker's exact log and Opus runs so actual activity is visible. Keep
configured routes distinct from observed routing. Reuse the panel rather than
opening multiple views.

## Read and establish the actual baseline

Read `docs/STATUS.md`, `docs/VERIFICATION.md`, `docs/OPENING_FLOW.md`,
`docs/PAUSED_HANDOFF.md`,
`docs/ARCHITECTURE.md`, `docs/ASSET_PIPELINE.md`, `docs/COMPATIBILITY.md`, and
`docs/BUILDING.md`, plus any applicable AGENTS.md. Inspect git status/log and the
latest checkpoint. `beb3a87` is the experimental pre-audit source baseline;
`694b9f0` contains the canonical audit documentation. Use actual current HEAD
and working tree; CP1 changes may remain uncommitted pending acceptance.

Treat documentation as claims to audit. Several older sections contradict
newer checkpoints. Build an evidence-backed inventory of what is implemented,
stubbed, approximated, tested, or merely planned. Protect existing user changes.

The previous session made a fundamental planning error: it used injected lethal
damage in the tutorial as the milestone for normal end-to-end combat without
first checking the tutorial's authored gameplay. The tutorial has no authored
enemy/hazard supplying normal lethal damage. The synthetic probe can exercise
real player damage methods, but that does not validate ordinary tutorial combat
or the new-game flow. Do not alter the tutorial to fit a test.

The paused work includes player damage/invulnerability/death and HUD work.
Verify its actual checkpoint evidence. An original Retry invocation returning
success was not proof of scene reload; the scene request did not propagate.
An unconditional harness rerun with health reset is not valid retry evidence.
The paused handoff also records lead review findings in numeric branch semantics,
array GetType, generic event/enumerator shims, input injection and visibly broken
rendering. Treat these as explicit audit inputs; the checkpoint is experimental.

## Review the foundations before choosing more implementation

Have Opus lead a practical audit, with Sol challenging its important conclusions:

1. Is the metadata/IL interpreter plus native Unity-compatible layer a viable
   route to this Xbox 360 port? What evidence supports it? What should be kept,
   repaired, replaced or deferred? Compare alternatives only where they affect
   a concrete decision. Avoid an endless research exercise or an ungrounded
   rewrite.
2. Audit startup, scene transitions, persistent objects/data, lifecycle,
   cloning/destruction, coroutine ownership/timing, delegates/generics and
   unsupported-call handling. Identify catch-all shims that silently report
   success while skipping behavior. A passing harness must not conceal them.
3. Audit the real opening scenes and their dependencies: UI/text, dialogue and
   initial weapon grant, world-map renderers/geometry and movement, interaction,
   input, animation, collision, audio, boss phase logic, game over and retry,
   victory/results and progression/save state. Verify original scene rules and
   call paths before designing each test.
4. Audit Xbox feasibility: C++03/VS2010/XDK, 32-bit big-endian PowerPC, 512 MB
   shared memory, CPU/frame cost, eager texture loading and the software renderer.
   Separate Windows execution, Xbox compilation, Xenia execution and hardware
   validation. A Windows PASS or compiled XEX is not console gameplay evidence.
5. Review the asset pipeline. Custom scene/metadata packages and raw per-sprite
   RGBA crops are development formats. Atlasing/streaming/Xbox layout support
   were planned, not completed. Decide the next concrete packaging, deduplication,
   streaming and target-format steps with measured memory and visual evidence.
   A GPU-native texture format needs a renderer that can consume it.
6. Review verification itself: tests must distinguish original authored gameplay
   from synthetic stimulus, assert meaningful outcomes, and fail when required
   behavior is absent. Direct scene boots are useful regressions, not a playable
   game loop.

Produce a concise diagnosis, prioritized risks, decisions with reasons, and a
small sequence of runnable checkpoints. Record consequential design choices.
Proceed within the authorized scope after that review; ask only when a missing
decision materially changes the agreed outcome, permissions or provider route.

## Agreed playable milestone

The user selected the normal opening flow as the next real milestone:

**New game -> Elder Kettle's house/required interaction -> visible controllable
Inkwell Isle I map -> Root Pack boss -> death/game over/retry -> victory/results
-> return to the map with progression retained.**

An original supported intro skip is acceptable. The tutorial is optional and
remains a regression. Root Pack is the selected acceptance encounter; it is
not claimed to be the only possible first boss in the original game.

Use original logic and content for the transitions, weapon grant, map entrance,
boss attacks/phases, damage, retry and win flow. Native code supplies the engine
services. No manufactured scene carousel, forced health changes, or synthetic
enemy damage may stand in for normal gameplay. Recover required dialogue data;
the existing empty dialogue scaffold is not sufficient for the opening.

Require real interactive input, a rendered health HUD driven by
PlayerStatsManager.Health, authored enemy/player damage, independent-source
invulnerability checks, death, original retry with a fresh encounter, victory,
and map progression. Keep port save data separate from installed-game saves.
Document exact save persistence and any audio/input/animation/physics gaps.
Explicitly muted runs are permitted, but absent audio remains a gap.

## Execution, evidence and housekeeping

Delegate implementation and tests to DeepSeek. Opus reviews the result; Sol
independently checks important assumptions and acceptance risks. Reuse valid
evidence and avoid duplicating a healthy worker's discovery. Close finished
native workers using the host's required lifecycle tools.

Run checks appropriate to each change and build both Windows and Xbox targets
for significant shared-code changes. Preserve tutorial/platforming and prepared
boss regressions. Record reproducible commands, exit codes, field/state probes,
framebuffer screenshots and important limits in `docs/VERIFICATION.md`.
Maintain one clear current status and mark older records as historical.

The project previously measured about 19.9 GB. Approved cleanup reclaimed
4,106,863,495 bytes, leaving about 15.81 GB at that checkpoint. It removed 22
obsolete extraction snapshots and redundant PNG sprite previews while preserving
41 screenshots/previews under `work/cleanup-evidence-20261031`. Local audit files
are `work/cleanup-manifest.json` and `work/cleanup-result.json`. Active regression
scenes, metadata, source, game installation and current combat evidence remain.

Keep generated output bounded as work proceeds: optional previews, one canonical
current extraction per scene, immutable shared content where appropriate, and
compact evidence. Do not delete active test data, unique evidence, source,
repository history, or the installed game. Never commit original game assets,
decompiled game source, proprietary SDK files or giant generated bundles.

Commit coherent source/docs checkpoints with honest remaining gaps. Do not mark
the playable milestone complete until the actual route and its verification
evidence exist. Start by reporting the verified model arrangement and the
fundamental audit findings, then carry the chosen plan through real execution.
