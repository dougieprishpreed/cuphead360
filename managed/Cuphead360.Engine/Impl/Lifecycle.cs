using System;
using System.Reflection;
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
            // MonoBehaviours/ScriptableObjects run their constructor (field
            // initialisers) exactly like Unity does. Built-in classes created by
            // the engine (a loaded Material, Mesh, ...) get no constructor call,
            // and some (Material, Shader) have no parameterless one at all.
            // (TextAsset(), GameObject() etc. would call Internal_Create* for a
            // second native object.)
            if (typeof(MonoBehaviour).IsAssignableFrom(t) || typeof(ScriptableObject).IsAssignableFrom(t))
            {
                var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ctor != null) return (UObject)ctor.Invoke(null);
            }
            return (UObject)RuntimeHelpers.GetUninitializedObject(t);
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
            if (type == typeof(RectTransform))
            {
                if (go.Transform is NRectTransform have) return have;
                return SwapTransform(go, true);
            }
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

        /// <summary>Replace a GameObject's Transform with a RectTransform (or back), keeping hierarchy and pose.</summary>
        public static NTransform SwapTransform(NGameObject go, bool rect, bool persistent = false)
        {
            var old = go.Transform;
            var managed = (Transform)NewManaged(rect ? typeof(RectTransform) : typeof(Transform));
            NTransform rec = rect ? new NRectTransform() : new NTransform();
            Objects.Register(managed, rec, go.Name, persistent);
            rec.GameObject = go;
            rec.LocalPosition = old.LocalPosition; rec.LocalRotation = old.LocalRotation; rec.LocalScale = old.LocalScale;
            rec.Parent = old.Parent;
            if (old.Parent != null) old.Parent.Children[old.Parent.Children.IndexOf(old)] = rec;
            foreach (var c in old.Children) { c.Parent = rec; rec.Children.Add(c); }
            old.Children.Clear();
            go.Components[go.Components.IndexOf(old)] = rec;
            go.Transform = rec;
            Objects.Release(old);
            rec.Invalidate();
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

        /// <summary>Awake/OnEnable for a freshly built active hierarchy (Instantiate).</summary>
        public static void ActivateTreeNew(NTransform t) => ActivateTree(t);

        /// <summary>Awake/OnEnable for a scene's active roots, as one batch (scene load).</summary>
        public static void ActivateTreesNew(List<NTransform> roots)
        {
            var behaviours = new List<NBehaviour>();
            foreach (var r in roots) Collect(r, behaviours);
            Wake(behaviours);
        }

        /// <summary>
        /// A hierarchy that became active: its built-in components first, in hierarchy order, then its
        /// behaviours' Awake and OnEnable in script execution order (hierarchy order among equals), as
        /// Unity wakes what it activates together.
        /// </summary>
        static void ActivateTree(NTransform t)
        {
            var behaviours = new List<NBehaviour>();
            Collect(t, behaviours);
            Wake(behaviours);
        }

        static void Collect(NTransform t, List<NBehaviour> behaviours)
        {
            var go = t.GameObject;
            if (!go.ActiveSelf) return;
            foreach (var c in go.Components.ToArray())
                if (c is NBehaviour b) behaviours.Add(b);
                else Builtins.OnActivated(c, true);
            foreach (var child in t.Children.ToArray()) Collect(child, behaviours);
        }

        static void Wake(List<NBehaviour> behaviours)
        {
            bool ordered = true;
            for (int i = 1; i < behaviours.Count && ordered; i++) ordered = behaviours[i - 1].ExecOrder <= behaviours[i].ExecOrder;
            if (!ordered)
            {
                // stable insertion: equal orders keep hierarchy order (the few ordered classes move)
                for (int i = 1; i < behaviours.Count; i++)
                {
                    var b = behaviours[i];
                    int j = i - 1;
                    while (j >= 0 && behaviours[j].ExecOrder > b.ExecOrder) { behaviours[j + 1] = behaviours[j]; j--; }
                    behaviours[j + 1] = b;
                }
            }
            foreach (var b in behaviours) Behaviours.Activate(b);
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
            var layout = NRectTransform.Snapshot(t);
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
            // Rects keep their anchored positions under the new parent (the moved
            // rect itself only when its local values stay).
            NRectTransform.Restore(layout, worldPositionStays && layout.Count > 0 && layout[0].t == t ? 1 : 0, true);
            t.Invalidate();
            // Activation follows the hierarchy.
            bool nowActive = t.GameObject.ActiveInHierarchy;
            if (nowActive && !wasActive) ActivateTree(t);
            else if (wasActive && !nowActive) DeactivateTree(t, true);
        }

        static float Div(float a, float b) => b == 0f ? 0f : a / b;
    }

    /// <summary>Built-in (non-script) components: SpriteRenderer, Camera, Animator, ...</summary>
    public static class Builtins
    {
        public static NComponent Create(Type t)
        {
            if (typeof(Camera).IsAssignableFrom(t)) return new NCamera();
            if (typeof(AudioSource).IsAssignableFrom(t)) return new NAudioSource();
            if (typeof(AudioListener).IsAssignableFrom(t)) return new NAudioListener();
            if (typeof(SpriteRenderer).IsAssignableFrom(t)) return new NSpriteRenderer();
            if (typeof(Renderer).IsAssignableFrom(t)) return new NRenderer();
            if (typeof(Animator).IsAssignableFrom(t)) return new NAnimator();
            if (typeof(Collider2D).IsAssignableFrom(t)) return new NCollider2D { Kind = NCollider2D.KindOf(t) };
            if (typeof(Rigidbody2D).IsAssignableFrom(t)) return new NRigidbody2D();
            if (typeof(Canvas).IsAssignableFrom(t)) return new NCanvas();
            if (typeof(CanvasGroup).IsAssignableFrom(t)) return new NCanvasGroup();
            if (typeof(CanvasRenderer).IsAssignableFrom(t)) return new NCanvasRenderer();
            return new NComponent();
        }
        public static void OnAdded(NComponent c) => c.Added();
        public static void OnDestroyed(NComponent c) => c.Removed();
        public static void OnActivated(NComponent c, bool on) => c.Activated(on);
        /// <summary>Instantiate: engine state of a built-in component (src -> copy).</summary>
        public static void CopyState(NComponent src, NComponent dst, Dictionary<NObject, NObject> map)
        {
            if (src.GetType() == dst.GetType()) dst.CopyFrom(src, map);
        }
        /// <summary>Instantiate of an asset (Material, Sprite, ...): its engine state.</summary>
        public static void CopyAsset(NObject src, NObject dst)
        {
            if (src.Managed is Material sm && dst.Managed is Material dm) MaterialState.Set(dm, MaterialState.Of(sm).Clone());
            else if (src.Managed is Sprite ss && dst.Managed is Sprite ds) Sprites.Set(ds, Sprites.Of(ss));
        }
        /// <summary>Scene/prefab load: a built-in component's converted fields.</summary>
        public static void OnLoaded(NComponent c, ChobObject co, SceneBuild b) => c.Load(co, b);
    }
}
