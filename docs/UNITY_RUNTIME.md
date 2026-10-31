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

- `Component.gameObject`, `Component.transform`, `GetComponent<T>`
- `GameObject.transform`, `SetActive`, `activeSelf`, `name`
- `Transform.position`, `localPosition`, `localScale`, `parent`, `SetParent`,
  `localEulerAngles`/`eulerAngles` (rotation is stored but not yet applied by
  the renderer)
- `Behaviour.enabled`, `MonoBehaviour.useGUILayout` (no-op)
- `MonoBehaviour.StartCoroutine`/`StopCoroutine`/`Invoke` (stub)
- `Object.name`, equality/inequality
- `Vector2`/`Vector3` constructors, fields, common operators, conversion
- `AnimationHelper.Update` and `AbstractMonoBehaviour.Awake` execute from
  original IL
- `TransformExtensions.ResetLocalTransforms` executes from original IL and
  changes the scene transform

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
- `Transform` PPtrs from serialized component path IDs are not yet mapped;
  component and GameObject references are handled.
- `GetComponent<T>` uses the MemberRef return type; generic MethodSpec
  resolution is not implemented yet.
- Coroutines/scheduler, `Time`, colliders/physics, audio, `Animator` state
  parameters, UI/Canvas, and `Destroy` lifecycle.
- `SetActive` is stored in the managed object flag only; the renderer still
  uses the scene object's active flag.
