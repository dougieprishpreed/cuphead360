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
        /// <summary>In AssetUnload's pass: its engine references are weak until the collection is over.</summary>
        public bool UnloadCandidate;
        /// <summary>The host's number for it (the console host's texture id): -1 not asked yet, -2 none.</summary>
        public int HostId = -1;
    }

    public sealed class NGameObject : NObject
    {
        public readonly List<NComponent> Components = new List<NComponent>();
        public NTransform Transform;
        /// <summary>A collider here was indexed for physics queries: its transform's changes move it there.</summary>
        public bool HasColliders;
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
        /// <summary>The component itself turned on or off (Behaviour.enabled) on an active object; Activated by default.</summary>
        public virtual void Toggled(bool on) => Activated(on);
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
        // UiAlpha.ResetBelow's answer and when it was worked out.
        public int UiResetAt = -1, UiResetUi = -1;
        public bool UiResetBelow;

        public void Invalidate()
        {
            HasChanged = true;
            if (!worldValid) return;
            worldValid = false;
            WorldVersion++;
            // Colliders here move in the physics queries' index (an invalid transform's children are
            // invalid too, so the first change after a query is always seen).
            if (GameObject != null && GameObject.HasColliders) QueryIndex.Moved(GameObject);
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

        /// <summary>An asset AssetUnload found unreferenced: its managed object is already collected.</summary>
        public static void Unloaded(NObject rec)
        {
            if (rec.Destroyed) return;
            Releases++;
            if (rec is NComponent c) { try { c.Removed(); } catch (Exception) { } }
            rec.Destroyed = true;
            if (rec.Block != IntPtr.Zero) { NativeMemory.Free((void*)rec.Block); rec.Block = IntPtr.Zero; }
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

        /// <summary>Live objects by kind (loaded assets, scene objects) and type, the most numerous first.</summary>
        public static string Census(int top)
        {
            var count = new Dictionary<string, int>();
            int assets = 0, scene = 0;
            for (int i = 1; i < table.Count; i++)
            {
                var r = table[i];
                if (r == null) continue;
                if (r.Persistent) assets++; else scene++;
                string key = (r.Persistent ? "asset " : "") + (r.Managed?.GetType().Name ?? r.GetType().Name);
                count.TryGetValue(key, out int n);
                count[key] = n + 1;
            }
            var list = new List<KeyValuePair<string, int>>(count);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var sb = new System.Text.StringBuilder($"OBJECTS {assets} assets, {scene} scene/run-time:");
            for (int i = 0; i < list.Count && i < top; i++) sb.Append(i == 0 ? " " : ", ").Append(list[i].Key).Append(' ').Append(list[i].Value);
            return sb.ToString();
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
            // Locals, not two arrays: transform.position on a child inverts its parent's matrix (a new
            // pair of arrays per write, and each array element a load-hit-store on the console).
            float a0 = m.m00, a1 = m.m10, a2 = m.m20, a3 = m.m30, a4 = m.m01, a5 = m.m11, a6 = m.m21, a7 = m.m31;
            float a8 = m.m02, a9 = m.m12, a10 = m.m22, a11 = m.m32, a12 = m.m03, a13 = m.m13, a14 = m.m23, a15 = m.m33;
            float inv0 = a5 * a10 * a15 - a5 * a11 * a14 - a9 * a6 * a15 + a9 * a7 * a14 + a13 * a6 * a11 - a13 * a7 * a10;
            float inv4 = -a4 * a10 * a15 + a4 * a11 * a14 + a8 * a6 * a15 - a8 * a7 * a14 - a12 * a6 * a11 + a12 * a7 * a10;
            float inv8 = a4 * a9 * a15 - a4 * a11 * a13 - a8 * a5 * a15 + a8 * a7 * a13 + a12 * a5 * a11 - a12 * a7 * a9;
            float inv12 = -a4 * a9 * a14 + a4 * a10 * a13 + a8 * a5 * a14 - a8 * a6 * a13 - a12 * a5 * a10 + a12 * a6 * a9;
            float inv1 = -a1 * a10 * a15 + a1 * a11 * a14 + a9 * a2 * a15 - a9 * a3 * a14 - a13 * a2 * a11 + a13 * a3 * a10;
            float inv5 = a0 * a10 * a15 - a0 * a11 * a14 - a8 * a2 * a15 + a8 * a3 * a14 + a12 * a2 * a11 - a12 * a3 * a10;
            float inv9 = -a0 * a9 * a15 + a0 * a11 * a13 + a8 * a1 * a15 - a8 * a3 * a13 - a12 * a1 * a11 + a12 * a3 * a9;
            float inv13 = a0 * a9 * a14 - a0 * a10 * a13 - a8 * a1 * a14 + a8 * a2 * a13 + a12 * a1 * a10 - a12 * a2 * a9;
            float inv2 = a1 * a6 * a15 - a1 * a7 * a14 - a5 * a2 * a15 + a5 * a3 * a14 + a13 * a2 * a7 - a13 * a3 * a6;
            float inv6 = -a0 * a6 * a15 + a0 * a7 * a14 + a4 * a2 * a15 - a4 * a3 * a14 - a12 * a2 * a7 + a12 * a3 * a6;
            float inv10 = a0 * a5 * a15 - a0 * a7 * a13 - a4 * a1 * a15 + a4 * a3 * a13 + a12 * a1 * a7 - a12 * a3 * a5;
            float inv14 = -a0 * a5 * a14 + a0 * a6 * a13 + a4 * a1 * a14 - a4 * a2 * a13 - a12 * a1 * a6 + a12 * a2 * a5;
            float inv3 = -a1 * a6 * a11 + a1 * a7 * a10 + a5 * a2 * a11 - a5 * a3 * a10 - a9 * a2 * a7 + a9 * a3 * a6;
            float inv7 = a0 * a6 * a11 - a0 * a7 * a10 - a4 * a2 * a11 + a4 * a3 * a10 + a8 * a2 * a7 - a8 * a3 * a6;
            float inv11 = -a0 * a5 * a11 + a0 * a7 * a9 + a4 * a1 * a11 - a4 * a3 * a9 - a8 * a1 * a7 + a8 * a3 * a5;
            float inv15 = a0 * a5 * a10 - a0 * a6 * a9 - a4 * a1 * a10 + a4 * a2 * a9 + a8 * a1 * a6 - a8 * a2 * a5;
            float det = a0 * inv0 + a1 * inv4 + a2 * inv8 + a3 * inv12;
            r = Matrix4x4.identity;
            if (det == 0f) { r = Matrix4x4.zero; return false; }
            det = 1f / det;
            r.m00 = inv0 * det; r.m10 = inv1 * det; r.m20 = inv2 * det; r.m30 = inv3 * det;
            r.m01 = inv4 * det; r.m11 = inv5 * det; r.m21 = inv6 * det; r.m31 = inv7 * det;
            r.m02 = inv8 * det; r.m12 = inv9 * det; r.m22 = inv10 * det; r.m32 = inv11 * det;
            r.m03 = inv12 * det; r.m13 = inv13 * det; r.m23 = inv14 * det; r.m33 = inv15 * det;
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
        static readonly List<NSpriteMask> spriteMasks = new List<NSpriteMask>();
        static readonly List<NMeshRenderer> meshRenderers = new List<NMeshRenderer>();
        static int dirty;   // bit per list holding destroyed records
        static readonly int[] dead = new int[8];   // destroyed records per list since it was last cleaned

        public static List<NCollider2D> Colliders => Clean(colliders, 1);
        public static List<NSpriteRenderer> SpriteRenderers => Clean(spriteRenderers, 2);
        public static List<NCanvas> Canvases => Clean(canvases, 4);
        public static List<NCamera> Cameras => Clean(cameras, 8);
        public static List<NBehaviour> Behaviours => Clean(behaviours, 16);
        public static List<NGameObject> GameObjects => Clean(gameObjects, 32);
        public static List<NSpriteMask> SpriteMasks => Clean(spriteMasks, 64);
        public static List<NMeshRenderer> MeshRenderers => Clean(meshRenderers, 128);

        static List<T> Clean<T>(List<T> list, int bit) where T : NObject
        {
            if ((dirty & bit) != 0)
            {
                dirty &= ~bit;
                dead[System.Numerics.BitOperations.TrailingZeroCount(bit)] = 0;
                list.RemoveAll(x => x.Destroyed);
            }
            return list;
        }

        /// <summary>
        /// At a frame's start (no list is being walked): lists that gathered many destroyed records drop
        /// them. They were dropped only when read, and the GameObject and sprite renderer lists are read
        /// rarely, so every destroyed scene's objects stayed reachable through them with everything they
        /// held: after six DLC and Inkwell Hell levels 56,000 destroyed records (the UI's rects, the
        /// bullets) and the heap 30 MB larger on the same map.
        /// </summary>
        public static void Sweep()
        {
            if (dirty == 0) return;
            SweepList(colliders, 1);
            SweepList(spriteRenderers, 2);
            SweepList(canvases, 4);
            SweepList(cameras, 8);
            SweepList(behaviours, 16);
            SweepList(gameObjects, 32);
            SweepList(spriteMasks, 64);
            SweepList(meshRenderers, 128);
        }

        // 256 destroyed records, or half of a short list (the sprite masks': 50 held 2,000 records)
        static void SweepList<T>(List<T> list, int bit) where T : NObject
        {
            int n = dead[System.Numerics.BitOperations.TrailingZeroCount(bit)];
            if (n >= 256 || (n > 0 && n * 2 >= list.Count)) Clean(list, bit);
        }

        public static void Add(NObject r)
        {
            switch (r)
            {
                case NCollider2D c: colliders.Add(c); QueryIndex.Added(c); break;
                case NSpriteRenderer s: spriteRenderers.Add(s); break;
                case NCanvas cv: canvases.Add(cv); break;
                case NCamera cam: cameras.Add(cam); break;
                case NBehaviour b: behaviours.Add(b); break;
                case NGameObject g: gameObjects.Add(g); break;
                case NSpriteMask m: spriteMasks.Add(m); break;
                case NMeshRenderer mr: meshRenderers.Add(mr); break;
                // (every caller adds the record to its GameObject's components right after, with no query between)
                case NRigidbody2D: NTransform.HierarchyVersion++; break;
            }
        }

        /// <summary>A record was released (Destroyed is set): its list drops it on the next read.</summary>
        public static void Remove(NObject r)
        {
            switch (r)
            {
                case NCollider2D c: dirty |= 1; dead[0]++; QueryIndex.Dirty(c); break;
                case NSpriteRenderer: dirty |= 2; dead[1]++; break;
                case NCanvas: dirty |= 4; dead[2]++; break;
                case NCamera: dirty |= 8; dead[3]++; break;
                case NBehaviour: dirty |= 16; dead[4]++; break;
                case NGameObject: dirty |= 32; dead[5]++; break;
                case NSpriteMask: dirty |= 64; dead[6]++; break;
                case NMeshRenderer: dirty |= 128; dead[7]++; break;
                case NRigidbody2D: NTransform.HierarchyVersion++; break;
            }
        }
    }
}
