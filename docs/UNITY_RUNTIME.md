# Managed Unity object model

`src/engine/unity_bind.cpp` binds a loaded `Scene` to managed
`UnityEngine.GameObject`, `Transform`, and `Component` objects.

## Object layout

- `GameObject`: type id, bindings side table; slot 0 = scene object index,
  slot 1 = managed Transform.
- `Transform`: slot 0 = scene object index, slot 1 = managed GameObject.
- `SpriteRenderer`/`Animator`/`Camera`: slot 0 = scene object index,
  slot 1 = scene component index.
- Script `MonoBehaviour` objects: allocated with the script type's normal
  field slots. A side table (`ManagedBinding`) maps the object pointer to its
  scene object/component, so `Component.gameObject` and
  `Component.transform` work without stealing script fields.

`BindScene()` creates all of these and is called by the `--unity` test and
(future) the real game boot.

## Native APIs currently implemented

- `Component.gameObject`, `Component.transform`, `GetComponent<T>`,
  `GetComponent(Type)`, `GetComponentsInChildren<T>`,
  `GetComponentInChildren<T>`, `Object.FindObjectsOfType<T>` (generic
  MethodSpec calls are resolved through the MemberRef recorded in metadata)
- `GameObject.transform`, `SetActive`, `activeSelf`, `name`, runtime
  `new GameObject(...)` with unbound Transform storage
- `Transform.position`, `localPosition`, `localScale`, `parent`, `SetParent`,
  `localEulerAngles`/`eulerAngles` (rotation is stored but not yet applied by
  the renderer)
- `Behaviour.enabled`, `MonoBehaviour.useGUILayout` (no-op)
- `MonoBehaviour.StartCoroutine`/`StopCoroutine`/`Invoke` (stub)
- `Object.name`, equality/inequality
- `Vector2`/`Vector3` constructors, fields, common operators, conversion;
  `Rect` component properties backed by 4 struct slots
- `Animator.speed`
- `BoxCollider2D.size/offset`, `CircleCollider2D.radius/offset`,
  `Rigidbody2D` mass/drag/gravity/velocity/body type (values are bound, but
  the physics solver is still native gameplay scaffolding)
- Delegates: construction, multicast `Delegate.Combine/Remove`, invocation;
  C# event add/remove methods execute from original IL
- BCL: `List<T>`, `Dictionary<K,V>` (Add, indexer, TryGetValue, ContainsKey,
  Remove, Clear, Count), `String` search/substring/case/trim,
  `Array.GetEnumerator`, `Enum.GetValues`, `Type.GetTypeFromHandle`,
  `RuntimeHelpers.InitializeArray` (FieldRVA data), `Interlocked.CompareExchange`
- `AnimationHelper.Update` and `AbstractMonoBehaviour.Awake` execute from
  original IL
- `TransformExtensions.ResetLocalTransforms` executes from original IL and
  changes the scene transform

## Player stack harness

`--stack` loads the tutorial scene with the extracted player prefab, sets
`Level.Current` and `PlayersCreated`, seeds `PlayerManager.players` and
`playerInputs`, then runs the original controller lifecycle.

Observed on Windows:

```
[INFO] [stack] get_motor ok=1 type=LevelPlayerMotor PASS
[INFO] [stack] player Awake PASS
[INFO] [stack] prefab component Awakes 11/11
[INFO] [stack] player LevelInit PASS
[INFO] [stack] player LevelStart PASS
[INFO] [stack] result=PASS
```

All eleven player-prefab component `Awake` methods and the original
`LevelPlayerController.LevelInit`/`LevelStart` now run. Reaching this
required:

- decoding MethodSpec generic calls (`GetComponent<T>`, `FindObjectsOfType<T>`);
- delegates/events, `Dictionary<K,V>`, `List<T>` and array enumeration;
- FieldRVA static-array initialization and enum constants;
- fixing the `[NonSerialized]` (field flag `0x80`) serialized-field schema,
  which had shifted every field after `preEnabled` by 4 bytes;
- offsetting prefab-internal GameObject/component PPtrs into the scene ID
  space.

The dialogue database is currently an explicit scaffold: `Resources.Load`
returns an empty `DialogueEditorMasterObjectWrapper` so save/player
initialization can proceed. The real asset exists at
`resources.assets` MonoBehaviour 1175 (87,612 bytes) and still needs its
nested graph extracted. The next gameplay step is running the original motor
and input loop.

## Verified

- Windows: bound 185 GameObjects / 276 components from the tutorial scene;
  original `TransformExtensions.ResetLocalTransforms` IL ran and reset a
  transform; `AnimationHelper.Update` and `AbstractMonoBehaviour.Awake`
  PASS.
- Xbox 360 (Xenia, big-endian): same binding and script calls PASS.

## Serialized fields

`tools/extract_scene.py` reads the MonoBehaviour header, resolves the script
type, and walks the script's serialized fields in Unity order. It supports
primitive/enum values, Unity strings, GameObject/Transform/component PPtrs,
primitive/PPtr arrays, and nested `[Serializable]` classes.

Values are encoded into the scene package. Script PPtrs are resolved in a
second bind pass, after every scene component has a managed object, so
forward references work. Verified examples:

- `AnimationHelper`: serialized `speed=1`, `layer=1`;
- `DeferredAudioSource.audioClipName = "MUS_Tutorial"`;
- `CupheadGlyph.rectTransformTexts` array length 6;
- `PlayerDeathEffect.cuphead` PPtr resolves to a SpriteRenderer;
- `TutorialLevel.intro` nested object with child value 1.

Original lifecycle methods executed on real scene scripts:

- `LevelPlatform.Awake`
- `PlayerDeathEffect.Start`
- `AnimationHelper.Update`

All pass on Windows and on the Xbox 360 XEX under Xenia.

## Missing

- Unity serializes some fields with version-specific layouts
  (`AnimationCurve`, `LayerMask`, nested arrays, `List<T>` of serializable
  classes). Those are rejected safely at the first unsupported field.
- Fields inherited from complex base classes can stop parsing early; the
  recovered prefix is still usable.
- `Resources.Load`/`AssetBundle` content, save files, dialogue data, audio,
  coroutine scheduling, physics simulation, UI/Canvas, and the Animator state
  machine are not implemented yet.
- Runtime-created GameObjects/Transforms have native storage but are not yet
  registered as scene objects, so they are not rendered or collided with.
- `SetActive` is stored in the managed object flag only; the renderer still
  uses the scene object's active flag.
