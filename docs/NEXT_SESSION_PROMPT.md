# Next session prompt

Updated 2026-11-01: comprehensive shipping review completed; D1 correction r4
is active. Maintain this prompt and HANDOFF.md together at the end of every
round of changes. They are the only recovery documents.

Continue the Cuphead360 port in:

`F:\downloads\temp\python\hermes\cuphead360`

Read `F:\downloads\temp\python\hermes\cuphead360\docs\HANDOFF.md` first, then
inspect actual git status/log and the current writer/review state. Read
PORT_REVIEW.md Part A for the current shipping plan, STATUS.md and the relevant
VERIFICATION.md evidence. Consult ARCHITECTURE.md, ASSET_PIPELINE.md and
BUILDING.md as needed. Old pause/restart files were retired; PROGRESS.md is
historical. Do not trust old PASS labels or create another handoff file.

Use the existing three-agent Codex on Crack workflow exactly:
- Sol gpt-6.1-sol stays host, second lead and independent technical reviewer.
- Opus claude-opus-5-5 uses the existing Max subscription, high requested,
  as project lead for architecture, briefs and acceptance.
- The sole crack_builder is opencode-go/deepseek-v4.1-flash via router at max.
- Native dispatch is host-only; no substitutions, extra council or worker
  delegation. Preserve others' edits and close finished children immediately.

Max subscription only, $0 API spend is already authorized. Routes work: reuse
configuration/evidence, without repeated readiness/routing checks. Recheck only
for a real failure/configuration change. Reuse the panel and validate external
requests before dispatch.

Latest accepted source is4c40d42 (CP1). The same exact builder is implementing
D1 correction r4 from the validated opus-d1-review-request.json, with host
clarifications and evidence paths in HANDOFF.md. D1 is still unaccepted and
uncommitted; r2 hashes/results are versioned prior evidence. Finish correction,
review, acceptance and a coherent checkpoint before the next feature. Honor
any generic-base stop condition; no native fallback may conceal it.

The completed Opus shipping review recommends one indexed archive with bounded
data volumes, atlas pages plus original sprite meshes, a GPU renderer consuming
Xbox-friendly formats, and a standalone converter. The old A1 crop plan is
superseded. E0 profiling/content/API census is the next feature gate after D1;
follow the current bounded brief and measured go/no-go decisions, not the old
ordering. Target formats/layout, memory fit and60Hz console feasibility remain
unproved. The review's precise coverage and source findings are in PORT_REVIEW.

The goal remains default.xex plus converter-generated GameData, with gameplay
and presentation virtually identical to the user's GOG build on Xbox360. The
near-term milestone is original new game -> Elder Kettle -> visible controllable
Isle I -> Root Pack -> authored death/retry -> victory/results -> map progression.
It is not achieved. No synthetic damage, scene carousel or health reset may
replace it. Port-local storage precedes original slot/save callbacks; installed
saves must remain untouched. Never commit original assets/code, SDKs or generated
work/game/build output. Continue porting; the former pause was revoked.

At the end of this round, update HANDOFF.md and this prompt together, record
honest source/evidence provenance and commit only coherent accepted checkpoints.
