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
3. Complete the required Elder Kettle interaction and retain the original
   loadout/tutorial/power-up behavior. New inventory already contains peashot;
   do not manufacture a weapon grant. The optional tutorial may be entered and
   exited.
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
  dialogue and its original messages. Recover the necessary original data.
- Port save/test data stays separate from the installed game's saves. Record
  what progression persists and test it; do not claim full save compatibility
  from a single in-memory map return.

## Asset pipeline

Content packaging, textures and audio follow [CONSOLE_PLAN.md](CONSOLE_PLAN.md)
section 2. Don't convert the whole game speculatively before this route
establishes the renderer's and audio's needs; CHOB object trees are the
development format until then.

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
| Regressions | Earlier routes and boss scenes keep booting and playing through the original flow |
| Platforms | Windows execution, both target builds, and separately identified Xenia/hardware execution |

Each test must fail when its required outcome is missing. A scene boot PASS
does not validate authored attacks, map navigation or the complete opening.
Do not mark revised M1 complete until the playable route and its evidence exist.
