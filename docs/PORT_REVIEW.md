# Fundamental port review — 2026-10-31

This review audits source checkpoint `beb3a87`. It supersedes architectural and
roadmap claims that describe planned facilities as implemented. It is not
acceptance of the playable opening flow. The implementation checkpoints below
must supply their own evidence in [VERIFICATION.md](VERIFICATION.md).

The requested roles are retained: Sol is the host and independent second lead;
Opus 5.5 is the external project lead; `crack_builder` is the DeepSeek V4.1 Flash
implementation/test worker at max effort. The adapter completed the initial
Opus audit on the exact `claude-opus-5-5` subscription route with no fallback,
no API-key source, and `not-billed-via-api` usage provenance. High effort was
requested through the supported process-local setting; the stream does not
expose an observed effort value. The user authorized Max quota only, with zero
API spend. The native worker's turn_context records the exact DeepSeek model
and max effort. Its exact-thread router_upstream activity reports successful
opencode-go requests on that model, corroborating the configured router route.
The session header separately says openai; that metadata discrepancy is retained
in the local readiness evidence rather than treated as a provider switch.
Host, Opus runs and the native worker's exact log are registered in the existing
activity panel. No role, model or provider was substituted. Readiness is not
source acceptance.

## Strategy decision

Retain original IL execution and the native Unity service layer for a bounded
pilot. Original startup, inventory, movement and damage paths provide useful
evidence that this approach can execute recovered game logic. They do not
establish CLR correctness, original-game fidelity, console performance or the
viability of the complete port.

Replacing the interpreter with Mono/AOT would still leave the Unity service,
content and presentation work. An immediate whole-engine rewrite has no
measured advantage. Reconsider the managed execution strategy if a small
correctness pilot cannot support the opening's actual call paths. Hot-path
translation is a future option, not an implemented escape hatch or a substitute
for correct semantics.

The current interpreter decodes IL during execution. Predecoding, collection,
asset streaming, a GPU renderer and production texture packing remain planned.
The existing scene/play harness is useful for regressions; a common lifecycle
and scene loop must replace its hand-selected gameplay callbacks.

## Findings and priorities

| Priority | Finding | Consequence and decision |
| --- | --- | --- |
| Critical | StartCoroutine returns null; no coroutine owner or join handle; Stop APIs are no-ops. | Repair handles, waits, owner lifetime and observable failures first. Preserve the distinction between an independently started coroutine and a raw nested iterator. |
| Critical | The scheduler silently disables all VeggiesLevel coroutines. | Root Pack spawn/phase/win code cannot execute. Remove the filter with diagnostics; diagnose remaining failures rather than replacing the filter. |
| Critical | Scene loads are no-ops or unsupported; active scene is manufactured; persistence is a no-op. | Build an original-request-driven transition pump, stable object identity, persistent roots, teardown and fresh lifecycle. A matching scene name does not prove retry. |
| Critical | Animator parameters, transitions and events are absent. | Potato Shoot and death events cannot fire. Test event-driven spawning and phase changes after adding the consuming runtime and extracted data. Do not inject the missing event as gameplay evidence. |
| High | Original callback failures can coexist with exit 0/PASS. | Add strict fault accounting, caller context and negative tests. Keep legacy smoke-test scope explicit. |
| High | throw/rethrow fail calls, leave skips finally, and endfinally faults; array runtime types, generic statics/events and iterator interface dispatch also have gaps. | Repair through separate, independently testable semantic checkpoints. Executing original bytes alone does not preserve their meaning. |
| High | Destroy mainly deactivates objects; component destruction, lifecycle and coroutine cancellation are incomplete. | Establish a common lifecycle before relying on scene reload or persistent state. |
| High | UI/TMP rendering and the real Dialoguer graph are absent. | Recover required data and the actual title/slot/house/map/game-over/results presentation. Native HUD rectangles are limited engine evidence. |
| High | JsonUtility produces an empty object; persistentDataPath and saves are stubbed. | Use a port-local store and original progression logic. Distinguish in-memory return to map from persistence after process restart. |
| High | Eager raw textures exceed console memory; CPU/frame cost is unmeasured. | Bounded residency and measured peaks are prerequisites for console gameplay. Profile before selecting acceleration work. |

The paused-handoff defects remain explicit audit inputs: unordered floating
branches and integer unsigned width/sign handling, array GetType,
PlayerEvent.Shared caching/arguments, generic
enumerator dispatch, retry propagation, synthetic input and broken rendering.
The original weapon inventory already contains peashot. Elder Kettle's message
drives the authored tutorial/power-up sequence; tests must not invent a new
inventory grant.

## Original route and content dependencies

