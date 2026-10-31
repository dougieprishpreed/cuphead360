# Reverse-engineering notes

All findings below come from the local GOG installation in `game/`. Paths are
relative to that directory unless stated otherwise.

## Build identification

| Item | Value |
|---|---|
| Product | Cuphead |
| GOG game id | `1963513391` |
| DLC GOG id | `2048763853` (The Delicious Last Course) |
| GOG build id | `55543280706943035` |
| Unity `bundleVersion` | `1.3.2` |
| Unity engine | `2017.4.9f1` (`Cuphead_Data/globalgamemanagers`) |
| Player executable | `Cuphead.exe`, file version `2017.4.9.34015` |
| Scripting backend | Mono (managed `Assembly-CSharp.dll`; no `GameAssembly.dll`) |
| Managed runtime | `Cuphead_Data/Mono/EmbedRuntime/mono.dll` |
| Default resolution | 1280x720 (PlayerSettings) |
| Scenes | 79 in `BuildSettings` |

The GOG installer contains both the base game and the DLC. The DLC is not a
separate executable: its scenes, scripts, bundles, and progression data are
part of the same build. `PlayerData` contains DLC weapons, charms, bosses,
maps, and save fields in the same assembly.

## Managed assemblies

`Cuphead_Data/Managed/`:

- `Assembly-CSharp.dll` — 5,600,768 bytes, 5,640 TypeDefs, 34,747
  MethodDefs, 37,524 FieldDefs, 34,266 methods with IL, 2,590,233 bytes of
  IL. No generic method definitions and no generic type definitions in game
  code; generics are confined to BCL/Unity/third-party assemblies.
- `Assembly-CSharp-firstpass.dll` — Steamworks/GOG Galaxy glue.
- `Rewired_Core.dll`, `Rewired_Windows_Lib.dll` — input.
- `GalaxyCSharp.dll`, `GalaxyCSharpGlue.dll`, `Galaxy64.dll` — GOG Galaxy.
- `UnityEngine*.dll` — Unity 2017.4 managed API surface.
- `mscorlib.dll`, `System.dll`, `System.Core.dll`, `System.Xml.dll`,
  `Mono.Security.dll`.

The game logic is not obfuscated. Type and method names are intact. ILSpy
11.1 decompiles all game types; six decompiler errors occur only in
`Rewired.Platforms.Switch` property getters and do not affect the game's used
paths.

## Scene and content layout

`BuildSettings` lists 79 scenes under `Assets/_CUPHEAD/Scenes/`:

- title, slot select, level select, shop, win, start
- worlds 1–4 maps, DLC map
- 5 run-and-gun platforming levels (6 scene files with two `2_2F` parts)
- base-game bosses: pirate, flower, train, veggies, frogs, bee, mouse,
  dragon, flying bird, flying mermaid, flying blimp, slime, baroness, robot,
  clown, Sally Stage Play, genie, devil, mausoleum, shmup tutorial
- King Dice board and all dice-palace sub-levels (domino, chips, cigar,
  booze, roulette, rabbit, flying horse, flying memory, eight ball, main)
- DLC: flying cowboy, airplane, rum runners, old man, snow cult, kitchen,
  saltbaker, chalice tutorial, graveyard, all six King of Games chess levels
- cutscenes: intro, king dice, devil, outro, credits, world 2/3, DLC intro,
  DLC pre-Saltbaker, DLC ending, DLC credits comic, DLC credits
- houses, DLC shop, load helper

`Cuphead_Data/level0..level78` are the serialized scene files. Scene object
counts range from 11 (boot scene) to over 2,000 (Saltbaker). Typical boss
scenes contain 200–600 GameObjects, 100–300 SpriteRenderers, 30–120
Animators, and dozens of colliders.

`globalgamemanagers` and `sharedassets*.assets` hold animation clips,
animator controllers, materials, shaders, fonts, and shared sprites. Asset
bundles in `StreamingAssets/AssetBundles/` hold 5.22 GB of sprite atlases,
music, sound effects, fonts, and DLC content.

### Asset bundle inventory (local scan)

| Object type | Count |
|---|---:|
| Sprite | 87,659 |
| Texture2D | 1,108 |
| AssetBundle | 420 |
| SpriteAtlas | 236 |
| AudioClip | 132 |
| Material | 66 |
| Shader | 60 |
| MonoScript | 30 |
| MonoBehaviour | 30 |
| Font | 29 |
| AssetBundleManifest | 1 |

The bundle files have no extension and start with `UnityFS`. They are Unity
2017.4 bundles. `atlas_*` bundles contain SpriteAtlas/Sprite/Texture2D data;
`music_*` bundles contain AudioClips. DLC assets are identifiable by `_dlc`
and `saltbaker`/`chalice`/`cowboy`/`oldman`/`snowcult`/`rumrunners`/
`graveyard`/`kog`/`chess` names.

## Save data

`PlayerData` is a `[Serializable]` class. Save slots use PlayerPrefs keys:

```
cuphead_player_data_v1_slot_0
cuphead_player_data_v1_slot_1
cuphead_player_data_v1_slot_2
```

Serialization is `JsonUtility.ToJson` / `JsonUtility.FromJson<PlayerData>`.
GOG Galaxy cloud storage is layered on top; the port must provide a local
PlayerPrefs/file backend and a stub online manager. Progression fields include
weapons, charms, supers, coins, level completion, difficulty unlocks, DLC
progress, chess zones, and dialoguer state.

## Input

The game uses Rewired. Gameplay code asks `PlayerInput`/`PlayerManager` for
axes and buttons; it does not call UnityEngine.Input directly in most gameplay
paths. This gives the port a clean seam: implement the Rewired-facing API used
by Cuphead on top of XInput, rather than porting Rewired's native backends.

## Rendering and animation

Cuphead is a 2D sprite game. The data uses:

- `SpriteRenderer` per visual element
- `Animator` + `AnimatorController` + `AnimationClip` for frame animation
- `SpriteAtlas`/`Sprite` for atlas pages
- 2D colliders (`BoxCollider2D`, `CircleCollider2D`, `PolygonCollider2D`,
  `EdgeCollider2D`) and `Rigidbody2D`
- Shaders/materials for palette, blending, and full-screen effects
- separate background/foreground layers and camera effects

Animation data is not in the StreamingAssets bundles; it is in the serialized
scene/shared asset files (6,043 AnimationClips and 1,677 AnimatorControllers
across the local data files). The asset pipeline must therefore read both
bundle sprites and serialized scene/animation data.

## Implications for the port

1. Game logic can be recovered exactly from IL; no behavioural guessing is
   required for gameplay systems.
2. The scripting backend is Mono, so there is no IL2CPP-generated C++ to
   reuse. A custom execution/translation layer is required.
3. Cuphead's logic uses no user-defined generics, which removes a large class
   of CLR runtime complexity. BCL generics (`List<T>`, `Dictionary<K,V>`) can
   be implemented natively.
4. The game is coroutine-heavy (~2,500 `StartCoroutine` sites). The runtime
   must support C# iterator state machines correctly from the start.
5. All serialized data and IL are little-endian; Xbox 360 is big-endian. Every
   loader and signature parser must perform explicit little-endian reads.
6. The DLC shares the same assembly and data layout as the base game. The
   architecture must not treat it as an optional add-on.
