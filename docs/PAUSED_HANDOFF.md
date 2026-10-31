# Paused checkpoint — 2026-10-31

The user requested a pause at the next logical stopping point and a new session
with the configured Opus external project lead, Sol Codex lead/host acting as
second lead, and DeepSeek builder. The worker has finished and been closed.
No opening-flow implementation was started. Use RESTART_PROMPT.md for the new
session; OPENING_FLOW.md records the agreed outcome, not an accepted architecture.

This is an **experimental source checkpoint**, not acceptance of M1 or a merge
readiness claim. The earlier tutorial combat milestone was mis-scoped: lethal
damage is injected by the harness, not supplied by normal authored tutorial
gameplay. The agreed playable outcome is now new game, Elder Kettle, Isle I map,
Root Pack combat/death/retry/victory, and return to the map.

## Evidence retained

The builder reports successful Windows and Xbox Release builds, the 272-frame
synthetic combat probe, and a 60-frame tutorial movement regression. Current EXE
and XEX timestamps postdate the final source edit. The combat transcript records
target health 20 -> -4, player health 3 -> 2 -> 1 -> 0, independent dealer/player
cooldown checks, and original death callbacks. See VERIFICATION.md and evidence/.

The lead inspected the diff, logs and health-3/health-0 images and checked diff
whitespace. The images demonstrate the health rectangles changing; they also
show substantial rendering defects, including exposed atlas-like character art
in the death capture. They are not evidence of a visually correct playable scene.

Original single-player retry is unproven. Audio is muted/stubbed. Manual shoot
mapping was added but not interactively tested. Full platforming/boss regressions
and Xenia execution of this checkpoint were not run before the pause. Only final
checkpoint stabilization, builds and documentation followed the stop request;
no further feature work or broad test campaign was started.

## Specific findings for the fundamental review

Preserve these as audit inputs, not as assertions that every other path is sound:

- `NumericCompare` now handles floating operands, but its floating `.un` branch
  cases use ordered C++ comparisons; unordered/NaN behavior still needs correction
  and targeted tests. Wider numeric behavior was not independently validated.
- The new `Object_GetType` returns an array's element type for an array object.
  That is not the array's runtime type and needs review.
- `PlayerEvent_Shared` allocates a new object on each call. The original method
  caches a static instance. The shim also writes playerId only for argc >= 2
  using a[1], whereas the original static API has one playerId parameter. Verify
  actual call conventions and per-player behavior; PlayerOne's default value can
  conceal this issue.
- Generic IEnumerable/IEnumerator dispatch now routes broadly through List
  helpers. Verify actual receiver layout before treating arrays, custom iterators
  or coroutine state machines as List enumerators.
- The retry probe can clear SceneLoader.currentlyLoading and checks SceneName.
  Even a matching name would not by itself prove an engine reload or fresh
  lifecycle. The recorded run reports UNPROVEN, and no recursive health-reset
  substitute remains.
- `--combat-test` silently raises a short frame limit to 272 and renders only
  health-capture frames. Its PASS covers the synthetic damage sequence, excludes
  retry, and does not mean the playable milestone passed. Reconsider this command
  contract as part of the verification review.
- The old synthetic StartBasic harness path remains alongside the new manual
  shoot action mapping. Physical input behavior has not been demonstrated.
- The historical diagnosis excerpt contains `enabled=0 flags=1`, whereas the
  current logging helper derives enabled from that bit. Keep the raw excerpt,
  but do not treat its inconsistent fields as a reliable current-state oracle.

The next session should decide which experimental changes to retain, repair or
remove. Do not spend more work fitting normal gameplay to the old harness.

## Space and routing

Approved cleanup reclaimed 4,106,863,495 bytes; approximately 15.81 GB remained
at the cleanup measurement. Unique root captures were checksum-preserved under
work/cleanup-evidence-20261031. Game data, active regression scenes and metadata
remain. The raw-texture duplication/eager-loading architecture still needs review.

All four requested workflow roles are configured. The non-spending Opus adapter
doctor could not find the official Claude Code CLI on PATH in this environment.
This is a runtime readiness gap, not evidence that the external-lead role was
never configured. Recheck the existing subscription route in the next session.
