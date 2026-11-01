# Next session prompt — Opus 5.5 only

Continue the Cuphead360 project at:

`F:\downloads\temp\python\hermes\cuphead360`

You are Claude Opus 5.5 and the **only agent** for this continuation. Use the
existing Max subscription only, with $0 API spend. Do not spawn Codex, DeepSeek
or other agents, and do not retain the previous multi-agent lead/worker split.
Working routing is already established; avoid repeated readiness checks unless
there is an actual failure or configuration change.

Read `F:\downloads\temp\python\hermes\cuphead360\docs\HANDOFF.md` first, then
inspect actual git status/log and the source/evidence state it describes. Read
PORT_REVIEW.md Part A, STATUS.md and the relevant VERIFICATION.md sections;
consult ARCHITECTURE.md, ASSET_PIPELINE.md, BUILDING.md and REVERSE_ENGINEERING.md
as useful. Handoff and this prompt are the only recovery documents. Historical
PASSes and proposed designs are not current acceptance evidence.

**You have full authority to reconsider or rewrite any aspect of this project:**
source code, runtime strategy, architecture, tooling, converter, formats,
rendering, tests, documentation, plans and priorities. Treat the existing work
and comprehensive review as evidence and proposals, not constraints. Keep,
refactor, replace or discard approaches when that makes the most sense for the
objective. Adapt the current project state and documentation to a practical
**single-agent workflow** as part of this continuation. The previous session
was asked only to grant this authority in the prompt, not to undertake that
rewrite or adaptation before pausing.

Your objective is a genuinely playable **alpha release candidate on Xbox 360
console hardware**. The current delivery model is `default.xex` plus a converter
creating accompanying `GameData` from the user's own GOG installation; adapt
build/package details as needed for the required later toolchain transition.
Aim for gameplay and presentation virtually identical to the GOG version.
The existing opening-flow milestone is new game -> Elder Kettle -> visible,
controllable Isle I -> Root Pack -> authored death/retry -> victory/results ->
map with progression. You may revise the implementation path and milestone
ordering to reach the console objective, with honest verification.

**Required toolchain transition:** work with the XDK version until genuine
in-game status is verified via Xenia. Once that in-game status is verified in
Xenia with the XDK version, **immediately begin using xSDK and vulkan4xenon to
finish the port**. Record exactly what was verified. A compiled XEX, a boot
screen or the old finite native movement scaffold is not proof of original
in-game behavior. Xenia functional evidence is separate from real-hardware
performance, memory and playable-alpha acceptance.

The last session was interrupted at the user's request during partial D1 r4.
Its source is unvalidated and uncommitted; existing binaries/tests are from r2.
Resume the precise snapshot and working-tree checkpoint in HANDOFF.md.
Resolve or reconsider that unaccepted D1 state
before assuming its tests passed. The review proposes profiling/content census,
proper game-generic support, indexed asset volumes, original atlas/mesh sprites,
a GPU renderer, runtime/lifecycle/serialization repairs and full original game
flow. You have authority to change those proposals when justified; don't
mechanically follow the old plan or silently preserve shortcuts.

Use original gameplay logic/content and meaningful tests. Synthetic damage,
forced readiness/health, scene carousels and harness resets cannot stand in for
normal play. Protect the user's files and installed saves; establish port-local
storage before original slot/save callbacks. Preserve source history and never
commit original game assets/assemblies/decompiled source, proprietary SDK files,
or generated work/game/build bundles. Make coherent source/docs checkpoints
with explicit remaining gaps.

At the end of each round, update HANDOFF.md and this NEXT_SESSION_PROMPT.md
together. Continue autonomously toward the console alpha candidate, using your
judgment and full project authority within the user's instructions.
