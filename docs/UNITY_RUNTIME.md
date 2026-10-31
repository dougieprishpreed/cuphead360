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

## Missing

- MonoBehaviour serialized-field parsing/initialization. Script objects
  currently start with zeroed fields, so scripts requiring scene references
  are not safe to run yet.
- `GetComponent<T>` uses the MemberRef return type; generic MethodSpec
  resolution is not implemented yet.
- Coroutines/scheduler, `Time`, colliders/physics, audio, `Animator` state
  parameters, UI/Canvas, and `Destroy` lifecycle.
- `SetActive` is stored in the managed object flag only; the renderer still
  uses the scene object's active flag.