Source tracing establishes the intended calls, not an executed route:

1. StartScreen confirmation requests slot selection. The new-game branch of
   SlotSelectScreen configures Cutscene.Load with the intro and house target.
   Cutscene.Skip uses the original configured destination.
2. HouseLevel/HouseElderKettle consume the recovered dialogue graph and its
   variables/messages. HouseLevelExit waits, then requests Isle I.
3. MapPlayerMotor uses two-axis Rigidbody2D velocity and LateUpdate ladder
   clamps. MapLevelLoader records return positions, opens the difficulty UI,
   and calls SceneLoader.LoadLevel after confirmation.
4. VeggiesLevel runs its original potato/onion/carrot/win coroutine chain.
   Potato attacks and death require Animator events. Difficulty matters.
5. LevelGameOverGUI.Retry calls SceneLoader.ReloadLevel. WinScreen returns
   through LoadLastMap. These must reach a real scene pump with progression
   retained; direct boots and harness reruns cannot demonstrate them.

Read-only inspection of original scene files found 173 SpriteRenderers and
40 EdgeCollider2D components in Isle I. The current extractor omits edge
colliders. Title/slot/house/results also depend on Canvas/text presentation;
results includes mesh-based text. These counts exclude external prefab graphs.

## Measured limits and baseline

The current prepared tutorial has 3,874 RGBA texture files totaling
989,522,484 bytes (943.68 MiB). Root Pack has 5,955 totaling 1,490,179,264 bytes
(1,421.15 MiB). Scene header texture counts match these file counts, and
Scene.LoadTextures retains a FileReadAll allocation for each. These file totals
are not a measured process peak, but the eager path already exceeds the entire
Xbox 360 512 MiB shared budget before other allocations. Disk deduplication or
optional PNG previews alone will not solve residency.

The Windows logic command was rerun against `beb3a87`: exit 0, logic PASS,
allocator peak 17,117,059 bytes. Its existing cases cover ease/inventory/velocity,
not the disputed semantics or gameplay. Tutorial and Root Pack boot commands
both exited 0/PASS despite 21 ERROR lines each, including failed constructors
and cloned Options UI Awake. Their approximately 21 MiB allocator peaks exclude
texture loading. Old health captures visibly expose packed character poses;
they establish changing HUD rectangles, not correct scene rendering.

Local audit artifacts are under `work/restart-20261031/`: the full Opus review,
Sol's independent findings, original-scene type inventory, texture inventory,
baseline logs, and exact adapter records. They are deliberately uncommitted.

## Runnable sequence

Continuation decision: read-only asset preflight found eight distinct Player
atlas pages sharing one texture name, which the current cache treats as one
identity. All 2,392 entries are tightly packed; 356 require packing transforms.
Opus therefore places a bounded sprite-correctness/residency checkpoint (A1)
after accepted CP1 and before the broader lifecycle work below. Its contract
must enforce allocation limits and preserve sprite placement; it is planned,
not implemented or accepted. See ASSET_PIPELINE.md for measured defects.

The project lead issues one bounded implementation brief at a time, followed
by Opus review and an independent Sol check:

1. Coroutine handles/waits/cancellation and strict fault diagnostics; remove
   the name filter without disguising missing Animator behavior.
2. Common lifecycle/player loop, followed by separate runtime-semantics fixes
   where required by actual paths. Avoid combining every CLR defect in one task.
3. Scene registry and asynchronous transition pump with persistent roots,
   original requests, teardown, new-instance and stale-coroutine assertions.
4. Required animation state/events, sprite correctness, bounded texture
   residency, and measured frame/heap/texture counters.
5. Opening dialogue, minimum UI/text, input and authored map geometry; run
   normal new game through the visible controllable map. Port-local storage
   initialization/load/save callbacks are already needed here: SlotSelectScreen
   calls PlayerData.Init, which initializes storage and loads/creates slots.
   Persistence cannot be postponed entirely until the victory checkpoint.
   The original GOG SavePath uses the installed game's AppData Cuphead directory
   on WindowsPlayer, which is also the platform currently reported by the shim.
   Install and test a port-local path/interface override **before enabling any
   original storage calls**; prove that the installed save directory is neither
   read nor written.
6. Enter Root Pack from the map; authored damage, invulnerability, death and
   original retry; original victory/results/map return and port save persistence.

Every significant shared-code checkpoint builds Windows and Xbox Release and
retains appropriate tutorial/platforming/prepared-boss regressions. Windows
execution, XEX compilation, Xenia execution and hardware results remain separate
evidence classes. Explicitly muted runs are permitted; absent audio remains a
gap. The opening milestone stays incomplete until the entire actual route and
its visible, interactive outcomes are recorded.
