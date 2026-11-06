using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Engine methods of UnityEngine.Object, GameObject, Component, Transform,
    // Behaviour, MonoBehaviour and Time. Names are the patcher's mangled ids.
    public static unsafe partial class Icalls
    {
        // ------------------------------------------------------------ Object

        public static int UnityEngine_Object__GetOffsetOfInstanceIDInCPlusPlusObject() => Objects.InstanceIdOffset;
        public static void UnityEngine_Object__EnsureRunningOnMainThread(UObject self) { }
        public static string UnityEngine_Object__get_name(UObject self) => Objects.Of(self)?.Name ?? "";
        public static void UnityEngine_Object__set_name(UObject self, string value)
        {
            var o = Objects.Of(self);
            if (o == null) return;
            o.Name = value ?? "";
            // A GameObject and its components share one name.
            var go = o as NGameObject ?? (o as NComponent)?.GameObject;
            if (go != null) { go.Name = o.Name; foreach (var c in go.Components) c.Name = o.Name; }
        }
        public static HideFlags UnityEngine_Object__get_hideFlags(UObject self) => Objects.Of(self)?.Flags ?? 0;
        public static void UnityEngine_Object__set_hideFlags(UObject self, HideFlags value) { var o = Objects.Of(self); if (o != null) o.Flags = value; }
        public static string UnityEngine_Object__ToString(UObject self)
        {
            var o = Objects.Of(self);
            return o == null ? "null" : o.Name + " (" + self.GetType().FullName + ")";
        }
        public static bool UnityEngine_Object__DoesObjectWithInstanceIDExist(int instanceID) => Objects.ByInstanceId(instanceID) != null;

        static readonly string traceDestroy = Environment.GetEnvironmentVariable("CH360_TRACE_RELEASE");

        public static void UnityEngine_Object__Destroy(UObject obj, float t)
        {
            var o = Objects.Of(obj);
            if (o == null) return;
            if (o is NComponent c && !(o is NTransform) && t <= 0f && c is NBehaviour b0) { /* deferred like everything else */ }
            if (o is NTransform) { Log.Line("Destroying a Transform is not allowed: " + o.Name); return; }
            if (traceDestroy != null && o.Name != null && o.Name.Contains(traceDestroy))
                Log.Line($"TRACE Destroy({o.Name}, {t}) frame {Clock.FrameCount}\n{Environment.StackTrace}");
            PlayerLoop.QueueDestroy(o, t);
        }
        public static void UnityEngine_Object__DestroyObject(UObject obj, float t) => UnityEngine_Object__Destroy(obj, t);
        public static void UnityEngine_Object__DestroyImmediate(UObject obj, bool allowDestroyingAssets) => Lifecycle.DestroyNow(Objects.Of(obj));
        public static void UnityEngine_Object__DontDestroyOnLoad(UObject target)
        {
            var o = Objects.Of(target);
            var go = o as NGameObject ?? (o as NComponent)?.GameObject;
            if (go != null) Scenes.MarkDontDestroy(go);
        }

        public static UObject[] UnityEngine_Object__FindObjectsOfType(Type type) => Find.ObjectsOfType(type, false);
        public static UObject[] UnityEngine_Object__FindSceneObjectsOfType(Type type) => Find.ObjectsOfType(type, false);
        public static UObject[] UnityEngine_Object__FindObjectsOfTypeIncludingAssets(Type type) => Find.ObjectsOfType(type, true);

        public static UObject UnityEngine_Object__Internal_CloneSingle(UObject data) => Clone.Instantiate(data, null, false, null, null);
        public static UObject UnityEngine_Object__Internal_CloneSingleWithParent(UObject data, Transform parent, bool worldPositionStays)
            => Clone.Instantiate(data, Objects.Tr(parent), worldPositionStays, null, null);
        public static UObject UnityEngine_Object__INTERNAL_CALL_Internal_InstantiateSingle(UObject data, ref Vector3 pos, ref Quaternion rot)
            => Clone.Instantiate(data, null, false, pos, rot);
        public static UObject UnityEngine_Object__INTERNAL_CALL_Internal_InstantiateSingleWithParent(UObject data, Transform parent, ref Vector3 pos, ref Quaternion rot)
            => Clone.Instantiate(data, Objects.Tr(parent), false, pos, rot);

        // ------------------------------------------------------------ GameObject

        public static void UnityEngine_GameObject__Internal_CreateGameObject(GameObject mono, string name)
        {
            var go = Lifecycle.CreateGameObject(mono, name ?? "New Game Object");
            Scenes.AddRoot(go);
        }
        public static Transform UnityEngine_GameObject__get_transform(GameObject self) => (Transform)Objects.GO(self)?.Transform.Managed;
        public static int UnityEngine_GameObject__get_layer(GameObject self) => Objects.GO(self)?.Layer ?? 0;
        public static void UnityEngine_GameObject__set_layer(GameObject self, int value) { var g = Objects.GO(self); if (g != null) g.Layer = value; }
        public static bool UnityEngine_GameObject__get_active(GameObject self) => Objects.GO(self)?.ActiveInHierarchy ?? false;
        public static void UnityEngine_GameObject__set_active(GameObject self, bool value) => UnityEngine_GameObject__SetActive(self, value);
        public static void UnityEngine_GameObject__SetActive(GameObject self, bool value) { var g = Objects.GO(self); if (g != null) Lifecycle.SetActive(g, value); }
        public static bool UnityEngine_GameObject__get_activeSelf(GameObject self) => Objects.GO(self)?.ActiveSelf ?? false;
        public static bool UnityEngine_GameObject__get_activeInHierarchy(GameObject self) => Objects.GO(self)?.ActiveInHierarchy ?? false;
        public static void UnityEngine_GameObject__SetActiveRecursively(GameObject self, bool state)
        {
            var g = Objects.GO(self);
            if (g == null) return;
            void Walk(NTransform t) { Lifecycle.SetActive(t.GameObject, state); foreach (var c in t.Children.ToArray()) Walk(c); }
            Walk(g.Transform);
        }
        public static string UnityEngine_GameObject__get_tag(GameObject self) => Objects.GO(self)?.Tag ?? "Untagged";
        public static void UnityEngine_GameObject__set_tag(GameObject self, string value) { var g = Objects.GO(self); if (g != null) g.Tag = value ?? "Untagged"; }
        public static bool UnityEngine_GameObject__CompareTag(GameObject self, string tag) => Objects.GO(self)?.Tag == tag;
        public static GameObject UnityEngine_GameObject__FindGameObjectWithTag(string tag) => Find.WithTag(tag, true) is var a && a.Length > 0 ? a[0] : null;
        public static GameObject[] UnityEngine_GameObject__FindGameObjectsWithTag(string tag) => Find.WithTag(tag, false);
        public static GameObject UnityEngine_GameObject__Find(string name) => Find.ByPath(name);
        public static void UnityEngine_GameObject__INTERNAL_get_scene(GameObject self, out UnityEngine.SceneManagement.Scene value)
        {
            value = default;
            var g = Objects.GO(self);
            value.m_Handle = g == null ? 0 : g.Scene;
        }
        public static Component UnityEngine_GameObject__Internal_AddComponentWithType(GameObject self, Type componentType)
        {
            var g = Objects.GO(self);
            return g == null ? null : (Component)Lifecycle.AddComponent(g, componentType)?.Managed;
        }
        public static Component UnityEngine_GameObject__AddComponentInternal(GameObject self, string className)
            => UnityEngine_GameObject__Internal_AddComponentWithType(self, Find.TypeByName(className));

        public static Component UnityEngine_GameObject__GetComponent(GameObject self, Type type) => Find.Component(Objects.GO(self), type);
        public static void UnityEngine_GameObject__GetComponentFastPath(GameObject self, Type type, IntPtr oneFurtherThanResultValue)
            => WriteResult(oneFurtherThanResultValue, Find.Component(Objects.GO(self), type));
        public static Component UnityEngine_GameObject__GetComponentByName(GameObject self, string type)
            => Find.Component(Objects.GO(self), Find.TypeByName(type));
        public static Component UnityEngine_GameObject__GetComponentInChildren(GameObject self, Type type, bool includeInactive)
            => Find.InChildren(Objects.GO(self), type, includeInactive);
        public static Component UnityEngine_GameObject__GetComponentInParent(GameObject self, Type type)
            => Find.InParent(Objects.GO(self), type);
        public static Array UnityEngine_GameObject__GetComponentsInternal(GameObject self, Type type, bool useSearchTypeAsArrayReturnType,
            bool recursive, bool includeInactive, bool reverse, object resultList)
            => Find.Components(Objects.GO(self), type, useSearchTypeAsArrayReturnType, recursive, includeInactive, reverse, resultList);

        public static void UnityEngine_GameObject__SendMessage(GameObject self, string methodName, object value, SendMessageOptions options)
            => Messages.Send(Objects.GO(self), methodName, value, options, 0);
        public static void UnityEngine_GameObject__SendMessageUpwards(GameObject self, string methodName, object value, SendMessageOptions options)
            => Messages.Send(Objects.GO(self), methodName, value, options, 1);
        public static void UnityEngine_GameObject__BroadcastMessage(GameObject self, string methodName, object parameter, SendMessageOptions options)
            => Messages.Send(Objects.GO(self), methodName, parameter, options, 2);

        /// <summary>
        /// Unity's GetComponent&lt;T&gt; fast path: the result is written into the
        /// caller's CastHelper&lt;T&gt; one pointer before the address it passes.
        /// </summary>
        static void WriteResult(IntPtr oneFurther, Component c)
        {
            Unsafe.AsRef<object>((void*)(oneFurther - IntPtr.Size)) = c;
        }

        // ------------------------------------------------------------ Component

        public static Transform UnityEngine_Component__get_transform(Component self) => (Transform)Objects.Comp(self)?.GameObject.Transform.Managed;
        public static GameObject UnityEngine_Component__get_gameObject(Component self) => (GameObject)Objects.Comp(self)?.GameObject.Managed;
        public static void UnityEngine_Component__GetComponentFastPath(Component self, Type type, IntPtr oneFurtherThanResultValue)
            => WriteResult(oneFurtherThanResultValue, Find.Component(Objects.Comp(self)?.GameObject, type));
        public static Component UnityEngine_Component__GetComponent(Component self, string type)
            => Find.Component(Objects.Comp(self)?.GameObject, Find.TypeByName(type));
        public static void UnityEngine_Component__GetComponentsForListInternal(Component self, Type searchType, object resultList)
            => Find.Components(Objects.Comp(self)?.GameObject, searchType, false, false, true, false, resultList);
        public static void UnityEngine_Component__SendMessage(Component self, string methodName, object value, SendMessageOptions options)
            => Messages.Send(Objects.Comp(self)?.GameObject, methodName, value, options, 0);
        public static void UnityEngine_Component__SendMessageUpwards(Component self, string methodName, object value, SendMessageOptions options)
            => Messages.Send(Objects.Comp(self)?.GameObject, methodName, value, options, 1);
        public static void UnityEngine_Component__BroadcastMessage(Component self, string methodName, object parameter, SendMessageOptions options)
            => Messages.Send(Objects.Comp(self)?.GameObject, methodName, parameter, options, 2);

        // ------------------------------------------------------------ Behaviour / MonoBehaviour

        public static bool UnityEngine_Behaviour__get_enabled(Behaviour self) => Objects.Comp(self)?.Enabled ?? false;
        public static void UnityEngine_Behaviour__set_enabled(Behaviour self, bool value)
        {
            var c = Objects.Comp(self);
            if (c is NBehaviour b) Behaviours.SetEnabled(b, value);
            else if (c != null && c.Enabled != value) { c.Enabled = value; Builtins.OnToggled(c, value && c.GameObject.ActiveInHierarchy); }
        }
        public static bool UnityEngine_Behaviour__get_isActiveAndEnabled(Behaviour self)
        {
            var c = Objects.Comp(self);
            if (c is NBehaviour b) return b.Awoken && Behaviours.ActiveAndEnabled(b);
            return c != null && c.Enabled && c.GameObject.ActiveInHierarchy;
        }

        public static Coroutine UnityEngine_MonoBehaviour__StartCoroutine_Auto_Internal(MonoBehaviour self, IEnumerator routine)
        {
            var b = Objects.Comp(self) as NBehaviour;
            if (b == null) return null;
            if (!b.GameObject.ActiveInHierarchy)
            {
                Log.Line("Coroutine couldn't be started because the game object '" + b.Name + "' is inactive!");
                return null;
            }
            return Coroutines.Start(b, routine);
        }
        public static Coroutine UnityEngine_MonoBehaviour__StartCoroutine(MonoBehaviour self, string methodName, object value)
        {
            var b = Objects.Comp(self) as NBehaviour;
            if (b == null) return null;
            var m = Behaviours.FindByName(b, methodName, value == null ? 0 : 1) ?? Behaviours.FindByName(b, methodName, 1);
            if (m == null) return null;
            var r = Behaviours.Send(b, m, value) as IEnumerator;
            if (r == null) return null;
            var h = UnityEngine_MonoBehaviour__StartCoroutine_Auto_Internal(self, r);
            var st = Coroutines.Of(h);
            if (st != null) st.Name = methodName;
            return h;
        }
        public static void UnityEngine_MonoBehaviour__StopCoroutine(MonoBehaviour self, string methodName)
        {
            if (Objects.Comp(self) is NBehaviour b)
                foreach (var c in b.Coroutines) if (c.Name == methodName) Coroutines.Stop(c);
        }
        public static void UnityEngine_MonoBehaviour__StopCoroutineViaEnumerator_Auto(MonoBehaviour self, IEnumerator routine)
        {
            if (Objects.Comp(self) is NBehaviour b)
                foreach (var c in b.Coroutines) if (ReferenceEquals(c.Routine, routine)) Coroutines.Stop(c);
        }
        public static void UnityEngine_MonoBehaviour__StopCoroutine_Auto(MonoBehaviour self, Coroutine routine)
        {
            var c = Coroutines.Of(routine);
            if (c != null && c.Owner == Objects.Comp(self)) Coroutines.Stop(c);
        }
        public static void UnityEngine_MonoBehaviour__StopAllCoroutines(MonoBehaviour self)
        {
            if (Objects.Comp(self) is NBehaviour b) Coroutines.StopAll(b);
        }
        public static void UnityEngine_MonoBehaviour__Invoke(MonoBehaviour self, string methodName, float time)
        {
            if (Objects.Comp(self) is NBehaviour b) Behaviours.Invoke(b, methodName, time, -1f);
        }
        public static void UnityEngine_MonoBehaviour__InvokeRepeating(MonoBehaviour self, string methodName, float time, float repeatRate)
        {
            if (Objects.Comp(self) is NBehaviour b) Behaviours.Invoke(b, methodName, time, repeatRate);
        }
        public static void UnityEngine_MonoBehaviour__CancelInvoke(MonoBehaviour self, string methodName)
        {
            if (Objects.Comp(self) is NBehaviour b) foreach (var i in b.Invokes) if (i.Method == methodName) i.Cancelled = true;
        }
        public static void UnityEngine_MonoBehaviour__Internal_CancelInvokeAll(MonoBehaviour self)
        {
            if (Objects.Comp(self) is NBehaviour b) foreach (var i in b.Invokes) i.Cancelled = true;
        }
        public static bool UnityEngine_MonoBehaviour__IsInvoking(MonoBehaviour self, string methodName)
            => Objects.Comp(self) is NBehaviour b && b.Invokes.Exists(i => !i.Cancelled && i.Method == methodName);
        public static bool UnityEngine_MonoBehaviour__Internal_IsInvokingAll(MonoBehaviour self)
            => Objects.Comp(self) is NBehaviour b && b.Invokes.Exists(i => !i.Cancelled);
        public static string UnityEngine_MonoBehaviour__GetScriptClassName(MonoBehaviour self) => self.GetType().Name;
        public static bool UnityEngine_MonoBehaviour__get_useGUILayout(MonoBehaviour self) => true;
        public static void UnityEngine_MonoBehaviour__set_useGUILayout(MonoBehaviour self, bool value) { }
        public static void UnityEngine_Coroutine__ReleaseCoroutine(Coroutine self) { }

        // ------------------------------------------------------------ Transform

        public static void UnityEngine_Transform__INTERNAL_get_position(Transform self, out Vector3 value) => value = Objects.Tr(self)?.Position ?? default;
        public static void UnityEngine_Transform__INTERNAL_set_position(Transform self, ref Vector3 value)
        {
            var t = Objects.Tr(self);
            if (t == null) return;
            if (t.Parent == null) t.LocalPosition = value;
            else { MathX.Invert(t.Parent.LocalToWorld, out var inv); t.LocalPosition = inv.MultiplyPoint3x4(value); }
            t.Invalidate();
        }
        public static void UnityEngine_Transform__INTERNAL_get_localPosition(Transform self, out Vector3 value) => value = Objects.Tr(self)?.LocalPosition ?? default;
        public static void UnityEngine_Transform__INTERNAL_set_localPosition(Transform self, ref Vector3 value)
        {
            var t = Objects.Tr(self); if (t == null) return;
            t.LocalPosition = value; t.Invalidate();
        }
        public static void UnityEngine_Transform__INTERNAL_get_rotation(Transform self, out Quaternion value) => value = Objects.Tr(self)?.Rotation ?? Quaternion.identity;
        public static void UnityEngine_Transform__INTERNAL_set_rotation(Transform self, ref Quaternion value)
        {
            var t = Objects.Tr(self); if (t == null) return;
            t.LocalRotation = t.Parent == null ? value : MathX.Inverse(t.Parent.Rotation) * value;
            t.Invalidate();
        }
        public static void UnityEngine_Transform__INTERNAL_get_localRotation(Transform self, out Quaternion value) => value = Objects.Tr(self)?.LocalRotation ?? Quaternion.identity;
        public static void UnityEngine_Transform__INTERNAL_set_localRotation(Transform self, ref Quaternion value)
        {
            var t = Objects.Tr(self); if (t == null) return;
            t.LocalRotation = value; t.Invalidate();
        }
        public static void UnityEngine_Transform__INTERNAL_get_localScale(Transform self, out Vector3 value) => value = Objects.Tr(self)?.LocalScale ?? Vector3.one;
        public static void UnityEngine_Transform__INTERNAL_set_localScale(Transform self, ref Vector3 value)
        {
            var t = Objects.Tr(self); if (t == null) return;
            t.LocalScale = value; t.Invalidate();
        }
        public static void UnityEngine_Transform__INTERNAL_get_lossyScale(Transform self, out Vector3 value) => value = Objects.Tr(self)?.LossyScale ?? Vector3.one;
        public static void UnityEngine_Transform__INTERNAL_CALL_GetLocalEulerAngles(Transform self, RotationOrder order, out Vector3 value)
            => value = MathX.ToEulerRad(Objects.Tr(self)?.LocalRotation ?? Quaternion.identity) * Mathf.Rad2Deg;
        public static void UnityEngine_Transform__INTERNAL_CALL_SetLocalEulerAngles(Transform self, ref Vector3 euler, RotationOrder order)
        {
            var t = Objects.Tr(self); if (t == null) return;
            t.LocalRotation = MathX.FromEulerRad(euler * Mathf.Deg2Rad); t.Invalidate();
        }
        public static Transform UnityEngine_Transform__get_parentInternal(Transform self) => (Transform)Objects.Tr(self)?.Parent?.Managed;
        public static void UnityEngine_Transform__set_parentInternal(Transform self, Transform value)
        {
            var t = Objects.Tr(self); if (t == null) return;
            Lifecycle.SetParent(t, Objects.Tr(value), true);
        }
        public static void UnityEngine_Transform__SetParent(Transform self, Transform parent, bool worldPositionStays)
        {
            var t = Objects.Tr(self); if (t == null) return;
            Lifecycle.SetParent(t, Objects.Tr(parent), worldPositionStays);
        }
        public static void UnityEngine_Transform__INTERNAL_get_worldToLocalMatrix(Transform self, out Matrix4x4 value)
        {
            MathX.Invert(Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity, out value);
        }
        public static void UnityEngine_Transform__INTERNAL_get_localToWorldMatrix(Transform self, out Matrix4x4 value) => value = Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity;
        public static void UnityEngine_Transform__INTERNAL_CALL_SetPositionAndRotation(Transform self, ref Vector3 position, ref Quaternion rotation)
        {
            UnityEngine_Transform__INTERNAL_set_rotation(self, ref rotation);
            UnityEngine_Transform__INTERNAL_set_position(self, ref position);
        }
        public static void UnityEngine_Transform__INTERNAL_CALL_TransformPoint(Transform self, ref Vector3 position, out Vector3 value)
            => value = (Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity).MultiplyPoint3x4(position);
        public static void UnityEngine_Transform__INTERNAL_CALL_InverseTransformPoint(Transform self, ref Vector3 position, out Vector3 value)
        {
            MathX.Invert(Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity, out var inv);
            value = inv.MultiplyPoint3x4(position);
        }
        public static void UnityEngine_Transform__INTERNAL_CALL_TransformDirection(Transform self, ref Vector3 direction, out Vector3 value)
            => value = (Objects.Tr(self)?.Rotation ?? Quaternion.identity) * direction;
        public static void UnityEngine_Transform__INTERNAL_CALL_InverseTransformDirection(Transform self, ref Vector3 direction, out Vector3 value)
            => value = MathX.Inverse(Objects.Tr(self)?.Rotation ?? Quaternion.identity) * direction;
        public static void UnityEngine_Transform__INTERNAL_CALL_TransformVector(Transform self, ref Vector3 vector, out Vector3 value)
            => value = (Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity).MultiplyVector(vector);
        public static void UnityEngine_Transform__INTERNAL_CALL_InverseTransformVector(Transform self, ref Vector3 vector, out Vector3 value)
        {
            MathX.Invert(Objects.Tr(self)?.LocalToWorld ?? Matrix4x4.identity, out var inv);
            value = inv.MultiplyVector(vector);
        }
        public static void UnityEngine_Transform__INTERNAL_CALL_RotateAroundInternal(Transform self, ref Vector3 axis, float angle)
        {
            var t = Objects.Tr(self); if (t == null) return;
            var q = MathX.AngleAxisRad(angle, axis) * t.Rotation;
            UnityEngine_Transform__INTERNAL_set_rotation(self, ref q);
        }
        public static void UnityEngine_Transform__INTERNAL_CALL_LookAt(Transform self, ref Vector3 worldPosition, ref Vector3 worldUp)
        {
            var t = Objects.Tr(self); if (t == null) return;
            var q = Quaternion.LookRotation(worldPosition - t.Position, worldUp);
            UnityEngine_Transform__INTERNAL_set_rotation(self, ref q);
        }
        public static Transform UnityEngine_Transform__get_root(Transform self)
        {
            var t = Objects.Tr(self); if (t == null) return null;
            while (t.Parent != null) t = t.Parent;
            return (Transform)t.Managed;
        }
        public static int UnityEngine_Transform__get_childCount(Transform self) => Objects.Tr(self)?.Children.Count ?? 0;
        public static int UnityEngine_Transform__GetChildCount(Transform self) => Objects.Tr(self)?.Children.Count ?? 0;
        public static Transform UnityEngine_Transform__GetChild(Transform self, int index)
        {
            var t = Objects.Tr(self);
            if (t == null || index < 0 || index >= t.Children.Count) throw new UnityException("Transform child out of bounds");
            return (Transform)t.Children[index].Managed;
        }
        public static void UnityEngine_Transform__DetachChildren(Transform self)
        {
            var t = Objects.Tr(self); if (t == null) return;
            foreach (var c in t.Children.ToArray()) Lifecycle.SetParent(c, null, true);
        }
        public static void UnityEngine_Transform__SetAsFirstSibling(Transform self) => Sibling(self, 0);
        public static void UnityEngine_Transform__SetAsLastSibling(Transform self) => Sibling(self, int.MaxValue);
        public static void UnityEngine_Transform__SetSiblingIndex(Transform self, int index) => Sibling(self, index);
        static void Sibling(Transform self, int index)
        {
            var t = Objects.Tr(self);
            if (t?.Parent == null) return;
            var l = t.Parent.Children;
            l.Remove(t);
            l.Insert(Math.Max(0, Math.Min(index, l.Count)), t);
        }
        public static int UnityEngine_Transform__GetSiblingIndex(Transform self)
        {
            var t = Objects.Tr(self);
            return t?.Parent == null ? 0 : t.Parent.Children.IndexOf(t);
        }
        public static Transform UnityEngine_Transform__Find(Transform self, string name) => Find.ChildByPath(Objects.Tr(self), name);
        public static bool UnityEngine_Transform__IsChildOf(Transform self, Transform parent)
        {
            var p = Objects.Tr(parent);
            for (var t = Objects.Tr(self); t != null; t = t.Parent) if (t == p) return true;
            return false;
        }
        public static bool UnityEngine_Transform__get_hasChanged(Transform self) => Objects.Tr(self)?.HasChanged ?? false;
        public static void UnityEngine_Transform__set_hasChanged(Transform self, bool value) { var t = Objects.Tr(self); if (t != null) t.HasChanged = value; }

        // ------------------------------------------------------------ Time

        public static float UnityEngine_Time__get_time() => Clock.InFixedTimeStep ? Clock.FixedTime : Clock.Time;
        public static float UnityEngine_Time__get_timeSinceLevelLoad() => Clock.Time - Clock.TimeSinceLevelLoadBase;
        public static float UnityEngine_Time__get_deltaTime() => Clock.InFixedTimeStep ? Clock.FixedDeltaTime * 1f : Clock.DeltaTime;
        public static float UnityEngine_Time__get_fixedTime() => Clock.FixedTime;
        public static float UnityEngine_Time__get_unscaledTime() => Clock.UnscaledTime;
        public static float UnityEngine_Time__get_fixedUnscaledTime() => Clock.FixedUnscaledTime;
        public static float UnityEngine_Time__get_unscaledDeltaTime() => Clock.UnscaledDeltaTime;
        public static float UnityEngine_Time__get_fixedUnscaledDeltaTime() => Clock.FixedDeltaTime;
        public static float UnityEngine_Time__get_fixedDeltaTime() => Clock.FixedDeltaTime;
        public static void UnityEngine_Time__set_fixedDeltaTime(float value) => Clock.FixedDeltaTime = value;
        public static float UnityEngine_Time__get_maximumDeltaTime() => Clock.MaximumDeltaTime;
        public static void UnityEngine_Time__set_maximumDeltaTime(float value) => Clock.MaximumDeltaTime = value;
        public static float UnityEngine_Time__get_smoothDeltaTime() => Clock.DeltaTime;
        public static float UnityEngine_Time__get_timeScale() => Clock.TimeScale;
        public static void UnityEngine_Time__set_timeScale(float value) => Clock.TimeScale = Math.Max(0f, value);
        public static int UnityEngine_Time__get_frameCount() => Clock.FrameCount;
        public static int UnityEngine_Time__get_renderedFrameCount() => Clock.FrameCount;
        public static float UnityEngine_Time__get_realtimeSinceStartup() => Clock.RealtimeSinceStartup;
        public static bool UnityEngine_Time__get_inFixedTimeStep() => Clock.InFixedTimeStep;

        // ------------------------------------------------------------ math

        public static void UnityEngine_Quaternion__INTERNAL_CALL_Internal_FromEulerRad(ref Vector3 euler, out Quaternion value) => value = MathX.FromEulerRad(euler);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_Internal_ToEulerRad(ref Quaternion rotation, out Vector3 value) => value = MathX.ToEulerRad(rotation);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_Inverse(ref Quaternion rotation, out Quaternion value) => value = MathX.Inverse(rotation);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_AngleAxis(float angle, ref Vector3 axis, out Quaternion value) => value = MathX.AngleAxisRad(angle * Mathf.Deg2Rad, axis);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_AxisAngle(ref Vector3 axis, float angle, out Quaternion value) => value = MathX.AngleAxisRad(angle, axis);

        /// <summary>
        /// Unity's native Vector3.RotateTowards: the direction turned toward the target's by at most
        /// maxRadiansDelta, the length moved toward its by at most maxMagnitudeDelta (missing until
        /// round 19: Cagney Carnation's level asked it 220 times in a fight and got zero).
        /// </summary>
        public static void UnityEngine_Vector3__INTERNAL_CALL_RotateTowards(ref Vector3 current, ref Vector3 target, float maxRadiansDelta, float maxMagnitudeDelta, out Vector3 value)
        {
            const float eps = 0.00001f;
            float lm = current.magnitude, rm = target.magnitude;
            if (lm > eps && rm > eps)
            {
                Vector3 ln = current / lm, rn = target / rm;
                float dot = Vector3.Dot(ln, rn);
                if (dot > 1f - eps) { value = Vector3.MoveTowards(current, target, maxMagnitudeDelta); return; }
                Vector3 axis;
                float angle;
                if (dot < -1f + eps) { axis = OrthoNormalFast(ln); angle = maxRadiansDelta; }
                else { axis = Vector3.Cross(ln, rn).normalized; angle = Math.Min(maxRadiansDelta, (float)Math.Acos(dot)); }
                var rotated = MathX.AngleAxisRad(angle, axis) * ln;
                float d = rm - lm;
                value = rotated * (d > 0f ? lm + Math.Min(d, maxMagnitudeDelta) : lm - Math.Min(-d, maxMagnitudeDelta));
                return;
            }
            value = Vector3.MoveTowards(current, target, maxMagnitudeDelta);
        }

        /// <summary>A unit vector at right angles to n (Unity's OrthoNormalVectorFast).</summary>
        static Vector3 OrthoNormalFast(Vector3 n)
        {
            if (Math.Abs(n.z) > 0.70710678f)
            {
                float k = 1f / (float)Math.Sqrt(n.y * n.y + n.z * n.z);
                return new Vector3(0f, -n.z * k, n.y * k);
            }
            float k2 = 1f / (float)Math.Sqrt(n.x * n.x + n.y * n.y);
            return new Vector3(-n.y * k2, n.x * k2, 0f);
        }

        /// <summary>Bounds.Contains: the point inside the box, faces included (Unity's AABB::IsInside).</summary>
        public static bool UnityEngine_Bounds__INTERNAL_CALL_Internal_Contains(ref Bounds m, ref Vector3 point)
        {
            Vector3 c = m.center, e = m.extents;
            return !(point.x < c.x - e.x || point.x > c.x + e.x || point.y < c.y - e.y || point.y > c.y + e.y || point.z < c.z - e.z || point.z > c.z + e.z);
        }

        /// <summary>ColorUtility.TryParseHtmlString's native half: #RGB, #RGBA, #RRGGBB, #RRGGBBAA or a colour name.</summary>
        public static bool UnityEngine_ColorUtility__DoTryParseHtmlColor(string htmlString, out Color32 color)
        {
            color = new Color32(0, 0, 0, 255);
            if (string.IsNullOrEmpty(htmlString)) return false;
            string s = htmlString.Trim();
            if (s[0] == '#')
            {
                string h = s.Substring(1);
                if (!uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint v)) return false;
                byte N(uint x) => (byte)(x * 17);   // one hex digit spread over a byte
                switch (h.Length)
                {
                    case 3: color = new Color32(N(v >> 8 & 15), N(v >> 4 & 15), N(v & 15), 255); return true;
                    case 4: color = new Color32(N(v >> 12 & 15), N(v >> 8 & 15), N(v >> 4 & 15), N(v & 15)); return true;
                    case 6: color = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255); return true;
                    case 8: color = new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v); return true;
                    default: return false;
                }
            }
            switch (s.ToLowerInvariant())
            {
                case "red": color = new Color32(255, 0, 0, 255); return true;
                case "cyan": case "aqua": color = new Color32(0, 255, 255, 255); return true;
                case "blue": color = new Color32(0, 0, 255, 255); return true;
                case "darkblue": color = new Color32(0, 0, 160, 255); return true;
                case "lightblue": color = new Color32(173, 216, 230, 255); return true;
                case "purple": color = new Color32(128, 0, 128, 255); return true;
                case "yellow": color = new Color32(255, 255, 0, 255); return true;
                case "lime": color = new Color32(0, 255, 0, 255); return true;
                case "fuchsia": case "magenta": color = new Color32(255, 0, 255, 255); return true;
                case "white": color = new Color32(255, 255, 255, 255); return true;
                case "silver": color = new Color32(192, 192, 192, 255); return true;
                case "grey": case "gray": color = new Color32(128, 128, 128, 255); return true;
                case "black": color = new Color32(0, 0, 0, 255); return true;
                case "orange": color = new Color32(255, 165, 0, 255); return true;
                case "brown": color = new Color32(165, 42, 42, 255); return true;
                case "maroon": color = new Color32(128, 0, 0, 255); return true;
                case "green": color = new Color32(0, 128, 0, 255); return true;
                case "olive": color = new Color32(128, 128, 0, 255); return true;
                case "navy": color = new Color32(0, 0, 128, 255); return true;
                case "teal": color = new Color32(0, 128, 128, 255); return true;
                default: return false;
            }
        }
        public static void UnityEngine_Quaternion__INTERNAL_CALL_Slerp(ref Quaternion a, ref Quaternion b, float t, out Quaternion value) => value = MathX.Slerp(a, b, Mathf.Clamp01(t));
        public static void UnityEngine_Quaternion__INTERNAL_CALL_SlerpUnclamped(ref Quaternion a, ref Quaternion b, float t, out Quaternion value) => value = MathX.Slerp(a, b, t);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_Lerp(ref Quaternion a, ref Quaternion b, float t, out Quaternion value) => value = MathX.Lerp(a, b, Mathf.Clamp01(t));
        public static void UnityEngine_Quaternion__INTERNAL_CALL_LerpUnclamped(ref Quaternion a, ref Quaternion b, float t, out Quaternion value) => value = MathX.Lerp(a, b, t);
        public static void UnityEngine_Quaternion__INTERNAL_CALL_FromToRotation(ref Vector3 fromDirection, ref Vector3 toDirection, out Quaternion value)
        {
            var a = fromDirection.normalized; var b = toDirection.normalized;
            float d = Vector3.Dot(a, b);
            if (d > 0.999999f) { value = Quaternion.identity; return; }
            if (d < -0.999999f)
            {
                var axis = Vector3.Cross(Vector3.right, a);
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(Vector3.up, a);
                value = MathX.AngleAxisRad(Mathf.PI, axis); return;
            }
            var c = Vector3.Cross(a, b);
            value = MathX.Normalize(new Quaternion(c.x, c.y, c.z, 1f + d));
        }
        public static void UnityEngine_Quaternion__INTERNAL_CALL_LookRotation(ref Vector3 forward, ref Vector3 upwards, out Quaternion value)
        {
            var f = forward.normalized;
            if (f.sqrMagnitude < 1e-12f) { value = Quaternion.identity; return; }
            var r = Vector3.Cross(upwards, f).normalized;
            if (r.sqrMagnitude < 1e-12f) { var fw = Vector3.forward; UnityEngine_Quaternion__INTERNAL_CALL_FromToRotation(ref fw, ref f, out value); return; }
            var u = Vector3.Cross(f, r);
            float m00 = r.x, m01 = u.x, m02 = f.x, m10 = r.y, m11 = u.y, m12 = f.y, m20 = r.z, m21 = u.z, m22 = f.z;
            float tr = m00 + m11 + m22;
            Quaternion q;
            if (tr > 0f) { float s = MathF.Sqrt(tr + 1f) * 2f; q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
            else if (m00 > m11 && m00 > m22) { float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f; q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
            else if (m11 > m22) { float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f; q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
            else { float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f; q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
            value = MathX.Normalize(q);
        }
        public static void UnityEngine_Matrix4x4__INTERNAL_CALL_TRS(ref Vector3 pos, ref Quaternion q, ref Vector3 s, out Matrix4x4 value) => value = MathX.TRS(pos, q, s);
        public static void UnityEngine_Matrix4x4__INTERNAL_CALL_Inverse(ref Matrix4x4 m, out Matrix4x4 value) => MathX.Invert(m, out value);
        public static bool UnityEngine_Matrix4x4__INTERNAL_CALL_Invert(ref Matrix4x4 inMatrix, out Matrix4x4 dest) => MathX.Invert(inMatrix, out dest);
        public static void UnityEngine_Matrix4x4__INTERNAL_CALL_Transpose(ref Matrix4x4 m, out Matrix4x4 value) => value = Transpose(m);
        static Matrix4x4 Transpose(Matrix4x4 m) { var r = m; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) r[i, j] = m[j, i]; return r; }
        public static bool UnityEngine_Matrix4x4__get_isIdentity(ref Matrix4x4 self) => self == Matrix4x4.identity;
        public static void UnityEngine_Matrix4x4__INTERNAL_CALL_Ortho(float left, float right, float bottom, float top, float zNear, float zFar, out Matrix4x4 value)
        {
            value = Matrix4x4.identity;
            value.m00 = 2f / (right - left); value.m11 = 2f / (top - bottom); value.m22 = -2f / (zFar - zNear);
            value.m03 = -(right + left) / (right - left); value.m13 = -(top + bottom) / (top - bottom); value.m23 = -(zFar + zNear) / (zFar - zNear);
        }
        public static bool UnityEngine_Mathf__IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
        public static int UnityEngine_Mathf__NextPowerOfTwo(int value) { if (value <= 0) return 0; int p = 1; while (p < value) p <<= 1; return p; }
        public static int UnityEngine_Mathf__ClosestPowerOfTwo(int value)
        {
            int n = UnityEngine_Mathf__NextPowerOfTwo(value), lo = n >> 1;
            return value - lo < n - value ? lo : n;
        }
        public static float UnityEngine_Mathf__GammaToLinearSpace(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        public static float UnityEngine_Mathf__LinearToGammaSpace(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        public static float UnityEngine_Mathf__PerlinNoise(float x, float y) => Perlin.Noise(x, y);
    }
}
