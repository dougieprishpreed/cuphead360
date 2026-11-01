using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    public static class Lifecycle
    {
        /// <summary>A managed instance of a built-in Unity type, as the native player makes them.</summary>
        public static T NewManaged<T>() where T : UObject => (T)NewManaged(typeof(T));

        public static UObject NewManaged(Type t)
        {
            // Built-in wrappers have trivial constructors; MonoBehaviours run theirs
            // (field initialisers) exactly like Unity does.
            return (UObject)Activator.CreateInstance(t, nonPublic: true);
        }

        public static NGameObject CreateGameObject(GameObject managed, string name, bool persistent = false)
        {
            var go = Objects.Register(managed, new NGameObject(), name, persistent);
            go.Scene = Scenes.ActiveHandle;
            var tr = Objects.Register(NewManaged<Transform>(), new NTransform(), name, persistent);
            tr.GameObject = go;
            go.Transform = tr;
            go.Components.Add(tr);
            return go;
        }

        public static NComponent AddComponent(NGameObject go, Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract) return null;
            if (type == typeof(Transform)) return go.Transform;
            var managed = (Component)NewManaged(type);
            if (managed is MonoBehaviour mb)
            {
                var b = Behaviours.Attach(mb, go);
                Behaviours.Activate(b);
                return b;
            }
            var rec = Objects.Register(managed, Builtins.Create(type), go.Name);
            rec.GameObject = go;
            go.Components.Add(rec);
            Builtins.OnAdded(rec);
            return rec;
        }

        public static void SetActive(NGameObject go, bool value)
        {
            if (go.ActiveSelf == value) return;
            bool wasActive = go.ActiveInHierarchy;
            go.ActiveSelf = value;
            bool nowActive = go.ActiveInHierarchy;
            if (wasActive == nowActive) return;
            if (nowActive) ActivateTree(go.Transform); else DeactivateTree(go.Transform, true);
        }

        /// <summary>Awake/OnEnable for a freshly built active hierarchy (Instantiate, scene load).</summary>
        public static void ActivateTreeNew(NTransform t) => ActivateTree(t);

        static void ActivateTree(NTransform t)
        {
            var go = t.GameObject;
            if (!go.ActiveSelf) return;
            foreach (var c in go.Components.ToArray())
                if (c is NBehaviour b) Behaviours.Activate(b);
                else Builtins.OnActivated(c, true);
            foreach (var child in t.Children.ToArray()) ActivateTree(child);
        }

        /// <summary>OnDisable for a hierarchy that just went inactive (top: the object whose state changed).</summary>
        static void DeactivateTree(NTransform t, bool top)
        {
            var go = t.GameObject;
            if (!top && !go.ActiveSelf) return;   // was inactive already
            foreach (var c in go.Components.ToArray())
            {
                if (c is NBehaviour b)
                {
                    if (b.Awoken && b.Enabled) Behaviours.Send(b, b.Messages.OnDisable);
                    // A GameObject going inactive stops its behaviours' coroutines.
                    Coroutines.StopAll(b);
                }
                else Builtins.OnActivated(c, false);
            }
            foreach (var child in t.Children.ToArray()) DeactivateTree(child, false);
        }

        /// <summary>Unity's end-of-frame destruction of a GameObject or component.</summary>
        public static void DestroyNow(NObject o)
        {
            if (o == null || o.Destroyed) return;
            if (o is NGameObject go) { DestroyTree(go.Transform, true); return; }
            if (o is NTransform t) { DestroyTree(t, true); return; }
            if (o is NComponent c) { DestroyComponent(c); return; }
            Objects.Release(o);
        }

        static void DestroyTree(NTransform t, bool top)
        {
            var go = t.GameObject;
            if (go.Destroyed) return;
            bool active = go.ActiveInHierarchy;
            foreach (var child in t.Children.ToArray()) DestroyTree(child, false);
            foreach (var c in go.Components.ToArray())
            {
                if (c is NBehaviour b)
                {
                    if (active && b.Awoken && b.Enabled) Behaviours.Send(b, b.Messages.OnDisable);
                    if (b.Awoken) Behaviours.Send(b, b.Messages.OnDestroy);
                    Coroutines.StopAll(b);
                }
                else Builtins.OnDestroyed(c);
            }
            if (top && t.Parent != null) { t.Parent.Children.Remove(t); t.Parent = null; }
            foreach (var c in go.Components) Objects.Release(c);
            go.Components.Clear();
            Scenes.Forget(go);
            Objects.Release(go);
        }

        static void DestroyComponent(NComponent c)
        {
            if (c is NBehaviour b)
            {
                if (c.GameObject.ActiveInHierarchy && b.Awoken && b.Enabled) Behaviours.Send(b, b.Messages.OnDisable);
                if (b.Awoken) Behaviours.Send(b, b.Messages.OnDestroy);
                Coroutines.StopAll(b);
            }
            else Builtins.OnDestroyed(c);
            c.GameObject.Components.Remove(c);
            Objects.Release(c);
        }

        public static void SetParent(NTransform t, NTransform parent, bool worldPositionStays)
        {
            if (t.Parent == parent) return;
            for (var p = parent; p != null; p = p.Parent) if (p == t) return;   // no cycles
            bool wasActive = t.GameObject.ActiveInHierarchy;
            Vector3 wp = t.Position; Quaternion wr = t.Rotation; Vector3 ws = t.LossyScale;
            if (t.Parent != null) t.Parent.Children.Remove(t);
            else Scenes.RemoveRoot(t.GameObject);
            t.Parent = parent;
            if (parent != null) parent.Children.Add(t);
            else Scenes.AddRoot(t.GameObject);
            if (worldPositionStays)
            {
                if (parent == null) { t.LocalPosition = wp; t.LocalRotation = wr; t.LocalScale = ws; }
                else
                {
                    MathX.Invert(parent.LocalToWorld, out var inv);
                    t.LocalPosition = inv.MultiplyPoint3x4(wp);
                    t.LocalRotation = MathX.Inverse(parent.Rotation) * wr;
                    var ps = parent.LossyScale;
                    t.LocalScale = new Vector3(Div(ws.x, ps.x), Div(ws.y, ps.y), Div(ws.z, ps.z));
                }
            }
            t.Invalidate();
            // Activation follows the hierarchy.
            bool nowActive = t.GameObject.ActiveInHierarchy;
            if (nowActive && !wasActive) ActivateTree(t);
            else if (wasActive && !nowActive) DeactivateTree(t, true);
        }

        static float Div(float a, float b) => b == 0f ? 0f : a / b;
    }

    /// <summary>Built-in (non-script) components: SpriteRenderer, colliders, Animator, ...</summary>
    public static class Builtins
    {
        public static Func<Type, NComponent> Factory;
        public static NComponent Create(Type t) => Factory?.Invoke(t) ?? new NComponent();
        public static Action<NComponent> Added, Destroyed;
        public static Action<NComponent, bool> Activated;
        public static void OnAdded(NComponent c) => Added?.Invoke(c);
        public static void OnDestroyed(NComponent c) => Destroyed?.Invoke(c);
        public static void OnActivated(NComponent c, bool on) => Activated?.Invoke(c, on);
        /// <summary>Instantiate: engine state of a built-in component (src -> copy).</summary>
        public static Action<NComponent, NComponent, Dictionary<NObject, NObject>> CopyStateHook;
        public static void CopyState(NComponent src, NComponent dst, Dictionary<NObject, NObject> map) => CopyStateHook?.Invoke(src, dst, map);
        public static Action<NObject, NObject> CopyAssetHook;
        public static void CopyAsset(NObject src, NObject dst) => CopyAssetHook?.Invoke(src, dst);
    }
}
