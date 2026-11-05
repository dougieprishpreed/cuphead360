using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// The engine-side record of a UnityEngine.Object (what the native Unity
    /// player keeps behind m_CachedPtr). m_CachedPtr points at a small native
    /// block: [0] = slot in the object table, [4] = instance id, read directly by
    /// Object.GetInstanceID through GetOffsetOfInstanceIDInCPlusPlusObject.
    /// A destroyed object gets m_CachedPtr = 0, which is what makes Unity's
    /// managed == null true for it.
    /// </summary>
    public class NObject
    {
        public UObject Managed;
        public int Slot;
        public int InstanceId;
        public string Name = "";
        public HideFlags Flags;
        public IntPtr Block;
        public bool Destroyed;
        /// <summary>Object.Destroy was called on it this frame (it goes at the frame's end).</summary>
        public bool PendingDestroy;
        /// <summary>Loaded from game data (an asset or a scene object), not created at run time.</summary>
        public bool Persistent;
    }

    public sealed class NGameObject : NObject
    {
        public readonly List<NComponent> Components = new List<NComponent>();
        public NTransform Transform;
        public int Layer;
        public string Tag = "Untagged";
        bool activeSelf = true, isAsset, building;
        public bool ActiveSelf { get => activeSelf; set { if (activeSelf != value) { activeSelf = value; ActiveVersion++; } } }
        /// <summary>Scene handle; 0 after DontDestroyOnLoad.</summary>
        public int Scene;
        public bool DontDestroy;
        /// <summary>Part of a prefab asset: never awake, updated or found as a scene object.</summary>
        public bool IsAsset { get => isAsset; set { if (isAsset != value) { isAsset = value; ActiveVersion++; } } }
        /// <summary>Part of a scene still being built over frames (SceneBuild.Building): inactive until it activates.</summary>
        public bool Building { get => building; set { if (building != value) { building = value; ActiveVersion++; } } }

        /// <summary>Changes when any GameObject's ActiveSelf, IsAsset or Building changes.</summary>
        public static int ActiveVersion;
        // ActiveInHierarchy's answer while no flag above and no parent changed (it walked up the
        // hierarchy at every ask: per sprite drawn, behaviour updated and collider queried).
        bool activeCached;
        int activeAt = -1, activeHierarchyAt = -1;

        public bool ActiveInHierarchy
        {
            get
            {
                if (activeAt == ActiveVersion && activeHierarchyAt == NTransform.HierarchyVersion) return activeCached;
                bool on = !isAsset && !building;
                for (var t = Transform; on && t != null; t = t.Parent)
                    if (!t.GameObject.activeSelf) on = false;
                activeCached = on;
                activeAt = ActiveVersion;
                activeHierarchyAt = NTransform.HierarchyVersion;
                return on;
            }
        }
    }

    public class NComponent : NObject
    {
        public NGameObject GameObject;
        /// <summary>Behaviour.enabled (always true for non-Behaviour components).</summary>
        public bool Enabled = true;
        /// <summary>The converted object this component was loaded from (null if made at run time).</summary>
        public ChobObject Data;

        // Built-in component modules (render, animation, UI) override these.
        /// <summary>Scene/prefab load: the component's converted fields.</summary>
        public virtual void Load(ChobObject co, SceneBuild b) { }
        /// <summary>Instantiate: copy engine state from the original (map: original -> copy).</summary>
        public virtual void CopyFrom(NComponent src, Dictionary<NObject, NObject> map) { }
        public virtual void Added() { }
        public virtual void Activated(bool on) { }
        public virtual void Removed() { }
    }

    public class NTransform : NComponent
    {
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.identity;
        public Vector3 LocalScale = Vector3.one;
        NTransform parent;
        public NTransform Parent { get => parent; set { parent = value; HierarchyVersion++; } }
        /// <summary>
        /// Changes when any transform's parent changes or a rigidbody is added or released:
        /// colliders keep the body they found (NCollider2D.Rigidbody) while it holds.
        /// </summary>
        public static int HierarchyVersion;
        public readonly List<NTransform> Children = new List<NTransform>();
        public bool HasChanged = true;
        // Cached local-to-world matrix; invalidated down the hierarchy.
        Matrix4x4 world;
        bool worldValid;

        /// <summary>Changes each time the cached world matrix is let go (colliders keep their shapes while it holds).</summary>
        public int WorldVersion;

        public void Invalidate()
        {
            HasChanged = true;
            if (!worldValid) return;
            worldValid = false;
            WorldVersion++;
            for (int i = 0; i < Children.Count; i++) Children[i].Invalidate();
        }

        public Matrix4x4 LocalToWorld
        {
            get
            {
                if (!worldValid)
                {
                    var local = MathX.TRS(LocalPosition, LocalRotation, LocalScale);
                    world = Parent == null ? local : Parent.LocalToWorld * local;
                    worldValid = true;
                }
                return world;
            }
        }

        public Quaternion Rotation => Parent == null ? LocalRotation : Parent.Rotation * LocalRotation;
        public Vector3 Position => Parent == null ? LocalPosition : Parent.LocalToWorld.MultiplyPoint3x4(LocalPosition);
        public Vector3 LossyScale
        {
            get
            {
                if (Parent == null) return LocalScale;
                var p = Parent.LossyScale;
                return new Vector3(p.x * LocalScale.x, p.y * LocalScale.y, p.z * LocalScale.z);
            }
        }
    }

    public static unsafe class Objects
    {
        static readonly List<NObject> table = new List<NObject> { null };   // slot 0 = null
        static readonly Stack<int> freeSlots = new Stack<int>();
        static int nextRuntimeId = -2;          // run-time objects: negative ids, as Unity
        static int nextAssetId = 2;             // loaded objects: positive ids

        public const int InstanceIdOffset = 4;

        public static T Register<T>(UObject managed, T rec, string name, bool persistent = false) where T : NObject
        {
            int slot = freeSlots.Count > 0 ? freeSlots.Pop() : table.Count;
            if (slot == table.Count) table.Add(rec); else table[slot] = rec;
            rec.Managed = managed;
            rec.Slot = slot;
            rec.Name = name ?? "";
            rec.Persistent = persistent;
            rec.InstanceId = persistent ? (nextAssetId += 2) : (nextRuntimeId -= 2);
            rec.Block = (IntPtr)NativeMemory.AllocZeroed(8);
            ((int*)rec.Block)[0] = slot;
            ((int*)rec.Block)[1] = rec.InstanceId;
            managed.m_CachedPtr = rec.Block;
            byId[rec.InstanceId] = rec;
            Typed.Add(rec);
            return rec;
        }

        static readonly Dictionary<int, NObject> byId = new Dictionary<int, NObject>();

        /// <summary>The record behind a live object, or null for null / destroyed objects.</summary>
        public static NObject Of(UObject o)
        {
            if ((object)o == null || o.m_CachedPtr == IntPtr.Zero) return null;
            return table[((int*)o.m_CachedPtr)[0]];
        }

        public static NGameObject GO(UObject o) => Of(o) as NGameObject;
        public static NComponent Comp(UObject o) => Of(o) as NComponent;
        public static NTransform Tr(UObject o) => Of(o) as NTransform;

        public static NObject ByInstanceId(int id) => byId.TryGetValue(id, out var r) ? r : null;

        /// <summary>Final release: Unity's managed object becomes "fake null".</summary>
        static readonly string traceRelease = Environment.GetEnvironmentVariable("CH360_TRACE_RELEASE");
        /// <summary>Counts releases (the player loop drops destroyed behaviours only when it moved).</summary>
        public static int Releases;

        public static void Release(NObject rec)
        {
            if (rec.Destroyed) return;
            Releases++;
            if (traceRelease != null && rec.Name != null && rec.Name.Contains(traceRelease))
                Log.Line($"TRACE release {rec.Name} ({rec.GetType().Name}) frame {Clock.FrameCount}\n{Environment.StackTrace}");
            rec.Destroyed = true;
            rec.Managed.m_CachedPtr = IntPtr.Zero;
            NativeMemory.Free((void*)rec.Block);
            rec.Block = IntPtr.Zero;
            table[rec.Slot] = null;
            freeSlots.Push(rec.Slot);
            byId.Remove(rec.InstanceId);
            Typed.Remove(rec);
        }

        public static IEnumerable<NObject> Live()
        {
            for (int i = 1; i < table.Count; i++)
                if (table[i] != null) yield return table[i];
        }

        public static int LiveCount
        {
            get { int n = 0; for (int i = 1; i < table.Count; i++) if (table[i] != null) n++; return n; }
        }
    }

    /// <summary>Unity math that the player implements natively.</summary>
    public static class MathX
    {
        public static Matrix4x4 TRS(Vector3 p, Quaternion q, Vector3 s)
        {
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z;
            float xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            Matrix4x4 m;
            m.m00 = (1f - (yy + zz)) * s.x; m.m01 = (xy - wz) * s.y; m.m02 = (xz + wy) * s.z; m.m03 = p.x;
            m.m10 = (xy + wz) * s.x; m.m11 = (1f - (xx + zz)) * s.y; m.m12 = (yz - wx) * s.z; m.m13 = p.y;
            m.m20 = (xz - wy) * s.x; m.m21 = (yz + wx) * s.y; m.m22 = (1f - (xx + yy)) * s.z; m.m23 = p.z;
            m.m30 = 0; m.m31 = 0; m.m32 = 0; m.m33 = 1;
            return m;
        }

        public static bool Invert(Matrix4x4 m, out Matrix4x4 r)
        {
            float[] a = new float[16], inv = new float[16];
            for (int i = 0; i < 16; i++) a[i] = m[i];
            inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
            inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
            inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
            inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
            inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
            inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
            inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
            inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
            inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
            inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
            inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
            inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
            inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
            inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
            inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
            inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
            float det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12];
            r = Matrix4x4.identity;
            if (det == 0f) { r = Matrix4x4.zero; return false; }
            det = 1f / det;
            for (int i = 0; i < 16; i++) r[i] = inv[i] * det;
            return true;
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (n == 0f) return new Quaternion(0, 0, 0, 1);
            float k = 1f / n;
            return new Quaternion(-q.x * k, -q.y * k, -q.z * k, q.w * k);
        }

        // Unity's Euler convention: ZXY (rotate about Z, then X, then Y), radians here.
        public static Quaternion FromEulerRad(Vector3 e)
        {
            float cx = MathF.Cos(e.x * 0.5f), sx = MathF.Sin(e.x * 0.5f);
            float cy = MathF.Cos(e.y * 0.5f), sy = MathF.Sin(e.y * 0.5f);
            float cz = MathF.Cos(e.z * 0.5f), sz = MathF.Sin(e.z * 0.5f);
            return new Quaternion(
                sx * cy * cz + cx * sy * sz,
                cx * sy * cz - sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz);
        }

        public static Vector3 ToEulerRad(Quaternion q)
        {
            // Inverse of FromEulerRad (ZXY): x = asin(2(wx - yz)).
            float sinx = 2f * (q.w * q.x - q.y * q.z);
            float x, y, z;
            if (MathF.Abs(sinx) >= 0.99999f)
            {
                x = MathF.CopySign(MathF.PI / 2f, sinx);
                y = 2f * MathF.Atan2(q.y, q.w);
                z = 0f;
            }
            else
            {
                x = MathF.Asin(sinx);
                y = MathF.Atan2(2f * (q.w * q.y + q.x * q.z), 1f - 2f * (q.x * q.x + q.y * q.y));
                z = MathF.Atan2(2f * (q.w * q.z + q.x * q.y), 1f - 2f * (q.x * q.x + q.z * q.z));
            }
            return new Vector3(x, y, z);
        }

        public static Quaternion AngleAxisRad(float rad, Vector3 axis)
        {
            float m = axis.magnitude;
            if (m < 1e-6f) return Quaternion.identity;
            axis /= m;
            float s = MathF.Sin(rad * 0.5f);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, MathF.Cos(rad * 0.5f));
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f) { dot = -dot; b = new Quaternion(-b.x, -b.y, -b.z, -b.w); }
            float k0, k1;
            if (dot > 0.9995f) { k0 = 1f - t; k1 = t; }
            else
            {
                float th = MathF.Acos(dot), s = MathF.Sin(th);
                k0 = MathF.Sin((1f - t) * th) / s; k1 = MathF.Sin(t * th) / s;
            }
            return Normalize(new Quaternion(a.x * k0 + b.x * k1, a.y * k0 + b.y * k1, a.z * k0 + b.z * k1, a.w * k0 + b.w * k1));
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            float s = dot < 0f ? -1f : 1f;
            return Normalize(new Quaternion(a.x + (b.x * s - a.x) * t, a.y + (b.y * s - a.y) * t, a.z + (b.z * s - a.z) * t, a.w + (b.w * s - a.w) * t));
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float n = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n < 1e-9f ? Quaternion.identity : new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }
    }

    /// <summary>
    /// Live records of the types hot paths scan every frame (physics, rendering,
    /// animation). A destroyed record leaves its list when the list is next read
    /// (one pass for a whole batch of destroys, order kept), not one search each.
    /// </summary>
    public static class Typed
    {
        static readonly List<NCollider2D> colliders = new List<NCollider2D>();
        static readonly List<NSpriteRenderer> spriteRenderers = new List<NSpriteRenderer>();
        static readonly List<NCanvas> canvases = new List<NCanvas>();
        static readonly List<NCamera> cameras = new List<NCamera>();
        static readonly List<NBehaviour> behaviours = new List<NBehaviour>();
        static readonly List<NGameObject> gameObjects = new List<NGameObject>();
        static int dirty;   // bit per list holding destroyed records

        public static List<NCollider2D> Colliders => Clean(colliders, 1);
        public static List<NSpriteRenderer> SpriteRenderers => Clean(spriteRenderers, 2);
        public static List<NCanvas> Canvases => Clean(canvases, 4);
        public static List<NCamera> Cameras => Clean(cameras, 8);
        public static List<NBehaviour> Behaviours => Clean(behaviours, 16);
        public static List<NGameObject> GameObjects => Clean(gameObjects, 32);

        static List<T> Clean<T>(List<T> list, int bit) where T : NObject
        {
            if ((dirty & bit) != 0)
            {
                dirty &= ~bit;
                list.RemoveAll(x => x.Destroyed);
            }
            return list;
        }

        public static void Add(NObject r)
        {
            switch (r)
            {
                case NCollider2D c: colliders.Add(c); break;
                case NSpriteRenderer s: spriteRenderers.Add(s); break;
                case NCanvas cv: canvases.Add(cv); break;
                case NCamera cam: cameras.Add(cam); break;
                case NBehaviour b: behaviours.Add(b); break;
                case NGameObject g: gameObjects.Add(g); break;
                // (every caller adds the record to its GameObject's components right after, with no query between)
                case NRigidbody2D: NTransform.HierarchyVersion++; break;
            }
        }

        /// <summary>A record was released (Destroyed is set): its list drops it on the next read.</summary>
        public static void Remove(NObject r)
        {
            switch (r)
            {
                case NCollider2D: dirty |= 1; break;
                case NSpriteRenderer: dirty |= 2; break;
                case NCanvas: dirty |= 4; break;
                case NCamera: dirty |= 8; break;
                case NBehaviour: dirty |= 16; break;
                case NGameObject: dirty |= 32; break;
                case NRigidbody2D: NTransform.HierarchyVersion++; break;
            }
        }
    }
}
