# M1: playable opening flow

The user revised M1 on 2026-10-31. The playable milestone is now the normal
opening flow through Inkwell Isle I and the Root Pack. Tutorial combat probes
remain runtime tests; they do not demonstrate this milestone.

## Why the tutorial is a separate checkpoint

The extracted tutorial does not author an enemy or hazard that supplies lethal
damage. The development combat probe constructs an enemy DamageDealer and
calls the original damage methods. This is useful for validating the engine,
but it is synthetic stimulus, not ordinary tutorial gameplay.

The original player damage, invulnerability and death methods must retain their
semantics. Do not add lethal tutorial content or change those methods to make a
test pass. A fresh scene manually launched by a harness is not evidence that
the original Retry request successfully reloaded the scene.

## Playable route

1. Enter the normal title/new-game selection and create a fresh port save slot.
2. Follow the opening into Elder Kettle's house. An original, supported intro
   skip is acceptable; directly selecting the house in a development harness
   does not validate this transition.
3. Complete the required Elder Kettle interaction and receive the original
   initial weapon/loadout. The optional tutorial may be entered and exited.
4. Leave the house for a visible, controllable Inkwell Isle I map. Walk to the
   Root Pack entrance and use its original interaction/difficulty selection.
5. Play the Root Pack encounter with authored boss behavior. Demonstrate player
   shooting, enemy damage, player damage, invulnerability, hit/death state,
   game over, and Retry.
6. Defeat the encounter through its original phase/win logic, complete its
   result flow, and return to the map with completion/progression retained.

Root Pack is the selected acceptance encounter, not a claim that the original
game forces every player to choose that boss first.

Relevant original scene identifiers are `scene_title`, `scene_slot_select`,
`scene_cutscene_intro`, `scene_level_house_elder_kettle`, `scene_level_tutorial`,
`scene_map_world_1`, `scene_level_veggies`, and `scene_win`. Resolve their real
transitions from original code and content rather than hardcoding this list as
a scene carousel.

## Runtime and presentation contract

- Game rules, phase changes, weapon grants, progression, and retry originate in
  the original IL. Native code provides the engine/platform services it calls.
- Scene loading must consume original requests, tear down the old scene, bind
  the next scene and run its lifecycle. Persistent player data survives the
  appropriate transitions; stale scene components and coroutine owners do not.
- Retry must be requested by the original single-player game-over flow and
  produce a freshly initialized encounter. A coop revive, direct health write,
  or an unconditional test-harness recursion is not sufficient.
- Keyboard/controller input feeds the original action API. Scripted input is
  useful for repeatable tests, but an interactive run must also work.
- The player, world map, encounter and health HUD must render. HUD health comes
  from PlayerStatsManager.Health. A log line alone does not validate rendering.
- Audio playback and any muted run must be described precisely. An explicitly
  muted milestone run is permitted under the original task; absent playback is
  still an audio implementation gap.
- The current empty dialogue scaffold cannot stand in for the required opening
  dialogue/weapon-grant interaction. Recover the necessary original data.
- Port save/test data stays separate from the installed game's saves. Record
  what progression persists and test it; do not claim full save compatibility
  from a single in-memory map return.

## Asset pipeline decision

The existing custom metadata/scene packages and per-sprite CHTX RGBA files are
development intermediates. Prior architecture documents planned atlasing,
streaming and Xbox texture layout support; a production Xbox asset format and
complete conversion pipeline have not been implemented.

Proceed incrementally:

1. Make sprite PNG previews optional. Keep one canonical current extraction
   per scene and preserve compact verification captures separately.
2. Deduplicate identical generated assets using immutable content identities
   and a manifest shared across scenes. Regeneration must not mutate another
   scene's assets through writable hardlinks.
3. Measure texture residency, scene-switch lifetime, runtime heap use and peak
   memory. Current Scene::LoadTextures eagerly loads every raw texture; this
   must not be confused with the planned streaming architecture.
4. Introduce bounded texture residency and suitable atlas/pack organization.
   Keep sprite rects, pivots, transparency and animation references correct.
5. Add target-specific texture/audio representations with renderer/audio paths
   that can consume them. The current software renderer reads linear RGBA;
   changing files to a GPU-native layout alone will not make it work.
6. Validate visual output and memory on Windows and Xenia. Record actual Xbox
   hardware results separately when available. The console has 512 MB shared
   memory; disk-size reduction alone does not demonstrate a viable memory
   working set.

Do not convert the entire game speculatively before this small playable route
establishes the required asset/runtime interfaces.

## Evidence and completion gate

Record reproducible commands, exit codes, logs and framebuffer screenshots in
VERIFICATION.md. At minimum cover:

| Check | Required evidence |
| --- | --- |
| New-game entry | Original transition requests and visible selection/opening |
| Elder Kettle | Required interaction, initial weapon grant, house exit |
| World map | Visible player movement and Root Pack entrance interaction |
| Combat | Authored attack/projectile collisions and enemy health/phase changes |
| Player damage | Health 3 -> 2 -> 1 -> 0 through original methods |
| Invulnerability | A fresh damage source blocked during the interval, then accepted after expiry |
| Death/retry | Original game-over Retry request, fresh encounter, health reset and subsequent damage |
| HUD | Captures tied to health changes, death and retry |
| Victory | Original win/results flow and return to map with progression retained |
| Input/audio | Interactive input evidence and explicit playback/mute status |
| Regressions | Tutorial/platforming and prepared boss boot/play harnesses |
| Platforms | Windows execution, both target builds, and separately identified Xenia/hardware execution |

Each test must fail when its required outcome is missing. A scene boot PASS
does not validate authored attacks, map navigation or the complete opening.
Do not mark revised M1 complete until the playable route and its evidence exist.

## Generated-file cleanup checkpoint

On 2026-10-31, the user approved removal of 22 obsolete extraction snapshots
and redundant sprite PNG sidecars with matching runtime RGBA files. Cleanup
reclaimed 4,106,863,495 bytes. Forty-one root screenshot/preview files were
copied and checksum-verified under `work/cleanup-evidence-20261031` first.
The local audit is in `work/cleanup-manifest.json` and `work/cleanup-result.json`.
The installed game, active regression scenes, metadata, current combat evidence,
source and repository history were preserved. Measured project size afterward
was about 15.81 GB (decimal); most remaining space is game data and generated
raw sprite content.
